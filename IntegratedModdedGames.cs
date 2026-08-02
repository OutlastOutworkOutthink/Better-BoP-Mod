using BepInEx.Logging;
using HarmonyLib;
using I2.Loc;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PolytopiaBackendBase.Common;
using PolytopiaBackendBase.Game;
using System.Collections;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine.EventSystems;

namespace BetterBoPMod;

/// <summary>
/// Client for Discord-created Integrated games. It deliberately owns only the
/// new Modded tab and the private command transport; stock Ongoing/Replays and
/// all locked Better BoP gameplay patches remain untouched.
/// </summary>
internal static class IntegratedModdedGames
{
    internal const string Label = "Modded";
    internal const int TabId = 0xBB014;

    private const string ServerBaseUrl = "https://better-bop-server-production.up.railway.app";
    private const string ServerTokenKey = "betterbop.server.token.0.5.14";
    private const string RulesetId = "better-bop-0.5.14";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static readonly SemaphoreSlim CommandSubmitLock = new(1, 1);
    private static readonly SemaphoreSlim CommandReceiveLock = new(1, 1);
    private static readonly SemaphoreSlim ResultReportLock = new(1, 1);
    private static readonly ConcurrentQueue<Action> MainThreadActions = new();
    private static readonly string[] TribeNames =
    {
        "", "Nature", "Ai-Mo", "Aquarion", "Bardur", "Elyrion", "Hoodrick",
        "Imperius", "Kickoo", "Luxidoor", "Oumaji", "Quetzali", "Vengir",
        "Xin-xi", "Yădakk", "Zebasi", "Polaris", "Cymanti",
    };

    private static ManualLogSource logger = null!;
    private static CancellationTokenSource? polling;
    private static MultiplayerSelectionScreen? owner;
    private static IntegratedMatch[] matches = Array.Empty<IntegratedMatch>();
    private static bool selected;
    private static bool loading;
    private static string lastError = string.Empty;
    private static string activeGameId = string.Empty;
    private static string activeMatchId = string.Empty;
    private static string pendingWinnerAccountId = string.Empty;
    private static int nextCommandIndex;
    private static bool active;
    private static bool deferredTabLogged;
    private static bool connectionPromptShown;
    private static bool reconnectRequired;
    private static int mainThreadId;
    private static int renderRequested;

    internal static bool Active => active;

    internal static void Initialize(ManualLogSource logSource)
    {
        logger = logSource;
        polling?.Cancel();
        polling = new CancellationTokenSource();
        _ = PollLoopAsync(polling.Token);
    }

    private enum AccountLinkState
    {
        Connected,
        Missing,
        NeedsRepair,
    }

    private static AccountLinkState CurrentAccountLinkState()
    {
        string token = UnityEngine.PlayerPrefs.GetString(DiscordAccountLink.IntegrationTokenKey, string.Empty);
        string linkedAccount = UnityEngine.PlayerPrefs.GetString(DiscordAccountLink.LinkedAccountIdKey, string.Empty);
        string currentAccount = AccountManager.PlayerAccountId.ToString();
        if (!string.IsNullOrWhiteSpace(token) &&
            !string.IsNullOrWhiteSpace(linkedAccount) &&
            string.Equals(linkedAccount, currentAccount, StringComparison.OrdinalIgnoreCase))
        {
            reconnectRequired = false;
            return AccountLinkState.Connected;
        }

        if (reconnectRequired ||
            !string.IsNullOrWhiteSpace(token) ||
            !string.IsNullOrWhiteSpace(linkedAccount))
            return AccountLinkState.NeedsRepair;

        return AccountLinkState.Missing;
    }

    private static bool HasCurrentAccountLink() => CurrentAccountLinkState() == AccountLinkState.Connected;

