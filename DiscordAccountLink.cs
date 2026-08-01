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
internal static class DiscordAccountLink
{
    internal const string ApiBaseUrl = "https://polyeconomic-bot-production.up.railway.app";
    // Alpha 0.5.15 changes only client UI timing. It deliberately speaks the
    // unchanged 0.5.14 multiplayer ruleset/protocol to the live server.
    internal const string ModVersion = "0.5.14";
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
    private static bool applyingVisuals;

    internal static void Initialize(ManualLogSource logSource)
    {
        logger = logSource;
        mainThread = SynchronizationContext.Current;
    }

    internal static bool InterceptControllerClick(UIButtonBase_UI2 instance)
    {
        if (!IsLinkButton(instance)) return true;

        logger.LogMessage("Connect Discord controller/keyboard click intercepted by Better BoP.");
        BeginDiscordLink();
        return false;
    }

    internal static bool IsLinkButton(UIButtonBase_UI2 candidate) =>
        linkButton != null && candidate.Pointer == linkButton.Pointer;

    internal static void EnsureButton(ProfileScreen profile, string lifecycle)
    {
        try
        {
            mainThread ??= SynchronizationContext.Current;
            owner = profile;

            if (!ButtonBelongsTo(profile))
            {
                DestroyButton();
                owner = profile;
                linkButton = UILibrary.NewRoundButton(profile.rectTransform)
                    .SetStyle(UIButtonBase_UI2.ButtonStyle.Delete)
                    .SetButtonSize(UIRoundButton_UI2.ButtonSize.ExtraLarge);
                linkButton.gameObject.name = "BetterBoP_ConnectDiscord";
                linkButton.gameObject.SetActive(true);
                logger.LogInfo($"Created top-right Connect Discord profile button during {lifecycle}.");
            }

            ConfigureButton();
        }
        catch (Exception exception)
        {
            // A later lifecycle callback will retry. Do not let an optional
            // account-link control interrupt the stock profile screen.
            logger.LogWarning($"Connect Discord button retry needed after {lifecycle}: {exception.Message}");
        }
    }

    internal static void RetryOwnedButton(string lifecycle)
    {
        try
        {
            if (owner != null) EnsureButton(owner, lifecycle);
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Connect Discord owned-button retry failed after {lifecycle}: {exception.Message}");
        }
    }

    internal static void ReassertAfterRoundButtonLayout(UIRoundButton_UI2 button)
    {
        if (!IsLinkButton(button)) return;

        try
        {
            ReassertButtonPosition();
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Could not reassert Connect Discord layout: {exception.Message}");
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

    private static void ConfigureButton()
    {
        if (linkButton == null) return;

        TakeClickOwnership();
        linkButton.SetButtonSize(UIRoundButton_UI2.ButtonSize.ExtraLarge);
        linkButton.Text = CurrentButtonText();
        linkButton.UpdateLabelVisibility();
        linkButton.RunLayout();
        ApplyPersistentVisuals();
    }

    private static void ApplyPersistentVisuals()
    {
        if (linkButton == null || applyingVisuals) return;

        applyingVisuals = true;
        try
        {
            linkButton.SetStyle(IsCurrentAccountLinked()
                ? UIButtonBase_UI2.ButtonStyle.Complete
                : UIButtonBase_UI2.ButtonStyle.Delete);
            ReassertButtonPosition();
        }
        finally
        {
            applyingVisuals = false;
        }
    }

    private static void ReassertButtonPosition()
    {
        if (linkButton == null) return;

        // Native layout can move custom buttons. Only restore geometry here;
        // never call SetStyle or SetButtonSize from a layout postfix.
        RectTransform transform = linkButton.rectTransform;
        transform.anchorMin = Vector2.one;
        transform.anchorMax = Vector2.one;
        transform.pivot = Vector2.one;
        transform.anchoredPosition = new Vector2(-48f, -48f);
        transform.localScale = Vector3.one;
        transform.localRotation = Quaternion.identity;
        transform.SetAsLastSibling();
        linkButton.gameObject.SetActive(true);

        if (linkButton.bg != null)
        {
            linkButton.bg.gameObject.SetActive(true);
            linkButton.bg.raycastTarget = true;
        }
        if (linkButton.outline != null) linkButton.outline.gameObject.SetActive(true);
    }

    private static string CurrentButtonText()
    {
        if (requestInFlight) return "Connecting...";

        return IsCurrentAccountLinked()
            ? "Discord Connected"
            : "Connect Discord";
    }

    private static bool IsCurrentAccountLinked()
    {
        string currentAccountId = AccountManager.PlayerAccountId.ToString();
        string linkedAccountId = PlayerPrefs.GetString(LinkedAccountIdKey, string.Empty);
        return !string.IsNullOrWhiteSpace(linkedAccountId) && linkedAccountId == currentAccountId;
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

        if (IsCurrentAccountLinked())
        {
            logger.LogInfo("Ignored Discord link request because this Polytopia account is already connected.");
            SetButtonText("Discord Connected");
            ShowPopup(
                "Discord Connected",
                "This Polytopia account is already connected to Discord. Each account can only be connected once."
            );
            return;
        }

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
                LinkErrorResponse? linkError = null;
                try
                {
                    linkError = JsonSerializer.Deserialize<LinkErrorResponse>(responseBody);
                }
                catch
                {
                    // Preserve the original HTTP diagnostic below when a
                    // proxy returns a non-JSON error page.
                }

                if ((int)response.StatusCode == 409 &&
                    linkError?.Error == "account_already_linked")
                {
                    logger.LogInfo($"Server confirmed Polytopia account {accountId} is already connected.");
                    RunOnMainThread(() =>
                    {
                        SetButtonText(IsCurrentAccountLinked()
                            ? "Discord Connected"
                            : "Already Connected");
                        ShowPopup(
                            "Discord Connected",
                            "This Polytopia account already has its one Discord connection. No new link or announcement was created."
                        );
                    });
                    return;
                }

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
                    logger.LogMessage($"Discord integration completed for Polytopia account {accountId}.");
                    if (Application.isFocused) SetButtonText("Discord Connected");
                }
                else
                {
                    if (Application.isFocused) SetButtonText("Connect Discord");
                }
            });
        }
        catch (Exception exception)
        {
            logger.LogError($"Discord linking failed: {exception}");
            RunOnMainThread(() =>
            {
                if (Application.isFocused) SetButtonText("Retry Discord");
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

    internal static void RunOnMainThread(Action action)
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
        ApplyPersistentVisuals();
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

    private sealed class LinkErrorResponse
    {
        [JsonPropertyName("error")]
        public string Error { get; init; } = string.Empty;
    }

    private sealed class LinkStatusResponse
    {
        [JsonPropertyName("completed")]
        public bool Completed { get; init; }

        [JsonPropertyName("expired")]
        public bool Expired { get; init; }
    }
}

// Every hook is independent. If a future Polytopia build changes one method,
// the remaining recovery paths still create and maintain the link control.
[HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.Start))]
internal static class DiscordProfileStartPatch
{
    [HarmonyPostfix]
    private static void Postfix(ProfileScreen __instance) =>
        DiscordAccountLink.EnsureButton(__instance, "Start");
}

[HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.OnEnable))]
internal static class DiscordProfileEnablePatch
{
    [HarmonyPostfix]
    private static void Postfix(ProfileScreen __instance) =>
        DiscordAccountLink.EnsureButton(__instance, "OnEnable");
}

[HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.UpdateValues))]
internal static class DiscordProfileValuesPatch
{
    [HarmonyPostfix]
    private static void Postfix(ProfileScreen __instance) =>
        DiscordAccountLink.EnsureButton(__instance, "UpdateValues");
}

[HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.OnScreenUpdated))]
internal static class DiscordProfileScreenUpdatedPatch
{
    [HarmonyPostfix]
    private static void Postfix(ProfileScreen __instance) =>
        DiscordAccountLink.EnsureButton(__instance, "OnScreenUpdated");
}

[HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.OnRefreshUser))]
internal static class DiscordProfileRefreshUserPatch
{
    [HarmonyPostfix]
    private static void Postfix(ProfileScreen __instance) =>
        DiscordAccountLink.EnsureButton(__instance, "OnRefreshUser");
}

[HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.OnLanguageChanged))]
internal static class DiscordProfileLanguagePatch
{
    [HarmonyPostfix]
    private static void Postfix(ProfileScreen __instance) =>
        DiscordAccountLink.EnsureButton(__instance, "OnLanguageChanged");
}

[HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.SubscribeButtonsEvents))]
internal static class DiscordProfileSubscribePatch
{
    [HarmonyPostfix]
    private static void Postfix(ProfileScreen __instance) =>
        DiscordAccountLink.EnsureButton(__instance, "SubscribeButtonsEvents");
}

[HarmonyPatch(typeof(ProfileScreen), nameof(ProfileScreen.OnRefresh))]
internal static class DiscordProfileRefreshPatch
{
    [HarmonyPostfix]
    private static void Postfix(ProfileScreen __instance) =>
        DiscordAccountLink.EnsureButton(__instance, "OnRefresh");
}

[HarmonyPatch(typeof(UILibrary), nameof(UILibrary.loadComplete))]
internal static class DiscordUILibraryReadyPatch
{
    [HarmonyPostfix]
    private static void Postfix() =>
        DiscordAccountLink.RetryOwnedButton("UILibrary.loadComplete");
}

[HarmonyPatch(typeof(UIRoundButton_UI2), nameof(UIRoundButton_UI2.RunLayout))]
internal static class DiscordRoundButtonLayoutPatch
{
    [HarmonyPostfix]
    private static void Postfix(UIRoundButton_UI2 __instance) =>
        DiscordAccountLink.ReassertAfterRoundButtonLayout(__instance);
}

[HarmonyPatch(typeof(UIRoundButton_UI2), nameof(UIRoundButton_UI2.OnEnable))]
internal static class DiscordRoundButtonEnablePatch
{
    [HarmonyPostfix]
    private static void Postfix(UIRoundButton_UI2 __instance) =>
        DiscordAccountLink.ReassertAfterRoundButtonLayout(__instance);
}

[HarmonyPatch(typeof(UIButtonBase_UI2), nameof(UIButtonBase_UI2.OnButtonClicked))]
internal static class DiscordControllerClickPatch
{
    [HarmonyPrefix]
    private static bool Prefix(UIButtonBase_UI2 __instance) =>
        DiscordAccountLink.InterceptControllerClick(__instance);
}
