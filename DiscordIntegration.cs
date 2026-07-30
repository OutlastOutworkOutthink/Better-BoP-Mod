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
/// Adds the league account-link action to the player's profile. The game
/// supplies its already-authenticated Polytopia identity; Discord authentication
/// happens in the player's browser and no game or Steam password is collected.
/// </summary>
[HarmonyPatch]
internal static class DiscordIntegrationPatch
{
    internal const string ApiBaseUrl = "https://polyeconomic-bot-production.up.railway.app";
    internal const string ModVersion = "0.4.8";
    internal const string IntegrationTokenKey = "polyeconomic.integration.token";
    internal const string LinkedAccountIdKey = "polyeconomic.integration.account_id";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static ManualLogSource logger = null!;
    private static SynchronizationContext? mainThread;
    private static UIRoundButton_UI2? linkButton;
    private static bool requestInFlight;

    internal static void Initialize(ManualLogSource logSource)
    {
        logger = logSource;
        mainThread = SynchronizationContext.Current;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.Start))]
    private static void ProfileScreen_Start(ProfileScreen __instance)
    {
        try
        {
            if (linkButton != null)
            {
                UnityEngine.Object.Destroy(linkButton.gameObject);
            }

            linkButton = UILibrary.NewRoundButton(__instance.rectTransform)
                .SetStyle(UIButtonBase_UI2.ButtonStyle.Suggested);
            linkButton.SetButtonSize(UIRoundButton_UI2.ButtonSize.ExtraLarge);
            linkButton.gameObject.SetActive(true);
            UpdateButtonForCurrentAccount();
            TakeClickOwnership();
            PositionButton(__instance);
            logger.LogInfo("Created Connect Discord profile button and replaced inherited callbacks.");
        }
        catch (Exception exception)
        {
            logger.LogError($"Could not create the Discord link button: {exception}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.OnScreenUpdated))]
    private static void ProfileScreen_OnScreenUpdated(ProfileScreen __instance)
    {
        // Some profile refresh paths rebuild UI callback state. Reassert both
        // ownership and layout so the stock logfile action can never return.
        TakeClickOwnership();
        ConfigureLinkButtonVisuals();
        PositionButton(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIRoundButton_UI2), nameof(UIRoundButton_UI2.OnPointerClick))]
    private static bool InterceptConnectDiscordPointerClick(UIRoundButton_UI2 __instance)
    {
        if (linkButton == null || __instance.Pointer != linkButton.Pointer)
        {
            return true;
        }

        // This hard interception is deliberately independent of the prefab's
        // callbacks. Even if another mod restores the logfile handler later,
        // this exact control can only start Discord linking.
        logger.LogMessage("Connect Discord pointer click intercepted by Better BoP.");
        BeginDiscordLink();
        return false;
    }

    private static void PositionButton(ProfileScreen profile)
    {
        if (linkButton == null)
        {
            return;
        }

        RectTransform linkTransform = linkButton.rectTransform;
        linkTransform.anchorMin = Vector2.one;
        linkTransform.anchorMax = Vector2.one;
        linkTransform.pivot = Vector2.one;
        linkTransform.anchoredPosition = new Vector2(-48f, -48f);
        linkTransform.SetAsLastSibling();
    }

    private static void UpdateButtonForCurrentAccount()
    {
        if (linkButton == null)
        {
            return;
        }

        string currentAccountId = AccountManager.PlayerAccountId.ToString();
        string linkedAccountId = PlayerPrefs.GetString(LinkedAccountIdKey, string.Empty);
        SetButtonText(!string.IsNullOrWhiteSpace(linkedAccountId) && linkedAccountId == currentAccountId
            ? "Check Games"
            : "Connect Discord");
    }

    private static void BeginDiscordLink()
    {
        if (requestInFlight)
        {
            return;
        }

        try
        {
            logger.LogMessage("Connect Discord clicked; creating an account-link session.");
            string accountId = AccountManager.PlayerAccountId.ToString();
            string linkedAccountId = PlayerPrefs.GetString(LinkedAccountIdKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(linkedAccountId) && linkedAccountId == accountId)
            {
                SetButtonText("Checking...");
                _ = IntegratedMultiplayer.CheckForAssignedGameAsync(true);
                return;
            }
            string displayName = AccountManager.Alias?.Trim() ?? string.Empty;
            string friendCode = AccountManager.UserModel?.FriendCode?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(accountId) ||
                accountId == "00000000-0000-0000-0000-000000000000" ||
                string.IsNullOrWhiteSpace(displayName))
            {
                SetButtonText("Sign in first");
                return;
            }

            requestInFlight = true;
            SetButtonText("Connecting...");
            _ = CreateLinkSessionAsync(accountId, displayName, friendCode);
        }
        catch (Exception exception)
        {
            requestInFlight = false;
            logger.LogError($"Could not begin Discord linking: {exception}");
            SetButtonText("Link failed - retry");
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

            LinkSessionResponse? linkSession = JsonSerializer.Deserialize<LinkSessionResponse>(responseBody);
            if (linkSession == null ||
                string.IsNullOrWhiteSpace(linkSession.LinkUrl) ||
                string.IsNullOrWhiteSpace(linkSession.StatusUrl) ||
                string.IsNullOrWhiteSpace(linkSession.IntegrationToken))
            {
                throw new InvalidOperationException("Link service returned an incomplete response.");
            }

            RunOnMainThread(() =>
            {
                SetButtonText("Finish in browser");
                OpenBrowser(linkSession.LinkUrl);
            });

            bool completed = await WaitForCompletionAsync(linkSession.StatusUrl).ConfigureAwait(false);
            RunOnMainThread(() =>
            {
                if (completed)
                {
                    PlayerPrefs.SetString(IntegrationTokenKey, linkSession.IntegrationToken);
                    PlayerPrefs.SetString(LinkedAccountIdKey, accountId);
                    PlayerPrefs.Save();
                    SetButtonText("Check Games");
                    IntegratedMultiplayer.Wake();
                    logger.LogMessage($"Linked Polytopia account {accountId} for Better Battle of Polytopia Mod multiplayer.");
                }
                else
                {
                    SetButtonText("Connect Discord");
                }
            });
        }
        catch (Exception exception)
        {
            logger.LogError($"Discord linking failed: {exception}");
            RunOnMainThread(() => SetButtonText("Link failed - retry"));
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
                string statusBody = await HttpClient.GetStringAsync(statusUrl).ConfigureAwait(false);
                LinkStatusResponse? status = JsonSerializer.Deserialize<LinkStatusResponse>(statusBody);
                if (status?.Completed == true)
                {
                    return true;
                }
                if (status?.Expired == true)
                {
                    return false;
                }
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
        // Always copy the single-use URL. This gives the player a recovery path
        // even on machines where every automatic browser handoff is blocked.
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
                    logger.LogInfo("Opened Discord link with the Windows shell.");
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
            logger.LogInfo("Requested Discord link through Polytopia's native URL helper.");
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Native browser helper failed: {exception.Message}");
        }

        try
        {
            Application.OpenURL(url);
            logger.LogInfo("Requested Discord link through Unity OpenURL.");
        }
        catch (Exception exception)
        {
            logger.LogError($"Every browser handoff failed; link remains copied to clipboard: {exception}");
            ShowLinkCopiedPopup();
        }
    }

    private static void ShowLinkCopiedPopup()
    {
        BasicPopup popup = PopupManager.GetBasicPopup()
            .SetHeader("Connect Discord")
            .SetDescription("The browser could not be opened automatically. The secure Discord link has been copied. Paste it into your browser to continue.");
        popup.SetMainButton(
            "Link copied",
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => { })
        );
        popup.Show();
    }

    internal static void RunOnMainThread(Action action)
    {
        if (mainThread == null || SynchronizationContext.Current == mainThread)
        {
            action();
            return;
        }

        mainThread.Post(_ => action(), null);
    }

    internal static void SetButtonText(string text)
    {
        if (linkButton != null)
        {
            linkButton.Text = text;
            // RunLayout resets round buttons to their stock circular geometry,
            // so always apply our pill geometry after it runs.
            linkButton.UpdateLabelVisibility();
            linkButton.RunLayout();
            ConfigureLinkButtonVisuals();
        }
    }

    private static void TakeClickOwnership()
    {
        if (linkButton == null)
        {
            return;
        }

        // UIRoundButton_UI2 has both an OnClicked delegate and an
        // OnClickedSignal. Clearing only the signal leaves callbacks inherited
        // from the source prefab, which is why the logfile popup was opening.
        linkButton.ClearCallbacks();
        linkButton.ButtonEnabled = true;
        linkButton.buttonEnabled = true;
        linkButton.blockClick = false;
        linkButton.eatClickAction = false;
        linkButton.OnClickedSignal.Add(
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(BeginDiscordLink)
        );
    }

    private static void ConfigureLinkButtonVisuals()
    {
        if (linkButton == null)
        {
            return;
        }

        const float width = 320f;
        const float height = 88f;
        linkButton.SetSize(width, height);
        linkButton.rectTransform.sizeDelta = new Vector2(width, height);

        // UI_DISCORD is not present in every PC sprite atlas. A permanent text
        // label avoids the question-mark fallback and clearly states the action.
        if (linkButton.iconContainer != null)
        {
            linkButton.iconContainer.gameObject.SetActive(false);
        }
        if (linkButton.icon != null)
        {
            linkButton.icon.gameObject.SetActive(false);
        }
        if (linkButton.bg != null)
        {
            linkButton.bg.gameObject.SetActive(true);
            linkButton.bg.raycastTarget = true;
            Stretch(linkButton.bg.rectTransform);
        }
        if (linkButton.outline != null)
        {
            Stretch(linkButton.outline.rectTransform);
        }
        if (linkButton.textBg != null)
        {
            linkButton.textBg.gameObject.SetActive(true);
            Stretch(linkButton.textBg);
        }
        if (linkButton.titleTextField != null)
        {
            linkButton.titleTextField.gameObject.SetActive(true);
            RectTransform label = linkButton.titleTextField.rectTransform;
            label.anchorMin = Vector2.zero;
            label.anchorMax = Vector2.one;
            label.pivot = new Vector2(0.5f, 0.5f);
            label.offsetMin = new Vector2(18f, 8f);
            label.offsetMax = new Vector2(-18f, -8f);
            linkButton.titleTextField.textField.fontSize = 20f;
            linkButton.titleTextField.textField.alignment = TMPro.TextAlignmentOptions.Center;
            linkButton.titleTextField.textField.enableWordWrapping = false;
            linkButton.titleTextField.textField.raycastTarget = false;
        }
    }

    private static void Stretch(RectTransform transform)
    {
        transform.anchorMin = Vector2.zero;
        transform.anchorMax = Vector2.one;
        transform.pivot = new Vector2(0.5f, 0.5f);
        transform.offsetMin = Vector2.zero;
        transform.offsetMax = Vector2.zero;
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