    private static async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOnMainThreadAsync(HasCurrentAccountLink).ConfigureAwait(false))
                {
                    await RefreshMatchesAsync(false).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(pendingWinnerAccountId))
                        await FlushPendingResultAsync().ConfigureAwait(false);
                    if (active) await ReceiveCommandsAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning($"Integrated Modded poll failed: {exception.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(active ? 3 : 12), cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    internal static bool EnsureTab(MultiplayerSelectionScreen screen)
    {
        try
        {
            owner = screen;
            UIHorizontalList list = screen.ScreenSelectionList;
            if (list == null || !TryGetVisibleTabLabels(list, out List<string> currentLabels, out string source))
            {
                if (!deferredTabLogged)
                {
                    deferredTabLogged = true;
                    int dataCount = list?.data == null ? -1 : list.data.Length;
                    int keyCount = list?.keys == null ? -1 : list.keys.Length;
                    int itemCount = list?.items == null ? -1 : list.items.Length;
                    int idCount = list?.ids == null ? -1 : list.ids.Length;
                    logger.LogInfo(
                        "Modded tab insertion deferred until a live multiplayer row source exists " +
                        $"(data={dataCount}, keys={keyCount}, items={itemCount}, ids={idCount})."
                    );
                }
                return false;
            }

            if (list.data != null && list.ids != null)
            {
                for (int index = 0; index < list.data.Length; index++)
                {
                    if (IsModdedIndex(list, index)) return true;
                }
            }

            int moddedIndex = currentLabels.FindIndex(
                label => string.Equals(label, Label, StringComparison.OrdinalIgnoreCase)
            );
            int oldLength = currentLabels.Count;
            int newLength = moddedIndex >= 0 ? oldLength : oldLength + 1;
            if (moddedIndex < 0) moddedIndex = oldLength;
            Il2CppStringArray labels = new(newLength);
            Il2CppStructArray<int> ids = new(newLength);
            for (int index = 0; index < oldLength; index++)
            {
                labels[index] = currentLabels[index];
                ids[index] = index == moddedIndex
                    ? TabId
                    : list.ids != null && index < list.ids.Length ? list.ids[index] : index;
            }
            if (newLength > oldLength)
            {
                labels[moddedIndex] = Label;
                ids[moddedIndex] = TabId;
            }

            int selectedIndex = selected ? moddedIndex : list.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex >= newLength) selectedIndex = 0;
            list.SetData(labels, ids, selectedIndex, false);
            deferredTabLogged = false;
            logger.LogInfo($"Added Modded beside Ongoing and Replays from live {source} ({newLength} tabs).");
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Could not add the Modded multiplayer tab yet: {exception.Message}");
            return false;
        }
    }

    private static bool TryGetVisibleTabLabels(
        UIHorizontalList list,
        out List<string> labels,
        out string source
    )
    {
        labels = new List<string>();
        source = string.Empty;

        if (list.data != null)
        {
            for (int index = 0; index < list.data.Length; index++)
            {
                string label = list.data[index];
                if (string.IsNullOrWhiteSpace(label)) continue;
                if (list.useDataAsLocalizationKeys)
                {
                    string? localized = LocalizationManager.GetTranslation(label);
                    if (!string.IsNullOrWhiteSpace(localized)) label = localized;
                }
                labels.Add(label);
            }
            if (labels.Count > 0)
            {
                source = "data";
                return true;
            }
        }

        labels.Clear();
        if (list.keys != null)
        {
            for (int index = 0; index < list.keys.Length; index++)
            {
                string key = list.keys[index];
                if (string.IsNullOrWhiteSpace(key)) continue;
                string? localized = LocalizationManager.GetTranslation(key);
                string label = string.IsNullOrWhiteSpace(localized) ? key : localized;
                labels.Add(label);
            }
            if (labels.Count > 0)
            {
                source = "localization keys";
                return true;
            }
        }

        labels.Clear();
        if (list.items != null)
        {
            for (int index = 0; index < list.items.Length; index++)
            {
                UIHorizontalListItem? item = list.items[index];
                string? label = item?.text;
                if (!string.IsNullOrWhiteSpace(label)) labels.Add(label);
            }
            if (labels.Count > 0)
            {
                source = "rendered items";
                return true;
            }
        }

        return false;
    }

    internal static void EnsureOwnedTab()
    {
        MultiplayerSelectionScreen? screen = owner;
        if (screen != null) EnsureTab(screen);
    }

    internal static void EnsureOwnedTab(UIHorizontalList list)
    {
        MultiplayerSelectionScreen? screen = owner;
        UIHorizontalList? ownedList = screen?.ScreenSelectionList;
        if (screen == null || ownedList == null || ownedList.Pointer != list.Pointer) return;
        EnsureTab(screen);
    }

    internal static bool IsModdedIndex(UIHorizontalList list, int index)
    {
        if (list?.data == null || index < 0 || index >= list.data.Length) return false;
        if (string.Equals(list.data[index], Label, StringComparison.OrdinalIgnoreCase)) return true;
        return list.ids != null && index < list.ids.Length && list.ids[index] == TabId;
    }

    internal static bool SelectTab(MultiplayerSelectionScreen screen, int index)
    {
        owner = screen;
        EnsureTab(screen);
        if (!IsModdedIndex(screen.ScreenSelectionList, index))
        {
            selected = false;
            connectionPromptShown = false;
            Interlocked.Exchange(ref renderRequested, 0);
            SetModdedNavigation(screen, false);
            return true;
        }

        bool enteringModded = !selected;
        selected = true;
        if (enteringModded) connectionPromptShown = false;
        SetModdedNavigation(screen, true);
        screen.replayScreen?.Hide();
        screen.multiplayerScreen?.Show(true);
        RequestRender();
        _ = RefreshMatchesAsync(true);
        return false;
    }

    internal static void LeaveScreen(MultiplayerSelectionScreen screen)
    {
        if (owner == null || owner.Pointer != screen.Pointer) return;
        selected = false;
        connectionPromptShown = false;
        SetModdedNavigation(screen, false);
        Interlocked.Exchange(ref renderRequested, 0);
    }

    private static void SetModdedNavigation(MultiplayerSelectionScreen screen, bool modded)
    {
        if (screen.NewGameButton != null) screen.NewGameButton.gameObject.SetActive(!modded);
        if (screen.TournamentsButton != null) screen.TournamentsButton.gameObject.SetActive(!modded);
    }

    internal static bool AllowVanillaListBuild(MultiplayerScreen screen)
    {
        if (!selected || owner?.multiplayerScreen == null || owner.multiplayerScreen.Pointer != screen.Pointer) return true;
        RequestRender();
        return false;
    }

    /// <summary>
    /// GameManager.Update is a stable Unity main-thread boundary in Polytopia
    /// 122. All IL2CPP UI and game-state work queued by HTTP continuations is
    /// drained here instead of trusting SynchronizationContext, which is null
    /// when PolyMod loads this assembly.
    /// </summary>
    internal static void PumpMainThread()
    {
        Volatile.Write(ref mainThreadId, Environment.CurrentManagedThreadId);

        int processed = 0;
        while (processed++ < 64 && MainThreadActions.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                logger.LogError($"Integrated main-thread action failed: {exception}");
            }
        }

        if (Interlocked.Exchange(ref renderRequested, 0) != 0)
        {
            RenderOnMainThread();
        }
    }

    private static void RenderOnMainThread()
    {
        if (!selected) return;
        MultiplayerScreen? screen = owner?.multiplayerScreen;
        if (screen == null) return;
        if (!screen.isActiveAndEnabled)
        {
            RequestRender();
            return;
        }
        try
        {
            screen.ClearList();

            AccountLinkState linkState = CurrentAccountLinkState();
            if (linkState != AccountLinkState.Connected)
            {
                bool reconnect = linkState == AccountLinkState.NeedsRepair;
                AddInfo(
                    screen,
                    reconnect ? "Reconnect Discord to use Modded games" : "Connect Discord to use Modded games",
                    "Open Profile and press Connect Discord, then return to this tab."
                );
                if (!connectionPromptShown)
                {
                    connectionPromptShown = true;
                    DiscordAccountLink.ShowConnectionPrompt(
                        reconnect,
                        reconnect
                            ? "This Polytopia profile's saved Discord connection is incomplete or could not be verified."
                            : "This Polytopia profile is not connected to Discord yet."
                    );
                }
                return;
            }
            if (loading && matches.Length == 0)
            {
                AddInfo(screen, "Loading", "Checking the Better BoP server...");
                return;
            }
            if (!string.IsNullOrWhiteSpace(lastError))
            {
                AddInfo(screen, "Could not refresh", lastError);
                screen.AddButtonRow("Retry", Click(() => _ = RefreshMatchesAsync(true)));
                return;
            }

            IntegratedMatch[] visible = matches.Where(match => match.Status != "cancelled").ToArray();
            if (visible.Length == 0)
            {
                AddInfo(screen, "You have no active modded games.", "Create one in Discord with ?open Classic Integrated, then have another connected player join and both accept.");
                screen.AddButtonRow("Refresh", Click(() => _ = RefreshMatchesAsync(true)));
                return;
            }

            foreach (IntegratedMatch match in visible)
            {
                RenderMatch(screen, match);
            }
            screen.AddButtonRow("Refresh Modded Games", Click(() => _ = RefreshMatchesAsync(true)));
        }
        catch (Exception exception)
        {
            logger.LogError($"Could not render Modded games: {exception}");
        }
        finally
        {
            try
            {
                if (screen.container != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(screen.container);
            }
            catch (Exception exception)
            {
                logger.LogWarning($"Could not rebuild the Modded list layout: {exception.Message}");
            }
        }
    }

    private static void RenderMatch(MultiplayerScreen screen, IntegratedMatch match)
    {
        string own = match.Role == "host" ? match.HostDisplayName : match.AwayDisplayName;
        string opponent = match.Role == "host" ? match.AwayDisplayName : match.HostDisplayName;
        int? ownTribe = match.Role == "host" ? match.HostTribe : match.AwayTribe;
        int? opponentTribe = match.Role == "host" ? match.AwayTribe : match.HostTribe;
        string status = match.Status switch
        {
            "awaiting_acceptance" => "Accept in Discord",
            "waiting_for_tribes" => "Choosing tribes",
            "ready_to_start" => match.Role == "host" ? "Ready for you to host" : "Waiting for host",
            "provisioning" => "Host is creating the game",
            "active" => "In progress",
            "completed" => "Completed",
            "disputed" => "Result needs admin review",
            _ => match.Status,
        };
        AddInfo(
            screen,
            $"Integrated G{match.BotGameId} — {status}",
            $"{own} (you) vs {opponent}\nTiny Dryland · Unranked\nYour tribe: {TribeName(ownTribe)} · Opponent: {TribeName(opponentTribe)}"
        );

        if (match.Status is "waiting_for_tribes" or "ready_to_start")
        {
            for (int tribe = 1; tribe < TribeNames.Length; tribe++)
            {
                int selectedTribe = tribe;
                string suffix = ownTribe == tribe ? " ✓" : string.Empty;
                screen.AddButtonRow($"{TribeNames[tribe]}{suffix}", Click(() => _ = SelectTribeAsync(match.Id, selectedTribe)));
            }
            if (match.Role == "host" && match.HostTribe.HasValue && match.AwayTribe.HasValue)
            {
                screen.AddButtonRow("Start Tiny Dryland Game", Click(() => _ = StartMatchAsync(match.Id)));
            }
        }
        else if (match.Status == "active")
        {
            screen.AddButtonRow(active && activeMatchId == match.Id ? "Game Open" : "Open Modded Game", Click(() => _ = OpenMatchAsync(match.Id)));
        }
        else if (match.Status == "provisioning" && match.Role == "host")
        {
            screen.AddButtonRow("Finish Creating Game", Click(() => _ = ProvisionHostAsync(match.Id)));
        }
    }

    private static string TribeName(int? tribe) => tribe is >= 1 and < 18 ? TribeNames[tribe.Value] : "Not selected";

    private static void AddInfo(MultiplayerScreen screen, string header, string description)
    {
        MultiplayerInfoRow row = screen.AddInfoRow();
        row.gameObject.SetActive(true);
        if (row.header != null)
        {
            row.header.gameObject.SetActive(true);
            row.header.text = header;
        }
        if (row.description != null)
        {
            row.description.gameObject.SetActive(true);
            row.description.text = description;
        }
    }

    private static UIButtonBase.ButtonAction Click(Action action)
    {
        Action<int, BaseEventData> callback = (_, _) => action();
        return callback;
    }

    private static async Task RefreshMatchesAsync(bool showLoading, bool waitForExisting = false)
    {
        if (waitForExisting)
        {
            await RefreshLock.WaitAsync().ConfigureAwait(false);
        }
        else if (!await RefreshLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            if (showLoading)
            {
                loading = true;
                RequestRender();
            }
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Get, "/v1/integrated-matches", token);
            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.UpgradeRequired)
            {
                await RunOnMainThreadAsync(() =>
                {
                    UnityEngine.PlayerPrefs.DeleteKey(ServerTokenKey);
                    UnityEngine.PlayerPrefs.Save();
                    return true;
                }).ConfigureAwait(false);
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(ServerMessage(response, body));
            MatchListResponse? list = JsonSerializer.Deserialize<MatchListResponse>(body);
            matches = list?.Matches ?? Array.Empty<IntegratedMatch>();
            lastError = string.Empty;
        }
        catch (Exception exception)
        {
            lastError = exception.Message;
            if (showLoading) logger.LogWarning($"Modded match refresh failed: {exception.Message}");
        }
        finally
        {
            loading = false;
            RefreshLock.Release();
            RequestRender();
        }
    }

    private static async Task SelectTribeAsync(string matchId, int tribe)
    {
        await MutateMatchAsync(matchId, "tribe", new { tribe }).ConfigureAwait(false);
    }

    private static async Task StartMatchAsync(string matchId)
    {
        try
        {
            if (!await MutateMatchAsync(matchId, "start", new { }).ConfigureAwait(false)) return;
            IntegratedMatch? match = matches.FirstOrDefault(item => item.Id == matchId && item.Status == "provisioning");
            if (match == null) throw new InvalidOperationException("The server did not make this match ready for hosting.");
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            await StartHostAsync(match, token).ConfigureAwait(false);
            await RefreshMatchesAsync(false).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lastError = exception.Message;
            logger.LogError($"Could not host Integrated game: {exception}");
            RequestRender();
        }
    }

    private static async Task OpenMatchAsync(string matchId)
    {
        try
        {
            IntegratedMatch? match = matches.FirstOrDefault(item => item.Id == matchId && item.Status == "active");
            if (match == null) throw new InvalidOperationException("This game is not active yet.");
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            await ResumeParticipantAsync(match, token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lastError = exception.Message;
            logger.LogError($"Could not open Integrated game: {exception}");
            RequestRender();
        }
    }

    private static async Task ProvisionHostAsync(string matchId)
    {
        try
        {
            IntegratedMatch? match = matches.FirstOrDefault(item => item.Id == matchId && item.Status == "provisioning" && item.Role == "host");
            if (match == null) throw new InvalidOperationException("This match is not waiting for host setup.");
            await StartHostAsync(match, await EnsureServerTokenAsync().ConfigureAwait(false)).ConfigureAwait(false);
            await RefreshMatchesAsync(false, true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lastError = exception.Message;
            logger.LogError($"Could not finish Integrated host setup: {exception}");
            RequestRender();
        }
    }

    private static async Task<bool> MutateMatchAsync(string matchId, string action, object payload)
    {
        try
        {
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            string json = JsonSerializer.Serialize(payload);
            using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Post, $"/v1/integrated-matches/{matchId}/{action}", token, json);
            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(ServerMessage(response, body));
            lastError = string.Empty;
            await RefreshMatchesAsync(false, true).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            lastError = exception.Message;
            logger.LogWarning($"Integrated match action {action} failed: {exception.Message}");
            RequestRender();
            return false;
        }
    }

    private static string ServerMessage(HttpResponseMessage response, string body)
    {
        try
        {
            ErrorResponse? error = JsonSerializer.Deserialize<ErrorResponse>(body);
            if (!string.IsNullOrWhiteSpace(error?.Message)) return error.Message;
        }
        catch { }
        return $"Better BoP server returned {(int)response.StatusCode}.";
    }

    private static void RequestRender() => Interlocked.Exchange(ref renderRequested, 1);

    private static async Task<string> EnsureServerTokenAsync()
    {
        string stored = await RunOnMainThreadAsync(() =>
            UnityEngine.PlayerPrefs.GetString(ServerTokenKey, string.Empty)
        ).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        string integrationToken = await RunOnMainThreadAsync(() =>
            UnityEngine.PlayerPrefs.GetString(DiscordAccountLink.IntegrationTokenKey, string.Empty)
        ).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(integrationToken)) throw new InvalidOperationException("Connect Discord before opening Integrated games.");
        string json = JsonSerializer.Serialize(new
        {
            integrationToken,
            modVersion = DiscordAccountLink.ModVersion,
            rulesetId = RulesetId,
        });
        using HttpResponseMessage response = await HttpClient.PostAsync(
            $"{ServerBaseUrl}/v1/auth/exchange",
            new StringContent(json, Encoding.UTF8, "application/json")
        ).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            await InvalidateDiscordConnectionAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The saved Discord connection could not be verified. Reconnect Discord from Profile.");
        }
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ServerMessage(response, body));
        AuthResponse? auth = JsonSerializer.Deserialize<AuthResponse>(body);
        if (string.IsNullOrWhiteSpace(auth?.Token)) throw new InvalidOperationException("Server sign-in returned no token.");
        await RunOnMainThreadAsync(() =>
        {
            UnityEngine.PlayerPrefs.SetString(ServerTokenKey, auth.Token);
            UnityEngine.PlayerPrefs.Save();
            return true;
        }).ConfigureAwait(false);
        return auth.Token;
    }

    private static async Task InvalidateDiscordConnectionAsync()
    {
        await RunOnMainThreadAsync(() =>
        {
            UnityEngine.PlayerPrefs.DeleteKey(ServerTokenKey);
            UnityEngine.PlayerPrefs.DeleteKey(DiscordAccountLink.IntegrationTokenKey);
            UnityEngine.PlayerPrefs.DeleteKey(DiscordAccountLink.LinkedAccountIdKey);
            UnityEngine.PlayerPrefs.Save();
            reconnectRequired = true;
            connectionPromptShown = true;
            DiscordAccountLink.ShowConnectionPrompt(
                true,
                "The multiplayer server rejected this profile's saved Discord credential."
            );
            return true;
        }).ConfigureAwait(false);
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string path, string token, string? json = null)
    {
        HttpRequestMessage request = new(method, $"{ServerBaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task StartHostAsync(IntegratedMatch match, string token)
    {
        if (match.Role != "host" || string.IsNullOrWhiteSpace(match.GameId)) throw new InvalidOperationException("Only the Discord opener can host this game.");
        byte[] state = await RunOnMainThreadAsync(() =>
        {
            if (!active || activeGameId != match.GameId)
            {
                GameSettings settings = BuildSettings(match);
                GameManager.Instance.SetLocalClient();
                GameManager.Client.CreateSession(settings, Il2CppSystem.Guid.Parse(match.GameId));
                GameManager.Instance.LoadLevel();
                activeMatchId = match.Id;
                activeGameId = match.GameId;
                nextCommandIndex = 0;
                active = true;
            }
            return SerializeClient();
        }).ConfigureAwait(false);
        string payload = JsonSerializer.Serialize(new { serializedState = Convert.ToBase64String(state) });
        using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Put, $"/v1/games/{match.GameId}/initial-state", token, payload);
        using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ServerMessage(response, body));
        logger.LogMessage($"Hosted Integrated G{match.BotGameId} as Tiny Dryland game {match.GameId}.");
    }

    private static async Task ResumeParticipantAsync(IntegratedMatch match, string token)
    {
        if (string.IsNullOrWhiteSpace(match.GameId)) throw new InvalidOperationException("The host has not created this game yet.");
        using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Get, $"/v1/games/{match.GameId}", token);
        using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ServerMessage(response, body));
        GameResponse? game = JsonSerializer.Deserialize<GameResponse>(body);
        byte[] state = Convert.FromBase64String(game?.Game.InitialState ?? throw new InvalidOperationException("Host state is not ready."));
        await RunOnMainThreadAsync(() =>
        {
            GameManager.Instance.SetLocalClient();
            GameManager.Client.CreateSession(new Il2CppStructArray<byte>(state), Il2CppSystem.Guid.Parse(match.GameId));
            GameManager.Instance.LoadLevel();
            activeMatchId = match.Id;
            activeGameId = match.GameId;
            // Restore every command after the initial snapshot on reopen.
            nextCommandIndex = 0;
            active = true;
            return true;
        }).ConfigureAwait(false);
        await ReceiveCommandsAsync().ConfigureAwait(false);
        logger.LogMessage($"Opened Integrated G{match.BotGameId} as {match.Role}.");
    }

    private static GameSettings BuildSettings(IntegratedMatch match)
    {
        if (!match.HostTribe.HasValue || !match.AwayTribe.HasValue) throw new InvalidOperationException("Both tribes are required.");
        GameSettings settings = new();
        settings.ApplyGameTypeDefaults(GameType.Multiplayer, GameMode.Domination);
        settings.GameName = $"Integrated G{match.BotGameId}";
        settings.GameType = GameType.Multiplayer;
        settings.BaseGameMode = GameMode.Domination;
        settings.RulesGameMode = GameMode.Domination;
        settings.MapSize = match.MapSize > 0 ? match.MapSize : 121;
        settings.mapPreset = MapPreset.Dryland;
        settings.OpponentCount = 1;
        settings.ClearPlayers();
        settings.AddPlayer(BuildPlayer(match.HostAccountId, match.HostDisplayName, match.HostTribe.Value));
        settings.AddPlayer(BuildPlayer(match.AwayAccountId, match.AwayDisplayName, match.AwayTribe.Value));
        return settings;
    }

    private static PlayerData BuildPlayer(string accountId, string name, int tribeId)
    {
        TribeType tribe = (TribeType)tribeId;
        PlayerProfileState profile = new()
        {
            id = Il2CppSystem.Guid.Parse(accountId),
            name = name,
        };
        return new PlayerData
        {
            type = PlayerDataType.OnlineUser,
            profile = profile,
            defaultName = name,
            knownTribe = true,
            tribe = tribe,
            tribeMix = tribe,
            climate = tribe,
        };
    }

    private static byte[] SerializeClient()
    {
        Type wrapperType = FindType("ClientSerializationWrapper");
        object wrapper = Activator.CreateInstance(wrapperType, GameManager.Client)
            ?? throw new InvalidOperationException("Could not construct the game state serializer.");
        Type helperType = FindType("DiskSerializationHelpers");
        MethodInfo method = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(item => item.Name == "ToLZ4CompressedByteArray" && item.IsGenericMethodDefinition && item.GetParameters().Length == 2)
            .MakeGenericMethod(wrapperType);
        object encoded = method.Invoke(null, new object[] { wrapper, GameManager.GameState.Version })
            ?? throw new InvalidOperationException("Game state serialization returned no data.");
        return Bytes(encoded);
    }

    internal static async Task SubmitCommandAsync(CommandBase command)
    {
        if (!active || string.IsNullOrWhiteSpace(activeGameId)) return;
        try
        {
            byte[] serialized = SerializeCommand(command);
            await PostSerializedCommandAsync(serialized, command is EndTurnCommand).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError($"Integrated command upload failed: {exception}");
        }
    }

    private static async Task PostSerializedCommandAsync(byte[] serialized, bool endsTurn)
    {
        await CommandSubmitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            string payload = JsonSerializer.Serialize(new
            {
                commandIndex = nextCommandIndex,
                serializedData = Convert.ToBase64String(serialized),
                clientStateHash = (string?)null,
                endsTurn,
            });
            using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Post, $"/v1/games/{activeGameId}/commands", token, payload);
            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(ServerMessage(response, body));
            CommandResponse? result = JsonSerializer.Deserialize<CommandResponse>(body);
            if (result != null) nextCommandIndex = result.NextCommandIndex;
        }
        finally
        {
            CommandSubmitLock.Release();
        }
    }

    private static async Task ReceiveCommandsAsync()
    {
        if (string.IsNullOrWhiteSpace(activeGameId)) return;
        if (!await CommandReceiveLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Get, $"/v1/games/{activeGameId}/commands?after={nextCommandIndex - 1}", token);
            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            CommandListResponse? list = JsonSerializer.Deserialize<CommandListResponse>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            foreach (RemoteCommand remote in list?.Commands ?? Array.Empty<RemoteCommand>())
            {
                if (remote.CommandIndex < nextCommandIndex) continue;
                byte[] serialized = Convert.FromBase64String(remote.SerializedData);
                await RunOnMainThreadAsync(() =>
                {
                    CommandBase command = DeserializeCommand(serialized);
                    MethodInfo receive = AccessTools.Method(typeof(ClientBase), "ReceiveCommand", new[] { typeof(CommandBase) });
                    receive.Invoke(GameManager.Client, new object[] { command });
                    return true;
                }).ConfigureAwait(false);
                nextCommandIndex = remote.CommandIndex + 1;
            }
        }
        finally
        {
            CommandReceiveLock.Release();
        }
    }

    private static byte[] SerializeCommand(CommandBase command)
    {
        MethodInfo method = FindSerializationMethod("ToByteArray");
        return Bytes(method.Invoke(null, new object[] { command, GameManager.GameState.Version })!);
    }

    private static CommandBase DeserializeCommand(byte[] bytes)
    {
        MethodInfo method = FindSerializationMethod("FromByteArray");
        object?[] args = { new Il2CppStructArray<byte>(bytes), null, GameManager.GameState.Version };
        bool success = (bool)(method.Invoke(null, args) ?? false);
        if (!success || args[1] is not CommandBase command) throw new InvalidOperationException("Could not deserialize a remote command.");
        return command;
    }

    private static MethodInfo FindSerializationMethod(string name)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        foreach (Type type in SafeTypes(assembly))
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (method.Name == name && !method.IsGenericMethod &&
                ((name == "ToByteArray" && parameters.Length == 2 && parameters[0].ParameterType == typeof(CommandBase)) ||
                 (name == "FromByteArray" && parameters.Length == 3 && parameters[1].ParameterType.IsByRef && parameters[1].ParameterType.GetElementType() == typeof(CommandBase))))
                return method;
        }
        throw new MissingMethodException($"Could not find command {name}.");
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null).Cast<Type>(); }
    }

    private static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .SelectMany(SafeTypes).First(type => type.Name == name);

    private static byte[] Bytes(object source)
    {
        if (source is byte[] bytes) return bytes;
        if (source is IEnumerable enumerable) return enumerable.Cast<object>().Select(Convert.ToByte).ToArray();
        throw new InvalidOperationException("Serialized data had an unexpected type.");
    }

    private static Task<T> RunOnMainThreadAsync<T>(Func<T> action)
    {
        int knownMainThread = Volatile.Read(ref mainThreadId);
        if (knownMainThread != 0 && Environment.CurrentManagedThreadId == knownMainThread)
        {
            try { return Task.FromResult(action()); }
            catch (Exception exception) { return Task.FromException<T>(exception); }
        }

        TaskCompletionSource<T> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThreadActions.Enqueue(() =>
        {
            try { source.SetResult(action()); }
            catch (Exception exception) { source.SetException(exception); }
        });
        return source.Task;
    }

    internal static async Task ReportResultAsync(string winnerAccountId)
    {
        if (!active || string.IsNullOrWhiteSpace(activeGameId) || string.IsNullOrWhiteSpace(winnerAccountId)) return;
        pendingWinnerAccountId = winnerAccountId;
        await FlushPendingResultAsync().ConfigureAwait(false);
    }

    private static async Task FlushPendingResultAsync()
    {
        if (string.IsNullOrWhiteSpace(activeGameId) || string.IsNullOrWhiteSpace(pendingWinnerAccountId)) return;
        if (!await ResultReportLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            string gameId = activeGameId;
            string winnerAccountId = pendingWinnerAccountId;
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            string payload = JsonSerializer.Serialize(new { winnerAccountId });
            using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Post, $"/v1/games/{gameId}/result", token, payload);
            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(ServerMessage(response, body));

            // Stop intercepting commands as soon as this finished match has been
            // acknowledged. This keeps ordinary multiplayer completely isolated.
            pendingWinnerAccountId = string.Empty;
            active = false;
            activeGameId = string.Empty;
            activeMatchId = string.Empty;
            logger.LogMessage("Integrated game result was accepted by the Better BoP server.");
            RequestRender();
        }
        catch (Exception exception)
        {
            // Keep the pending winner in memory. The background loop retries it,
            // so a brief network outage at MatchEnded does not silently lose the result.
            logger.LogWarning($"Integrated result report will retry: {exception.Message}");
        }
        finally
        {
            ResultReportLock.Release();
        }
    }

    internal static string WinnerAccountId(byte winnerPlayerId)
    {
        MethodInfo? getPlayer = typeof(GameState).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "GetPlayer" && method.GetParameters().Length == 1);
        if (getPlayer == null) return string.Empty;
        Type parameterType = getPlayer.GetParameters()[0].ParameterType;
        object argument = Convert.ChangeType(winnerPlayerId, parameterType);
        object? player = getPlayer.Invoke(GameManager.GameState, new[] { argument });
        return FindAccountId(player, 0);
    }

    private static string FindAccountId(object? value, int depth)
    {
        if (value == null || depth > 3) return string.Empty;
        Type type = value.GetType();
        foreach (string idName in new[] { "accountId", "profileId", "userId", "id" })
        {
            object? idValue = type.GetField(idName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value)
                ?? type.GetProperty(idName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
            if (idValue != null && Il2CppSystem.Guid.TryParse(idValue.ToString(), out Il2CppSystem.Guid parsed) && parsed != Il2CppSystem.Guid.Empty)
                return parsed.ToString();
        }
        foreach (string nestedName in new[] { "profile", "playerData", "data", "user" })
        {
            object? nested = type.GetField(nestedName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value)
                ?? type.GetProperty(nestedName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
            string found = FindAccountId(nested, depth + 1);
            if (!string.IsNullOrWhiteSpace(found)) return found;
        }
        return string.Empty;
    }

    private sealed class AuthResponse { [JsonPropertyName("token")] public string Token { get; init; } = string.Empty; }
    private sealed class ErrorResponse { [JsonPropertyName("message")] public string Message { get; init; } = string.Empty; }
    private sealed class MatchListResponse { [JsonPropertyName("matches")] public IntegratedMatch[] Matches { get; init; } = Array.Empty<IntegratedMatch>(); }
    private sealed class IntegratedMatch
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("bot_game_id")] public string BotGameId { get; init; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
        [JsonPropertyName("game_id")] public string? GameId { get; init; }
        [JsonPropertyName("role")] public string Role { get; init; } = string.Empty;
        [JsonPropertyName("map_size")] public int MapSize { get; init; } = 121;
        [JsonPropertyName("host_tribe")] public int? HostTribe { get; init; }
        [JsonPropertyName("away_tribe")] public int? AwayTribe { get; init; }
        [JsonPropertyName("host_account_id")] public string HostAccountId { get; init; } = string.Empty;
        [JsonPropertyName("host_display_name")] public string HostDisplayName { get; init; } = string.Empty;
        [JsonPropertyName("away_account_id")] public string AwayAccountId { get; init; } = string.Empty;
        [JsonPropertyName("away_display_name")] public string AwayDisplayName { get; init; } = string.Empty;
    }
    private sealed class GameResponse { [JsonPropertyName("game")] public GamePayload Game { get; init; } = new(); }
    private sealed class GamePayload { [JsonPropertyName("initialState")] public string? InitialState { get; init; } }
    private sealed class CommandResponse { [JsonPropertyName("nextCommandIndex")] public int NextCommandIndex { get; init; } }
    private sealed class CommandListResponse { [JsonPropertyName("commands")] public RemoteCommand[] Commands { get; init; } = Array.Empty<RemoteCommand>(); }
    private sealed class RemoteCommand
    {
        [JsonPropertyName("command_index")] public int CommandIndex { get; init; }
        [JsonPropertyName("serialized_data")] public string SerializedData { get; init; } = string.Empty;
    }
}

