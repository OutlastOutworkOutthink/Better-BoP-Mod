using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace BetterBoPMod;

/// <summary>
/// One-purpose profile integration: create a Discord OAuth link session, open
/// it in the player's browser, and remember the completed account link. This
/// file deliberately contains no multiplayer or gameplay patches.
/// </summary>
[HarmonyPatch]
internal static class DiscordAccountLink
{
    internal const string ApiBaseUrl = "https://polyeconomic-bot-production.up.railway.app";
    internal const string ModVersion = "0.5.7";
    internal const string IntegrationTokenKey = "polyeconomic.integration.token";
    internal const string LinkedAccountIdKey = "polyeconomic.integration.account_id";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static ManualLogSource logger = null!;
    private static SynchronizationContext? mainThread;
    private static ProfileScreen? owner;
    private static UIRoundButton_UI2? linkButton;
    private static bool requestInFlight;

    internal static void Initialize(ManualLogSource logSource)
    {
        logger = logSource;
        mainThread = SynchronizationContext.Current;
    }

    // ProfileScreen is rebuilt and refreshed through several separate paths.
    // Reasserting the button from all of them prevents the control from going
    // missing after returning from another screen, refreshing the account,
    // changing language, or receiving a late backend profile update.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.Start))]
    private static void ProfileScreen_Start(ProfileScreen __instance) =>
        EnsureButton(__instance, "Start");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.OnEnable))]
    private static void ProfileScreen_OnEnable(ProfileScreen __instance) =>
        EnsureButton(__instance, "OnEnable");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.UpdateValues))]
    private static void ProfileScreen_UpdateValues(ProfileScreen __instance) =>
        EnsureButton(__instance, "UpdateValues");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.OnScreenUpdated))]
    private static void ProfileScreen_OnScreenUpdated(ProfileScreen __instance) =>
        EnsureButton(__instance, "OnScreenUpdated");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.OnRefreshUser))]
    private static void ProfileScreen_OnRefreshUser(ProfileScreen __instance) =>
        EnsureButton(__instance, "OnRefreshUser");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.OnLanguageChanged))]
    private static void ProfileScreen_OnLanguageChanged(ProfileScreen __instance) =>
        EnsureButton(__instance, "OnLanguageChanged");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIRoundButton_UI2), nameof(UIRoundButton_UI2.OnPointerClick))]
    private static bool InterceptPointerClick(UIRoundButton_UI2 __instance)
    {
        if (!IsLinkButton(__instance)) return true;

        logger.LogMessage("Connect Discord pointer click intercepted by Better BoP.");
        BeginDiscordLink();
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIButtonBase_UI2), nameof(UIButtonBase_UI2.OnButtonClicked))]
    private static bool InterceptControllerClick(UIButtonBase_UI2 __instance)
    {
        if (!IsLinkButton(__instance)) return true;

        logger.LogMessage("Connect Discord controller/keyboard click intercepted by Better BoP.");
        BeginDiscordLink();
        return false;
    }

    private static bool IsLinkButton(UIButtonBase_UI2 candidate) =>
        linkButton != null && candidate.Pointer == linkButton.Pointer;

    private static void EnsureButton(ProfileScreen profile, string lifecycle)
    {
        try
        {
            mainThread ??= SynchronizationContext.Current;

            if (!ButtonBelongsTo(profile))
            {
                DestroyButton();
                owner = profile;
                linkButton = UILibrary.NewRoundButton(profile.rectTransform)
                    .SetStyle(UIButtonBase_UI2.ButtonStyle.Delete)
                    .SetButtonSize(UIRoundButton_UI2.ButtonSize.Regular);
                linkButton.gameObject.name = "BetterBoP_ConnectDiscord";
                linkButton.gameObject.SetActive(true);
                logger.LogInfo($"Created red Connect Discord profile button during {lifecycle}.");
            }

            ConfigureButton(profile);
        }
        catch (Exception exception)
        {
            // A later lifecycle callback will retry. Do not let an optional
            // account-link control interrupt the stock profile screen.
            logger.LogWarning($"Connect Discord button retry needed after {lifecycle}: {exception.Message}");
        }
    }

    private static bool ButtonBelongsTo(ProfileScreen profile)
    {
        try
        {
            return owner != null &&
                   owner.Pointer == profile.Pointer &&
                   linkButton != null &&
                   linkButton.gameObject != null &&
                   linkButton.rectTransform.parent == profile.rectTransform;
        }
        catch
        {
            return false;
        }
    }

    private static void DestroyButton()
    {
        try
        {
            if (linkButton != null && linkButton.gameObject != null)
            {
                UnityEngine.Object.Destroy(linkButton.gameObject);
            }
        }
        catch
        {
            // A destroyed Il2Cpp wrapper can throw while checking its object.
        }

        linkButton = null;
        owner = null;
    }

    private static void ConfigureButton(ProfileScreen profile)
    {
        if (linkButton == null) return;

        TakeClickOwnership();
        linkButton.SetStyle(UIButtonBase_UI2.ButtonStyle.Delete);
        linkButton.SetButtonSize(UIRoundButton_UI2.ButtonSize.Regular);
        linkButton.Text = CurrentButtonText();
        linkButton.UpdateLabelVisibility();
        linkButton.RunLayout();

        // Keep the stock round geometry. The Delete style is Polytopia's red
        // button style; placing it beside the back control makes it visible in
        // the top-left without covering the profile information.
        RectTransform transform = linkButton.rectTransform;
        transform.anchorMin = new Vector2(0f, 1f);
        transform.anchorMax = new Vector2(0f, 1f);
        transform.pivot = new Vector2(0f, 1f);
        transform.anchoredPosition = new Vector2(132f, -42f);
        transform.SetAsLastSibling();
        linkButton.gameObject.SetActive(profile.gameObject.activeInHierarchy);
    }

    private static string CurrentButtonText()
    {
        if (requestInFlight) return "Connecting...";

        string currentAccountId = AccountManager.PlayerAccountId.ToString();
        string linkedAccountId = PlayerPrefs.GetString(LinkedAccountIdKey, string.Empty);
        return !string.IsNullOrWhiteSpace(linkedAccountId) && linkedAccountId == currentAccountId
            ? "Discord Connected"
            : "Connect Discord";
    }

    private static void TakeClickOwnership()
    {
        if (linkButton == null) return;

        linkButton.ClearCallbacks();
        linkButton.ButtonEnabled = true;
        linkButton.buttonEnabled = true;
        linkButton.blockClick = false;
        linkButton.eatClickAction = false;
        linkButton.OnClickedSignal.Add(
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(BeginDiscordLink)
        );
    }

    private static void BeginDiscordLink()
    {
        if (requestInFlight) return;

        try
        {
            string accountId = AccountManager.PlayerAccountId.ToString();
            string displayName = AccountManager.Alias?.Trim() ?? string.Empty;
            string friendCode = AccountManager.UserModel?.FriendCode?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(accountId) ||
                accountId == "00000000-0000-0000-0000-000000000000" ||
                string.IsNullOrWhiteSpace(displayName))
            {
                SetButtonText("Sign in first");
                ShowPopup(
                    "Connect Discord",
                    "Sign in to your Polytopia profile first, then press Connect Discord again."
                );
                return;
            }

            requestInFlight = true;
            SetButtonText("Connecting...");
            logger.LogMessage($"Creating Discord link session for Polytopia account {accountId}.");
            _ = CreateLinkSessionAsync(accountId, displayName, friendCode);
        }
        catch (Exception exception)
        {
            requestInFlight = false;
            logger.LogError($"Could not begin Discord linking: {exception}");
            SetButtonText("Retry Discord");
        }
    }

    private static async Task CreateLinkSessionAsync(
        string accountId,
        string displayName,
        string friendCode
    )
    {
        try
        {
            string body = JsonSerializer.Serialize(new LinkSessionRequest
            {
                PolytopiaAccountId = accountId,
                DisplayName = displayName,
                FriendCode = friendCode,
                GameVersion = Application.version,
                ModVersion = ModVersion,
            });

            using HttpResponseMessage response = await HttpClient.PostAsync(
                $"{ApiBaseUrl}/api/integrations/polytopia/link-sessions",
                new StringContent(body, Encoding.UTF8, "application/json")
            ).ConfigureAwait(false);
            string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Link service returned {(int)response.StatusCode}: {responseBody}"
                );
            }

            LinkSessionResponse? session = JsonSerializer.Deserialize<LinkSessionResponse>(responseBody);
            if (session == null ||
                string.IsNullOrWhiteSpace(session.LinkUrl) ||
                string.IsNullOrWhiteSpace(session.StatusUrl) ||
                string.IsNullOrWhiteSpace(session.IntegrationToken))
            {
                throw new InvalidOperationException("Link service returned an incomplete response.");
            }

            RunOnMainThread(() =>
            {
                SetButtonText("Finish in browser");
                OpenBrowser(session.LinkUrl);
            });

            bool completed = await WaitForCompletionAsync(session.StatusUrl).ConfigureAwait(false);
            RunOnMainThread(() =>
            {
                if (completed)
                {
                    PlayerPrefs.SetString(IntegrationTokenKey, session.IntegrationToken);
                    PlayerPrefs.SetString(LinkedAccountIdKey, accountId);
                    PlayerPrefs.Save();
                    SetButtonText("Discord Connected");
                    ShowPopup(
                        "Discord Connected",
                        $"{displayName} is now registered with the PolyEconomic Bot."
                    );
                    logger.LogMessage($"Discord integration completed for Polytopia account {accountId}.");
                }
                else
                {
                    SetButtonText("Connect Discord");
                    ShowPopup(
                        "Discord Link Expired",
                        "The sign-in was not completed in time. Press Connect Discord to try again."
                    );
                }
            });
        }
        catch (Exception exception)
        {
            logger.LogError($"Discord linking failed: {exception}");
            RunOnMainThread(() =>
            {
                SetButtonText("Retry Discord");
                ShowPopup(
                    "Discord Link Failed",
                    "The bot could not start Discord sign-in. Check your connection and try again."
                );
            });
        }
        finally
        {
            requestInFlight = false;
        }
    }

    private static async Task<bool> WaitForCompletionAsync(string statusUrl)
    {
        for (int attempt = 0; attempt < 90; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            try
            {
                using HttpResponseMessage response = await HttpClient.GetAsync(statusUrl).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                LinkStatusResponse? status = JsonSerializer.Deserialize<LinkStatusResponse>(body);
                if (status?.Completed == true) return true;
                if (status?.Expired == true) return false;
            }
            catch (Exception exception)
            {
                logger.LogWarning($"Could not check Discord link status: {exception.Message}");
            }
        }

        return false;
    }

    private static void OpenBrowser(string url)
    {
        // Always provide a recovery route even when a platform blocks automatic
        // browser handoff.
        GUIUtility.systemCopyBuffer = url;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                Process? browser = Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                if (browser != null)
                {
                    logger.LogInfo("Opened Discord OAuth through the Windows shell.");
                    return;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning($"Windows browser handoff failed: {exception.Message}");
            }
        }

        try
        {
            NativeHelpers.OpenURL(url, false);
            logger.LogInfo("Opened Discord OAuth through Polytopia's URL helper.");
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Polytopia URL helper failed: {exception.Message}");
        }

        try
        {
            Application.OpenURL(url);
            logger.LogInfo("Opened Discord OAuth through Unity OpenURL.");
        }
        catch (Exception exception)
        {
            logger.LogError($"Automatic browser handoff failed: {exception}");
            ShowPopup(
                "Connect Discord",
                "The secure Discord link was copied. Paste it into your browser to continue."
            );
        }
    }

    private static void RunOnMainThread(Action action)
    {
        if (mainThread == null || SynchronizationContext.Current == mainThread)
        {
            action();
            return;
        }

        mainThread.Post(_ => action(), null);
    }

    private static void SetButtonText(string text)
    {
        if (linkButton == null) return;

        linkButton.Text = text;
        linkButton.UpdateLabelVisibility();
        linkButton.RunLayout();
        linkButton.SetStyle(UIButtonBase_UI2.ButtonStyle.Delete);
        linkButton.SetButtonSize(UIRoundButton_UI2.ButtonSize.Regular);
    }

    private static void ShowPopup(string title, string message)
    {
        BasicPopup popup = PopupManager.GetBasicPopup()
            .SetHeader(title)
            .SetDescription(message);
        popup.SetMainButton(
            "OK",
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => { })
        );
        popup.Show();
    }

    private sealed class LinkSessionRequest
    {
        [JsonPropertyName("polytopiaAccountId")]
        public string PolytopiaAccountId { get; init; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; init; } = string.Empty;

        [JsonPropertyName("friendCode")]
        public string FriendCode { get; init; } = string.Empty;

        [JsonPropertyName("gameVersion")]
        public string GameVersion { get; init; } = string.Empty;

        [JsonPropertyName("modVersion")]
        public string ModVersion { get; init; } = string.Empty;
    }

    private sealed class LinkSessionResponse
    {
        [JsonPropertyName("linkUrl")]
        public string LinkUrl { get; init; } = string.Empty;

        [JsonPropertyName("statusUrl")]
        public string StatusUrl { get; init; } = string.Empty;

        [JsonPropertyName("integrationToken")]
        public string IntegrationToken { get; init; } = string.Empty;
    }

    private sealed class LinkStatusResponse
    {
        [JsonPropertyName("completed")]
        public bool Completed { get; init; }

        [JsonPropertyName("expired")]
        public bool Expired { get; init; }
    }
}
