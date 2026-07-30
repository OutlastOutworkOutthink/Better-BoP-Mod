using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
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
    internal const string ModVersion = "0.4.2";
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
                .SetStyle(UIButtonBase_UI2.ButtonStyle.Suggested)
                .SetSprite(SpriteRef.UI_DISCORD, 0.55f);
            linkButton.iconContainer.gameObject.SetActive(true);
            linkButton.icon.gameObject.SetActive(true);
            linkButton.icon.raycastTarget = false;
            linkButton.titleTextField.textField.raycastTarget = false;
            linkButton.titleTextField.textField.fontSize = 18f;
            linkButton.SetButtonSize(UIRoundButton_UI2.ButtonSize.ExtraLarge);
            linkButton.SetSize(280f, 92f);
            linkButton.BG.raycastTarget = true;
            linkButton.ButtonEnabled = true;
            linkButton.blockClick = false;
            linkButton.OnClickedSignal.Clear();
            linkButton.OnClickedSignal.Add(
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(BeginDiscordLink)
            );
            UpdateButtonForCurrentAccount();
            PositionButton(__instance);
            linkButton.UpdateLabelVisibility();
            linkButton.RunLayout();
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
        PositionButton(__instance);
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
        linkButton.Text = !string.IsNullOrWhiteSpace(linkedAccountId) && linkedAccountId == currentAccountId
            ? "Check Games"
            : "Link Discord";
    }

    private static void BeginDiscordLink()
    {
        if (requestInFlight)
        {
            return;
        }

        try
        {
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
                try
                {
                    Application.OpenURL(linkSession.LinkUrl);
                }
                catch (Exception exception)
                {
                    logger.LogWarning($"Unity could not open the Discord link, trying the native browser helper: {exception.Message}");
                    NativeHelpers.OpenURL(linkSession.LinkUrl, false);
                }
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
                    SetButtonText("Link Discord");
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
        }
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