[HarmonyPatch(typeof(MultiplayerSelectionScreen), nameof(MultiplayerSelectionScreen.Awake))]
internal static class ModdedTabAwakePatch
{
    [HarmonyPostfix]
    private static void AddTab(MultiplayerSelectionScreen __instance) => IntegratedModdedGames.EnsureTab(__instance);
}

[HarmonyPatch(typeof(MultiplayerSelectionScreen), nameof(MultiplayerSelectionScreen.Show))]
internal static class ModdedTabShowPatch
{
    [HarmonyPostfix]
    private static void AddTab(MultiplayerSelectionScreen __instance) => IntegratedModdedGames.EnsureTab(__instance);
}

[HarmonyPatch(typeof(MultiplayerSelectionScreen), "OnEnable")]
internal static class ModdedTabEnablePatch
{
    [HarmonyPostfix]
    private static void AddTab(MultiplayerSelectionScreen __instance) => IntegratedModdedGames.EnsureTab(__instance);
}

[HarmonyPatch(typeof(MultiplayerSelectionScreen), "OnDisable")]
internal static class ModdedTabDisablePatch
{
    [HarmonyPostfix]
    private static void LeaveModdedScreen(MultiplayerSelectionScreen __instance) =>
        IntegratedModdedGames.LeaveScreen(__instance);
}

/// <summary>
/// MultiplayerSelectionScreen's early lifecycle runs before its serialized
/// horizontal list has initialized on Polytopia 122. Recheck at the controller
/// and content boundaries that execute after the visible row exists.
/// </summary>
[HarmonyPatch]
internal static class ModdedTabLateLifecyclePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(
            typeof(MultiplayerSelectionScreen),
            nameof(MultiplayerSelectionScreen.UpdateScreenSelectionListSelectedIndex)
        );
        yield return AccessTools.Method(typeof(MultiplayerScreen), nameof(MultiplayerScreen.OnScreenUpdated));
        yield return AccessTools.Method(typeof(ReplaysScreen), nameof(ReplaysScreen.OnScreenUpdated));
    }

    [HarmonyPostfix]
    private static void AddTabAfterVisibleScreenUpdate() => IntegratedModdedGames.EnsureOwnedTab();
}

/// <summary>
/// Vanilla can repopulate the Ongoing/Replays list asynchronously. This final
/// data-boundary guard restores Modded after any such refresh and is a no-op
/// for every other horizontal list.
/// </summary>
[HarmonyPatch(
    typeof(UIHorizontalList),
    nameof(UIHorizontalList.SetData),
    new[]
    {
        typeof(Il2CppStringArray),
        typeof(Il2CppStructArray<int>),
        typeof(int),
        typeof(bool),
    }
)]
internal static class ModdedTabSetDataPatch
{
    [HarmonyPostfix]
    private static void RestoreTabAfterDataChange(UIHorizontalList __instance) =>
        IntegratedModdedGames.EnsureOwnedTab(__instance);
}

/// <summary>
/// Serialized prefab lists may never call SetData. Their localized keys and
/// rendered items become usable during UIHorizontalList's own enable/create
/// lifecycle, so recheck the owned Multiplayer list at those exact points.
/// </summary>
[HarmonyPatch]
internal static class ModdedTabListReadyPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(UIHorizontalList), "OnEnable");
        yield return AccessTools.Method(typeof(UIHorizontalList), "CreateItems");
    }

    [HarmonyPostfix]
    private static void AddTabWhenListBecomesUsable(UIHorizontalList __instance) =>
        IntegratedModdedGames.EnsureOwnedTab(__instance);
}

[HarmonyPatch(typeof(MultiplayerSelectionScreen), nameof(MultiplayerSelectionScreen.OnScreenSelectionListChanged))]
internal static class ModdedTabSelectionPatch
{
    [HarmonyPrefix]
    private static bool Select(MultiplayerSelectionScreen __instance, int index) => IntegratedModdedGames.SelectTab(__instance, index);
}

[HarmonyPatch(typeof(MultiplayerScreen), "BuildListAsync")]
internal static class ModdedListBuildPatch
{
    [HarmonyPrefix]
    private static bool KeepModdedList(MultiplayerScreen __instance) => IntegratedModdedGames.AllowVanillaListBuild(__instance);
}

/// <summary>
/// Unity invokes GameManager.Update on its main thread in menus and gameplay.
/// This dispatcher prevents HTTP continuations from touching IL2CPP objects.
/// </summary>
[HarmonyPatch(typeof(GameManager), "Update")]
internal static class IntegratedMainThreadPumpPatch
{
    [HarmonyPostfix]
    private static void DrainIntegratedWork() => IntegratedModdedGames.PumpMainThread();
}

[HarmonyPatch(typeof(ClientBase), "SendCommandRemote", new[] { typeof(CommandBase) })]
internal static class IntegratedModdedCommandPatch
{
    [HarmonyPrefix]
    private static bool SendThroughBetterBoP(CommandBase __0)
    {
        if (!IntegratedModdedGames.Active) return true;
        _ = IntegratedModdedGames.SubmitCommandAsync(__0);
        return false;
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.MatchEnded))]
internal static class IntegratedModdedResultPatch
{
    [HarmonyPrefix]
    private static void ReportWinner(byte __2)
    {
        if (!IntegratedModdedGames.Active) return;
        _ = IntegratedModdedGames.ReportResultAsync(IntegratedModdedGames.WinnerAccountId(__2));
    }
}
