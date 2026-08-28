using BepInEx.Logging;
using HarmonyLib;
using I2.Loc;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PolytopiaBackendBase.Auth;
using PolytopiaBackendBase.Common;
using PolytopiaBackendBase.Game;
using PolytopiaBackendBase.Game.ViewModels;
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
    private const string RulesetHash = "fdee6c4a2fcb0d3fd9b31c0de271031b3363ad7ffd3d6fc16c55e1aa748add89";
    private const int TinyDrylandTileCount = 121;
    private const int TinyDrylandSideLength = 11;
    private const int IntegratedTurnTimeMinutes = 24 * 60;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static readonly SemaphoreSlim CommandSubmitLock = new(1, 1);
    private static readonly SemaphoreSlim CommandReceiveLock = new(1, 1);
    private static readonly SemaphoreSlim ResultReportLock = new(1, 1);
    private static readonly SemaphoreSlim HostStartLock = new(1, 1);
    private static readonly ConcurrentQueue<Action> MainThreadActions = new();
    private static ManualLogSource logger = null!;
    private static CancellationTokenSource? polling;
    private static MultiplayerSelectionScreen? owner;
    private static MultiplayerSelectionScreen? moddedOwner;
    private static MultiplayerScreen? moddedScreen;
    private static IntegratedMatch[] matches = Array.Empty<IntegratedMatch>();
    private static volatile bool selected;
    private static bool loading;
    private static string lastError = string.Empty;
    private static string activeGameId = string.Empty;
    private static string activeMatchId = string.Empty;
    private static readonly ConcurrentDictionary<string, string> PendingResults = new(StringComparer.OrdinalIgnoreCase);
    private static int nextCommandIndex;
    private static bool active;
    private static volatile bool activeSession;
    private static bool deferredTabLogged;
    private static bool connectionPromptShown;
    private static bool reconnectRequired;
    private static int mainThreadId;
    private static int renderRequested;
    private static bool listReuseBootstrapLogged;
    private static string lastPollFailure = string.Empty;
    private static string lastRenderSummary = string.Empty;
    private static string pendingHostStateKey = string.Empty;
    private static byte[]? pendingHostInitialState;
    private static string selectingTribeMatchId = string.Empty;
    private static string pendingTribePickerMatchId = string.Empty;
    private static GameSettings? tribePickerSettingsBackup;
    private static bool tribePickerOwnsSettings;
    [ThreadStatic] private static LaunchManifest? pendingLaunchManifest;

    internal static bool Active => active && activeSession;

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
                    if (selected) await RefreshMatchesAsync(false).ConfigureAwait(false);
                    if (!PendingResults.IsEmpty)
                        await FlushPendingResultsAsync().ConfigureAwait(false);
                    if (Active) await ReceiveCommandsAsync().ConfigureAwait(false);
                }
                if (lastPollFailure.Length > 0)
                {
                    lastPollFailure = string.Empty;
                    logger.LogInfo("Integrated Modded polling recovered.");
                }
            }
            catch (Exception exception)
            {
                string failure = $"{exception.GetType().Name}: {exception.Message}";
                if (failure != lastPollFailure)
                    logger.LogWarning($"Integrated Modded poll failed: {failure}");
                lastPollFailure = failure;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Active ? 3 : 12), cancellationToken).ConfigureAwait(false);
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
            if (list != null && list.data != null && list.ids != null)
            {
                for (int index = 0; index < list.data.Length; index++)
                {
                    if (IsModdedIndex(list, index)) return true;
                }
            }
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
        if (selected && moddedScreen?.isActiveAndEnabled == true) RequestRender();
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
            if (!selected) return true;
            moddedScreen?.Hide();
            selected = false;
            connectionPromptShown = false;
            Interlocked.Exchange(ref renderRequested, 0);
            SetModdedNavigation(screen, false);
            // Modded handles its tab without running the stock selector. Force
            // the next native tab through its full Ongoing/Replays transition,
            // including the very first switch away from Modded.
            screen.currentScreenType = UIConstants.Screens.None;
            return true;
        }

        bool enteringModded = !selected;
        screen.replayScreen?.Hide();
        screen.multiplayerScreen?.Hide();
        MultiplayerScreen? content = EnsureModdedScreen(screen);
        if (content == null) return false;
        selected = true;
        screen.currentScreenType = UIConstants.Screens.None;
        if (enteringModded) connectionPromptShown = false;
        SetModdedNavigation(screen, true);
        content.Show(true);
        RequestRender();
        _ = RefreshMatchesAsync(true);
        return false;
    }

    private static MultiplayerScreen? EnsureModdedScreen(MultiplayerSelectionScreen screen)
    {
        if (moddedScreen != null && moddedOwner?.Pointer == screen.Pointer) return moddedScreen;
        if (moddedScreen != null)
        {
            moddedScreen.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(moddedScreen.gameObject);
        }
        moddedScreen = null;
        moddedOwner = null;
        MultiplayerScreen? template = screen.multiplayerScreen;
        if (template == null) return null;
        try
        {
            // The template is hidden before cloning, so the clone stays
            // inactive until its pointer is registered. Its first OnEnable is
            // therefore already protected from stock list construction.
            template.Hide();
            MultiplayerScreen clone = UnityEngine.Object.Instantiate(template, template.transform.parent);
            clone.name = "Better BoP Modded Multiplayer";
            clone.gameObject.SetActive(false);
            if (clone.rows != null)
                foreach (UIBasicButton row in clone.rows)
                    if (row != null)
                    {
                        row.gameObject.SetActive(false);
                        UnityEngine.Object.Destroy(row.gameObject);
                    }
            if (clone.otherRows != null)
                foreach (UnityEngine.GameObject row in clone.otherRows)
                    if (row != null)
                    {
                        row.SetActive(false);
                        UnityEngine.Object.Destroy(row);
                    }
            clone.rows = new Il2CppSystem.Collections.Generic.List<UIBasicButton>();
            clone.otherRows = new Il2CppSystem.Collections.Generic.List<UnityEngine.GameObject>();
            clone.listReuse = clone.container == null ? null : new ListReuseHelper(clone.container);
            moddedOwner = screen;
            moddedScreen = clone;
            logger.LogInfo("Created the isolated Modded multiplayer screen.");
            return clone;
        }
        catch (Exception exception)
        {
            logger.LogError($"Could not create the Modded multiplayer screen: {exception}");
            return null;
        }
    }

    internal static void LeaveScreen(MultiplayerSelectionScreen screen)
    {
        if (owner == null || owner.Pointer != screen.Pointer) return;
        // Opening either vanilla tribe-picker implementation temporarily
        // disables its parent selection screen. Keep the owned Modded view and
        // session alive so the picker can submit and return to the same tab.
        if (!string.IsNullOrEmpty(pendingTribePickerMatchId)) return;
        selected = false;
        connectionPromptShown = false;
        SetModdedNavigation(screen, false);
        Interlocked.Exchange(ref renderRequested, 0);
        RestoreTribePickerSettings();
        pendingTribePickerMatchId = string.Empty;
        MultiplayerScreen? content = moddedScreen;
        moddedScreen = null;
        moddedOwner = null;
        if (content != null) UnityEngine.Object.Destroy(content.gameObject);
    }

    private static void SetModdedNavigation(MultiplayerSelectionScreen screen, bool modded)
    {
        if (screen.NewGameButton != null) screen.NewGameButton.gameObject.SetActive(!modded);
        if (screen.TournamentsButton != null) screen.TournamentsButton.gameObject.SetActive(!modded);
    }

    internal static bool AllowVanillaListBuild(MultiplayerScreen screen)
    {
        if (!OwnsModdedList(screen)) return true;
        RequestRender();
        return false;
    }

    internal static bool HandlePullRefresh(MultiplayerScreen screen)
    {
        if (!OwnsModdedList(screen)) return true;
        _ = RefreshAfterPullAsync();
        return false;
    }

    private static async Task RefreshAfterPullAsync()
    {
        await RefreshMatchesAsync(true, true).ConfigureAwait(false);
        await RunOnMainThreadAsync(() =>
        {
            moddedScreen?.refresher?.EndRefreshing();
            return true;
        }).ConfigureAwait(false);
    }

    private static bool OwnsModdedList(MultiplayerScreen screen) =>
        moddedScreen != null && moddedScreen.Pointer == screen.Pointer;

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
        MultiplayerScreen? screen = moddedScreen;
        if (screen == null) return;
        if (!screen.isActiveAndEnabled) return;
        ListReuseHelper.Guard? refresh = null;
        try
        {
            // MultiplayerScreen's stock row helpers are backed by
            // ListReuseHelper. ClearList only clears the visible collections;
            // AddInfoRow/AddButtonRow still require an active refresh guard.
            // Alpha 0.5.18 rendered on the correct Unity thread but omitted
            // this guard, so every active match failed before its first row.
            // Some multiplayer-screen instances have already finished OnEnable
            // before the stock list is built. Because Modded suppresses that
            // stock build, its ListReuseHelper can still be null here. Create
            // the same helper against the stock container instead of touching
            // a null field or waiting for a vanilla build that will not run.
            if (screen.container == null || screen.infoRowPrefab == null ||
                screen.buttonRowPrefab == null || screen.lobbyInfoRowPrefab == null)
            {
                if (!listReuseBootstrapLogged)
                {
                    listReuseBootstrapLogged = true;
                    logger.LogInfo(
                        "Modded list render deferred until the multiplayer container and row prefabs are ready."
                    );
                }
                RequestRender();
                return;
            }

            ListReuseHelper? listReuse = screen.listReuse;
            if (listReuse == null)
            {
                listReuse = new ListReuseHelper(screen.container);
                screen.listReuse = listReuse;
                logger.LogInfo("Initialized the Modded list reuse helper from the stock multiplayer container.");
            }
            listReuseBootstrapLogged = false;
            if (!listReuse.isRefreshing) refresh = listReuse.BeginRefresh();
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
                AddInfo(screen, "You have no active modded games.", "Create one in Discord with ?open Classic Integrated, then have another connected player join. The game is created automatically.");
                screen.AddButtonRow("Refresh", Click(() => _ = RefreshMatchesAsync(true)));
                return;
            }

            string renderSummary = string.Join(", ", visible.Select(match => $"G{match.BotGameId}={match.Status}"));
            if (!string.Equals(renderSummary, lastRenderSummary, StringComparison.Ordinal))
            {
                lastRenderSummary = renderSummary;
                logger.LogInfo($"Rendering {visible.Length} Modded match row(s): {renderSummary}");
            }
            foreach (IntegratedMatch match in visible)
            {
                RenderMatch(screen, match);
            }
        }
        catch (Exception exception)
        {
            logger.LogError($"Could not render Modded games: {exception}");
        }
        finally
        {
            try
            {
                refresh?.Dispose();
            }
            catch (Exception exception)
            {
                logger.LogWarning($"Could not finish the Modded list refresh: {exception.Message}");
            }
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
        // Keep the unselected Modded tab as clean as the stock Ongoing list.
        // All setup details and actions belong to Polytopia's lobby popup.
        screen.AddLobbyRow(BuildLobbyViewModel(match));
    }

    private static LobbyGameViewModel BuildLobbyViewModel(IntegratedMatch match)
    {
        Il2CppSystem.Collections.Generic.List<ParticipatorViewModel> participators = new();
        participators.Add(BuildParticipator(match.HostAccountId, match.HostDisplayName, match.HostTribe));
        participators.Add(BuildParticipator(match.AwayAccountId, match.AwayDisplayName, match.AwayTribe));
        return new LobbyGameViewModel
        {
            Id = Il2CppSystem.Guid.Parse(match.Id),
            Name = $"Integrated G{match.BotGameId}",
            MapPreset = MapPreset.Dryland,
            MapSize = TinyDrylandSideLength,
            OpponentCount = 1,
            GameMode = GameMode.Domination,
            OwnerId = Il2CppSystem.Guid.Parse(match.HostAccountId),
            DisabledTribes = new Il2CppSystem.Collections.Generic.List<int>(),
            IsPersistent = false,
            IsSharable = false,
            TimeLimit = IntegratedTurnTimeMinutes,
            ScoreLimit = 0,
            InviteLink = string.Empty,
            GameContext = new GameContext(),
            Participators = participators,
            Bots = new Il2CppSystem.Collections.Generic.List<int>(),
        };
    }

    private static ParticipatorViewModel BuildParticipator(string accountId, string name, int? tribe)
    {
        return new ParticipatorViewModel
        {
            UserId = Il2CppSystem.Guid.Parse(accountId),
            Name = name,
            GameVersion = new Il2CppSystem.Collections.Generic.List<ClientGameVersionViewModel>(),
            // The Discord match already reserved and accepted both seats.
            // Tribe readiness is rendered separately so Polytopia does not
            // open its unrelated Accept/Decline-invitation flow.
            InvitationState = PlayerInvitationState.Accepted,
            SelectedTribe = tribe ?? (int)TribeType.None,
            SelectedTribeSkin = (int)SkinType.Default,
            // A zero-length payload marks this as one of our synthetic seats.
            // PlayerDataUtils is bypassed for these rows below, avoiding stock
            // avatar deserialization against an intentionally absent payload.
            AvatarStateData = new Il2CppStructArray<byte>(0),
        };
    }

    internal static bool TryGetIntegratedPlayerData(ParticipatorViewModel participator, out PlayerData data)
    {
        data = null!;
        if (!selected || participator?.AvatarStateData == null || participator.AvatarStateData.Length != 0)
            return false;
        string accountId = participator.UserId.ToString();
        int? tribe = participator.SelectedTribe is >= 2 and <= 17 ? participator.SelectedTribe : null;
        data = BuildPlayer(accountId, participator.Name, tribe);
        return true;
    }

    private static void OpenTribePicker(string matchId)
    {
        try
        {
            if (!string.IsNullOrEmpty(pendingTribePickerMatchId) ||
                !string.IsNullOrEmpty(selectingTribeMatchId))
                throw new InvalidOperationException("Your tribe choice is already being saved.");
            IntegratedMatch? match = matches.FirstOrDefault(item => item.Id == matchId);
            if (match == null || match.Status is not ("waiting_for_tribes" or "ready_to_start"))
                throw new InvalidOperationException("This match is no longer accepting tribe choices.");
            int? ownTribe = match.Role == "host" ? match.HostTribe : match.AwayTribe;
            if (ownTribe.HasValue)
                throw new InvalidOperationException("Your tribe is already locked for this game.");

            tribePickerSettingsBackup = GameManager.PreliminaryGameSettings;
            tribePickerOwnsSettings = true;
            GameManager.PreliminaryGameSettings = BuildPickerSettings(match);
            pendingTribePickerMatchId = match.Id;
            lastError = string.Empty;
            UIManager.Instance.ShowScreen(UIConstants.Screens.TribePicker, false, null);
            logger.LogInfo($"Opened the native tribe picker for Integrated G{match.BotGameId}.");
        }
        catch (Exception exception)
        {
            pendingTribePickerMatchId = string.Empty;
            RestoreTribePickerSettings();
            lastError = exception.Message;
            logger.LogError($"Could not open the Integrated tribe picker: {exception}");
            RequestRender();
        }
    }

    internal static bool SubmitIntegratedTribe(object picker)
    {
        string matchId = pendingTribePickerMatchId;
        if (string.IsNullOrEmpty(matchId)) return string.IsNullOrEmpty(selectingTribeMatchId);
        if (!string.IsNullOrEmpty(selectingTribeMatchId)) return false;

        int tribe = (int)(picker switch
        {
            TribePickerScreen_UI2 current => current.selectedTribe?.type ?? TribeType.None,
            TribeSelectorScreen legacy => legacy.selectedTribe?.type ?? TribeType.None,
            _ => TribeType.None,
        });
        if (tribe is < 2 or > 17)
        {
            PopupManager.ShowSimplePopup(
                "better-bop-integrated-tribe",
                "Choose Tribe",
                "Choose a specific playable tribe for this Integrated game."
            );
            return false;
        }

        IntegratedMatch? match = matches.FirstOrDefault(item =>
            item.Id == matchId &&
            item.Status is "waiting_for_tribes" or "ready_to_start" &&
            (item.Role == "host" ? !item.HostTribe.HasValue : !item.AwayTribe.HasValue));
        if (match == null)
        {
            pendingTribePickerMatchId = string.Empty;
            RestoreTribePickerSettings();
            lastError = "This match is no longer accepting tribe choices.";
            RequestRender();
            return false;
        }

        selectingTribeMatchId = matchId;
        pendingTribePickerMatchId = string.Empty;
        RestoreTribePickerSettings();
        logger.LogInfo($"Submitting tribe {tribe} for Integrated G{match.BotGameId}.");
        _ = SelectTribeAsync(matchId, tribe);
        // Close this owned picker immediately. Both picker implementations are
        // suppressed, so Polytopia can never create a stock multiplayer lobby.
        UIManager.Instance.PopCurrentScreen();
        return false;
    }

    internal static void CloseIntegratedTribePicker()
    {
        pendingTribePickerMatchId = string.Empty;
        RestoreTribePickerSettings();
        RequestRender();
    }

    private static void RestoreTribePickerSettings()
    {
        if (!tribePickerOwnsSettings) return;
        GameManager.PreliminaryGameSettings = tribePickerSettingsBackup;
        tribePickerSettingsBackup = null;
        tribePickerOwnsSettings = false;
    }

    private static GameSettings BuildPickerSettings(IntegratedMatch match)
    {
        GameSettings settings = new();
        settings.ApplyGameTypeDefaults(GameType.Multiplayer, GameMode.Domination);
        settings.GameName = $"Integrated G{match.BotGameId}";
        settings.GameType = GameType.Multiplayer;
        settings.BaseGameMode = GameMode.Domination;
        settings.RulesGameMode = GameMode.Domination;
        settings.MapSize = TinyDrylandSideLength;
        settings.mapPreset = MapPreset.Dryland;
        settings.OpponentCount = 1;
        settings.ClearPlayers();
        settings.AddPlayer(BuildPlayer(match.HostAccountId, match.HostDisplayName, match.HostTribe));
        settings.AddPlayer(BuildPlayer(match.AwayAccountId, match.AwayDisplayName, match.AwayTribe));
        return settings;
    }

    private static bool TryGetIntegratedLobby(LobbyGameViewModel? lobby, out IntegratedMatch? match)
    {
        match = null;
        if (lobby == null) return false;
        string lobbyId = lobby.Id.ToString();
        match = matches.FirstOrDefault(item => string.Equals(item.Id, lobbyId, StringComparison.OrdinalIgnoreCase));
        return match != null;
    }

    private static bool CanPickOwnTribe(IntegratedMatch match, int? ownTribe) =>
        !ownTribe.HasValue && match.Status is ("waiting_for_tribes" or "ready_to_start") &&
        !string.Equals(selectingTribeMatchId, match.Id, StringComparison.OrdinalIgnoreCase);

    internal static bool ShowIntegratedPlayerInfo(LobbyPopup popup, Il2CppSystem.Nullable<Il2CppSystem.Guid> userId)
    {
        if (!TryGetIntegratedLobby(popup.lobbyGameViewModel, out IntegratedMatch? match) || match == null) return true;
        string accountId = userId.HasValue ? userId.Value.ToString() : string.Empty;
        string localAccountId = AccountManager.PlayerAccountId.ToString();
        int? selectedTribe = string.Equals(accountId, match.HostAccountId, StringComparison.OrdinalIgnoreCase)
            ? match.HostTribe
            : match.AwayTribe;
        if (string.Equals(accountId, localAccountId, StringComparison.OrdinalIgnoreCase) &&
            CanPickOwnTribe(match, selectedTribe))
        {
            popup.Hide();
            OpenTribePicker(match.Id);
        }
        else
        {
            string playerName = string.Equals(accountId, match.HostAccountId, StringComparison.OrdinalIgnoreCase)
                ? match.HostDisplayName
                : match.AwayDisplayName;
            PopupManager.ShowSimplePopup("better-bop-integrated-player", playerName, "This seat is managed by the Discord Integrated match.");
        }
        return false;
    }

    internal static void RefreshIntegratedLobbyRow(LobbyGameInfoRow row)
    {
        if (!TryGetIntegratedLobby(row.lobbyGameViewModel, out IntegratedMatch? match) || match == null ||
            row.infoLabel == null) return;
        int? ownTribe = match.Role == "host" ? match.HostTribe : match.AwayTribe;
        int? opponentTribe = match.Role == "host" ? match.AwayTribe : match.HostTribe;
        row.infoLabel.text = !ownTribe.HasValue
            ? "Choose your tribe"
            : !opponentTribe.HasValue
                ? "Waiting for opponent's tribe"
                : match.Status switch
        {
            "ready_to_start" when match.Role == "host" => "Ready to start",
            "ready_to_start" => "Waiting for host",
            "provisioning" => "Creating Tiny Dryland map...",
            "active" => "In progress",
            _ => row.infoLabel.text,
        };
    }

    internal static bool GetIntegratedParticipantBadge(
        LobbyPopup popup,
        ParticipatorViewModel participator,
        ref string result
    )
    {
        if (!TryGetIntegratedLobby(popup.lobbyGameViewModel, out IntegratedMatch? match) || match == null)
            return true;
        // A selected tribe head is the readiness indicator. Integrated seats
        // never use invitation/ready badges, which also avoids the white badge
        // placeholder shown when a stock sprite is unavailable.
        result = string.Empty;
        return false;
    }

    internal static void RefreshIntegratedLobbyPopup(LobbyPopup popup)
    {
        if (!TryGetIntegratedLobby(popup.lobbyGameViewModel, out IntegratedMatch? match) || match == null) return;
        if (popup.addPlayerButton != null) popup.addPlayerButton.gameObject.SetActive(false);
        HideIntegratedReadyBadges(popup);
        popup.Description = GetIntegratedLobbyDescription(match, popup.Description);

        UITextButton? actionButton = popup.TopButton;
        if (actionButton == null) return;
        int? ownTribe = match.Role == "host" ? match.HostTribe : match.AwayTribe;
        int? opponentTribe = match.Role == "host" ? match.AwayTribe : match.HostTribe;
        string? label = CanPickOwnTribe(match, ownTribe) ? "PICK TRIBE" : match.Status switch
        {
            "ready_to_start" when match.Role == "host" && ownTribe.HasValue && opponentTribe.HasValue => "START GAME",
            "provisioning" when match.Role == "host" => "CONTINUE SETUP",
            "active" => Active && activeMatchId == match.Id ? "GAME OPEN" : "OPEN GAME",
            _ => null,
        };
        actionButton.gameObject.SetActive(label != null);
        if (label != null)
        {
            actionButton.ButtonEnabled = true;
            actionButton.text = label;
        }
    }

    private static void HideIntegratedReadyBadges(LobbyPopup popup)
    {
        if (popup.playerButtons == null) return;
        foreach (PlayerButton button in popup.playerButtons)
        {
            button.BadgeEnabled = false;
            if (button.badgeHolder != null) button.badgeHolder.gameObject.SetActive(false);
        }
    }

    internal static void DescribeIntegratedLobby(LobbyPopup popup, ref string description)
    {
        if (!TryGetIntegratedLobby(popup.lobbyGameViewModel, out IntegratedMatch? match) || match == null) return;
        description = GetIntegratedLobbyDescription(match, description);
    }

    private static string GetIntegratedLobbyDescription(IntegratedMatch match, string fallback)
    {
        int? ownTribe = match.Role == "host" ? match.HostTribe : match.AwayTribe;
        int? opponentTribe = match.Role == "host" ? match.AwayTribe : match.HostTribe;
        if (!ownTribe.HasValue) return "Choose your tribe to lock in your seat.";
        if (!opponentTribe.HasValue) return "Waiting for your opponent to choose a tribe.";
        return match.Status switch
        {
            "ready_to_start" when match.Role == "host" => "Both tribes are locked. Start the game when ready.",
            "ready_to_start" => "Both tribes are locked. Waiting for the host to start.",
            "provisioning" => "The host is creating the Tiny Dryland map.",
            "active" => "This Integrated game is in progress.",
            _ => fallback,
        };
    }

    internal static bool BlockIntegratedLobbyAction(LobbyPopup popup, string action)
    {
        if (!TryGetIntegratedLobby(popup.lobbyGameViewModel, out IntegratedMatch? match) || match == null) return true;
        int? ownTribe = match.Role == "host" ? match.HostTribe : match.AwayTribe;
        int? opponentTribe = match.Role == "host" ? match.AwayTribe : match.HostTribe;
        if (action == "start" && CanPickOwnTribe(match, ownTribe))
        {
            popup.Hide();
            OpenTribePicker(match.Id);
            return false;
        }
        if (action == "start" && match.Status == "ready_to_start" && match.Role == "host" &&
            ownTribe.HasValue && opponentTribe.HasValue)
        {
            popup.Hide();
            _ = StartMatchAsync(match.Id);
            return false;
        }
        if (action == "start" && match.Status == "provisioning" && match.Role == "host")
        {
            popup.Hide();
            _ = StartMatchAsync(match.Id);
            return false;
        }
        if (action == "start" && match.Status == "active")
        {
            popup.Hide();
            if (!Active || activeMatchId != match.Id) _ = OpenMatchAsync(match.Id);
            return false;
        }
        string description = action switch
        {
            "invite" => "Both player seats are assigned by the Discord bot.",
            "leave" => "Manage or delete this Integrated match from its Discord game channel.",
            _ when match.Status == "waiting_for_tribes" => "Waiting for both players to lock their tribes.",
            _ when match.Status == "ready_to_start" => "Only the Discord game opener can start after both tribes are locked.",
            _ => "This action is not available for the Integrated game right now.",
        };
        PopupManager.ShowSimplePopup("better-bop-integrated-lobby", "Integrated Game", description);
        return false;
    }

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
        try
        {
            MatchMutationResponse? selected = await MutateMatchAsync(matchId, "tribe", new { tribe }).ConfigureAwait(false);
            if (selected == null)
                logger.LogWarning($"Integrated tribe selection failed for match {matchId}; the choice remains unlocked.");
        }
        finally
        {
            selectingTribeMatchId = string.Empty;
            RequestRender();
        }
    }

    private static async Task StartMatchAsync(string matchId)
    {
        if (!await HostStartLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            MatchMutationResponse? started = await MutateMatchAsync(matchId, "start", new { }).ConfigureAwait(false);
            IntegratedMatch? match = started?.Match ?? matches.FirstOrDefault(item => item.Id == matchId);
            if (match == null || started?.LaunchManifest == null)
                throw new InvalidOperationException("The server returned no Integrated launch manifest.");
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            if (match.Status == "active") await ResumeParticipantAsync(match, token).ConfigureAwait(false);
            else if (match.Status == "provisioning")
                await StartHostAsync(match, token, started.LaunchManifest).ConfigureAwait(false);
            else throw new InvalidOperationException("The server did not make this match ready for hosting.");
            await RefreshMatchesAsync(false).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lastError = exception.Message;
            logger.LogError($"Could not host Integrated game: {exception}");
            RequestRender();
        }
        finally
        {
            HostStartLock.Release();
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

    private static async Task<MatchMutationResponse?> MutateMatchAsync(string matchId, string action, object payload)
    {
        bool refresh = false;
        MatchMutationResponse? mutation = null;
        await RefreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            string json = JsonSerializer.Serialize(payload);
            using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Post, $"/v1/integrated-matches/{matchId}/{action}", token, json);
            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(ServerMessage(response, body));
            lastError = string.Empty;
            mutation = JsonSerializer.Deserialize<MatchMutationResponse>(body);
            refresh = !CommitMutation(matchId, action, mutation);
        }
        catch (Exception exception)
        {
            lastError = exception.Message;
            logger.LogWarning($"Integrated match action {action} failed: {exception.Message}");
        }
        finally
        {
            RefreshLock.Release();
        }
        if (mutation != null && refresh) await RefreshMatchesAsync(false, true).ConfigureAwait(false);
        RequestRender();
        return mutation;
    }

    private static bool CommitMutation(string matchId, string action, MatchMutationResponse? mutation)
    {
        IntegratedMatch[] snapshot = matches;
        int index = Array.FindIndex(snapshot, item => item.Id == matchId);
        if (mutation == null || mutation.MatchId != matchId || index < 0 ||
            string.IsNullOrEmpty(mutation.Status)) return false;
        IntegratedMatch current = snapshot[index];
        IntegratedMatch updated = mutation.Match?.Id == matchId ? mutation.Match : action switch
        {
            "tribe" => current with
            {
                Status = mutation.Status,
                HostTribe = mutation.HostTribe,
                AwayTribe = mutation.AwayTribe,
            },
            "start" => current with
            {
                Status = mutation.Status,
                GameId = mutation.GameId ?? current.GameId,
            },
            _ => current,
        };
        if (ReferenceEquals(updated, current)) return false;
        IntegratedMatch[] next = (IntegratedMatch[])snapshot.Clone();
        next[index] = updated;
        Volatile.Write(ref matches, next);
        return true;
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

    internal static void RefreshActiveSession()
    {
        activeSession = active && GameManager.Client != null &&
            string.Equals(GameManager.Client.CurrentGameId.ToString(), activeGameId, StringComparison.OrdinalIgnoreCase);
    }

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
            rulesetHash = RulesetHash,
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

    private static async Task StartHostAsync(IntegratedMatch match, string token, LaunchManifest manifest)
    {
        ValidateLaunchManifest(match, manifest);
        string stateKey = $"{match.Id}:{manifest.GameId}:{manifest.RulesetHash}:{manifest.Seed}:{manifest.VillageNameSeed}";
        byte[] state;
        if (string.Equals(pendingHostStateKey, stateKey, StringComparison.Ordinal) &&
            pendingHostInitialState is { Length: > 0 })
        {
            state = pendingHostInitialState;
        }
        else
        {
            state = await RunOnMainThreadAsync(() =>
            {
                GameSettings settings = BuildSettings(match, manifest);
                GameManager.Instance.SetLocalClient();
                CreateSessionResult result;
                pendingLaunchManifest = manifest;
                try
                {
                    result = GameManager.Client.CreateSession(settings, Il2CppSystem.Guid.Parse(manifest.GameId));
                }
                finally
                {
                    pendingLaunchManifest = null;
                }
                MapData map = ValidateSession(
                    result,
                    $"Integrated G{match.BotGameId}",
                    TinyDrylandSideLength
                );
                if (GameManager.GameState.Seed != manifest.Seed ||
                    GameManager.GameState.VillageNameSeed != manifest.VillageNameSeed)
                    throw new InvalidOperationException("Polytopia did not apply the server launch seeds.");
                logger.LogMessage(
                    $"Generated stock Polytopia map for Integrated G{match.BotGameId}: " +
                    $"{map.Width}x{map.Height}, {map.Tiles.Length} tiles, {GameManager.GameState.PlayerCount} players."
                );
                return SerializeClient();
            }).ConfigureAwait(false);
            pendingHostStateKey = stateKey;
            pendingHostInitialState = state;
        }
        string payload = JsonSerializer.Serialize(new { serializedState = Convert.ToBase64String(state) });
        using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Put, $"/v1/games/{manifest.GameId}/initial-state", token, payload);
        using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(ServerMessage(response, body));
        await RunOnMainThreadAsync(() =>
        {
            if (!Active || activeGameId != manifest.GameId)
            {
                GameManager.Instance.LoadLevel();
                activeMatchId = match.Id;
                activeGameId = manifest.GameId;
                nextCommandIndex = 0;
                active = true;
                RefreshActiveSession();
            }
            return true;
        }).ConfigureAwait(false);
        pendingHostStateKey = string.Empty;
        pendingHostInitialState = null;
        logger.LogMessage($"Hosted Integrated G{match.BotGameId} as Tiny Dryland game {manifest.GameId}.");
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
            CreateSessionResult result = GameManager.Client.CreateSession(
                new Il2CppStructArray<byte>(state),
                Il2CppSystem.Guid.Parse(match.GameId)
            );
            ValidateSession(
                result,
                $"downloaded Integrated G{match.BotGameId}",
                TinyDrylandSideLength
            );
            GameManager.Instance.LoadLevel();
            activeMatchId = match.Id;
            activeGameId = match.GameId;
            // Restore every command after the initial snapshot on reopen.
            nextCommandIndex = 0;
            active = true;
            RefreshActiveSession();
            return true;
        }).ConfigureAwait(false);
        await ReceiveCommandsAsync().ConfigureAwait(false);
        logger.LogMessage($"Opened Integrated G{match.BotGameId} as {match.Role}.");
    }

    private static MapData ValidateSession(CreateSessionResult result, string context, int expectedSideLength)
    {
        GameState? gameState = GameManager.GameState;
        MapData? map = gameState?.Map;
        int tileCount = map?.Tiles?.Length ?? 0;
        if (result != CreateSessionResult.Success || gameState == null || map == null ||
            map.Width != expectedSideLength || map.Height != expectedSideLength ||
            tileCount != expectedSideLength * expectedSideLength || gameState.PlayerCount != 2)
        {
            throw new InvalidOperationException(
                $"Polytopia could not create the {context} map " +
                $"(result={result}, size={map?.Width}x{map?.Height}, tiles={tileCount}, players={gameState?.PlayerCount})."
            );
        }
        return map;
    }

    private static GameSettings BuildSettings(IntegratedMatch match, LaunchManifest manifest)
    {
        GameSettings settings = new();
        settings.ApplyGameTypeDefaults(GameType.Multiplayer, GameMode.Domination);
        settings.GameName = $"Integrated G{match.BotGameId}";
        settings.GameType = GameType.Multiplayer;
        settings.BaseGameMode = GameMode.Domination;
        settings.RulesGameMode = GameMode.Domination;
        settings.MapSize = TinyDrylandSideLength;
        settings.mapPreset = MapPreset.Dryland;
        settings.OpponentCount = 1;
        settings.ClearPlayers();
        settings.AddPlayer(BuildPlayer(manifest.Players[0].AccountId, manifest.Players[0].DisplayName, manifest.Players[0].Tribe));
        settings.AddPlayer(BuildPlayer(manifest.Players[1].AccountId, manifest.Players[1].DisplayName, manifest.Players[1].Tribe));
        return settings;
    }

    private static void ValidateLaunchManifest(IntegratedMatch match, LaunchManifest manifest)
    {
        if (match.Role != "host" || string.IsNullOrWhiteSpace(match.GameId))
            throw new InvalidOperationException("Only the Discord opener can host this game.");
        if (manifest.SchemaVersion != 1 || manifest.GameId != match.GameId ||
            manifest.RulesetId != RulesetId || manifest.RulesetHash != RulesetHash ||
            manifest.RatingMode is not ("classic" or "modern") ||
            manifest.BaseGameMode != "domination" || manifest.MapPreset != "dryland" ||
            manifest.MapSize != TinyDrylandTileCount || manifest.PlayerCount != 2 ||
            manifest.Seed < 0 || manifest.VillageNameSeed < 0 ||
            manifest.Players.Length != 2)
            throw new InvalidOperationException("The server launch manifest is incompatible with this Better BoP build.");
        LaunchPlayer host = manifest.Players[0];
        LaunchPlayer away = manifest.Players[1];
        if (host.Seat != 1 || host.Role != "host" || away.Seat != 2 || away.Role != "away" ||
            host.AccountId != match.HostAccountId || away.AccountId != match.AwayAccountId ||
            host.Tribe != match.HostTribe || away.Tribe != match.AwayTribe ||
            host.Tribe is < 2 or > 17 || away.Tribe is < 2 or > 17 ||
            !Il2CppSystem.Guid.TryParse(host.AccountId, out _) ||
            !Il2CppSystem.Guid.TryParse(away.AccountId, out _))
            throw new InvalidOperationException("The server launch seats do not match the locked Discord match.");
    }

    internal static void ApplyLaunchSeeds(ref int seed, GameState state)
    {
        LaunchManifest? manifest = pendingLaunchManifest;
        if (manifest == null) return;
        seed = manifest.Seed;
        state.Seed = manifest.Seed;
        state.VillageNameSeed = manifest.VillageNameSeed;
        state.randomHash = new XXHash(manifest.Seed);
        state.villageNameHash = new XXHash(manifest.VillageNameSeed);
    }

    private static PlayerData BuildPlayer(string accountId, string name, int? tribeId)
    {
        TribeType tribe = tribeId.HasValue ? (TribeType)tribeId.Value : TribeType.None;
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
            knownTribe = tribeId.HasValue,
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
        if (!Active || string.IsNullOrWhiteSpace(activeGameId)) return;
        string gameId = activeGameId;
        try
        {
            byte[] serialized = SerializeCommand(command);
            await PostSerializedCommandAsync(gameId, serialized, command is EndTurnCommand).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError($"Integrated command upload failed: {exception}");
        }
    }

    private static async Task PostSerializedCommandAsync(string gameId, byte[] serialized, bool endsTurn)
    {
        await CommandSubmitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Active || activeGameId != gameId) return;
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            if (!Active || activeGameId != gameId) return;
            int commandIndex = nextCommandIndex;
            string payload = JsonSerializer.Serialize(new
            {
                commandIndex,
                serializedData = Convert.ToBase64String(serialized),
                clientStateHash = (string?)null,
                endsTurn,
            });
            using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Post, $"/v1/games/{gameId}/commands", token, payload);
            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(ServerMessage(response, body));
            CommandResponse? result = JsonSerializer.Deserialize<CommandResponse>(body);
            if (result != null)
                await RunOnMainThreadAsync(() =>
                {
                    RefreshActiveSession();
                    if (Active && activeGameId == gameId)
                        nextCommandIndex = Math.Max(nextCommandIndex, result.NextCommandIndex);
                    return true;
                }).ConfigureAwait(false);
        }
        finally
        {
            CommandSubmitLock.Release();
        }
    }

    private static async Task ReceiveCommandsAsync()
    {
        if (!Active || string.IsNullOrWhiteSpace(activeGameId)) return;
        if (!await CommandReceiveLock.WaitAsync(0).ConfigureAwait(false)) return;
        string gameId = activeGameId;
        try
        {
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Get, $"/v1/games/{gameId}/commands?after={nextCommandIndex - 1}", token);
            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            CommandListResponse? list = JsonSerializer.Deserialize<CommandListResponse>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            foreach (RemoteCommand remote in list?.Commands ?? Array.Empty<RemoteCommand>())
            {
                if (remote.CommandIndex < nextCommandIndex) continue;
                byte[] serialized = Convert.FromBase64String(remote.SerializedData);
                bool applied = await RunOnMainThreadAsync(() =>
                {
                    RefreshActiveSession();
                    if (!Active || activeGameId != gameId) return false;
                    CommandBase command = DeserializeCommand(serialized);
                    MethodInfo receive = AccessTools.Method(typeof(ClientBase), "ReceiveCommand", new[] { typeof(CommandBase) });
                    receive.Invoke(GameManager.Client, new object[] { command });
                    nextCommandIndex = Math.Max(nextCommandIndex, remote.CommandIndex + 1);
                    return true;
                }).ConfigureAwait(false);
                if (!applied) return;
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
        if (!Active || string.IsNullOrWhiteSpace(activeGameId) || string.IsNullOrWhiteSpace(winnerAccountId)) return;
        PendingResults[activeGameId] = winnerAccountId;
        await FlushPendingResultsAsync().ConfigureAwait(false);
    }

    private static async Task FlushPendingResultsAsync()
    {
        if (PendingResults.IsEmpty) return;
        if (!await ResultReportLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            foreach (KeyValuePair<string, string> pending in PendingResults.ToArray())
            {
                string gameId = pending.Key;
                string winnerAccountId = pending.Value;
                string token = await EnsureServerTokenAsync().ConfigureAwait(false);
                string payload = JsonSerializer.Serialize(new { winnerAccountId });
                using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Post, $"/v1/games/{gameId}/result", token, payload);
                using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException(ServerMessage(response, body));

                if (PendingResults.TryGetValue(gameId, out string? queuedWinner) && queuedWinner == winnerAccountId)
                    PendingResults.TryRemove(gameId, out _);
                await RunOnMainThreadAsync(() =>
                {
                    RefreshActiveSession();
                    if (activeGameId != gameId) return false;
                    active = false;
                    activeSession = false;
                    activeGameId = string.Empty;
                    activeMatchId = string.Empty;
                    return true;
                }).ConfigureAwait(false);
                logger.LogMessage($"Integrated game {gameId} result was accepted by the Better BoP server.");
                RequestRender();
            }
        }
        catch (Exception exception)
        {
            // Keep pending results in memory for the background retry loop.
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
    private sealed class MatchMutationResponse
    {
        [JsonPropertyName("matchId")] public string MatchId { get; init; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
        [JsonPropertyName("gameId")] public string? GameId { get; init; }
        [JsonPropertyName("hostTribe")] public int? HostTribe { get; init; }
        [JsonPropertyName("awayTribe")] public int? AwayTribe { get; init; }
        [JsonPropertyName("match")] public IntegratedMatch? Match { get; init; }
        [JsonPropertyName("launchManifest")] public LaunchManifest? LaunchManifest { get; init; }
    }
    private sealed class LaunchManifest
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
        [JsonPropertyName("gameId")] public string GameId { get; init; } = string.Empty;
        [JsonPropertyName("seed")] public int Seed { get; init; }
        [JsonPropertyName("villageNameSeed")] public int VillageNameSeed { get; init; }
        [JsonPropertyName("ratingMode")] public string RatingMode { get; init; } = string.Empty;
        [JsonPropertyName("baseGameMode")] public string BaseGameMode { get; init; } = string.Empty;
        [JsonPropertyName("mapPreset")] public string MapPreset { get; init; } = string.Empty;
        [JsonPropertyName("mapSize")] public int MapSize { get; init; }
        [JsonPropertyName("playerCount")] public int PlayerCount { get; init; }
        [JsonPropertyName("rulesetId")] public string RulesetId { get; init; } = string.Empty;
        [JsonPropertyName("rulesetHash")] public string RulesetHash { get; init; } = string.Empty;
        [JsonPropertyName("players")] public LaunchPlayer[] Players { get; init; } = Array.Empty<LaunchPlayer>();
    }
    private sealed class LaunchPlayer
    {
        [JsonPropertyName("seat")] public int Seat { get; init; }
        [JsonPropertyName("role")] public string Role { get; init; } = string.Empty;
        [JsonPropertyName("accountId")] public string AccountId { get; init; } = string.Empty;
        [JsonPropertyName("displayName")] public string DisplayName { get; init; } = string.Empty;
        [JsonPropertyName("tribe")] public int Tribe { get; init; }
    }
    private sealed record IntegratedMatch
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("bot_game_id")] public string BotGameId { get; init; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
        [JsonPropertyName("game_id")] public string? GameId { get; init; }
        [JsonPropertyName("role")] public string Role { get; init; } = string.Empty;
        [JsonPropertyName("map_size")] public int MapSize { get; init; } = TinyDrylandTileCount;
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

[HarmonyPatch]
internal static class ModdedPullRefreshPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MultiplayerScreen), "OnRefreshTrigger");
        yield return AccessTools.Method(typeof(MultiplayerScreen), "OnRefreshGames");
    }

    [HarmonyPrefix]
    private static bool RefreshModdedInstead(MultiplayerScreen __instance) =>
        IntegratedModdedGames.HandlePullRefresh(__instance);
}

/// <summary>
/// Unity invokes GameManager.Update on its main thread in menus and gameplay.
/// This dispatcher prevents HTTP continuations from touching IL2CPP objects.
/// </summary>
[HarmonyPatch(typeof(GameManager), "Update")]
internal static class IntegratedMainThreadPumpPatch
{
    [HarmonyPostfix]
    private static void DrainIntegratedWork()
    {
        IntegratedModdedGames.PumpMainThread();
        HomeVersionLabel.Tick();
    }
}

[HarmonyPatch(typeof(PlayerDataUtils), nameof(PlayerDataUtils.GetPlayerData),
    new[] { typeof(ParticipatorViewModel), typeof(bool) })]
internal static class IntegratedSyntheticPlayerDataPatch
{
    [HarmonyPrefix]
    private static bool UseIntegratedSeat(
        ParticipatorViewModel __0,
        ref PlayerData __result
    ) => !IntegratedModdedGames.TryGetIntegratedPlayerData(__0, out __result);
}

[HarmonyPatch]
internal static class IntegratedTribePickerSubmitPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(TribePickerScreen_UI2), "OnTribePicked");
        yield return AccessTools.Method(typeof(TribeSelectorScreen), "OnPickTribe");
    }

    [HarmonyPrefix]
    private static bool SubmitToIntegratedServer(object __instance) =>
        IntegratedModdedGames.SubmitIntegratedTribe(__instance);
}

[HarmonyPatch]
internal static class IntegratedTribePickerClosePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(TribePickerScreen_UI2), "OnHide");
        yield return AccessTools.Method(typeof(TribeSelectorScreen), "OnDisable");
    }

    [HarmonyPrefix]
    private static void ClearIntegratedSelection() =>
        IntegratedModdedGames.CloseIntegratedTribePicker();
}

[HarmonyPatch(typeof(LobbyPopup), "OnShowPlayerInfo")]
internal static class IntegratedLobbyPlayerPatch
{
    [HarmonyPrefix]
    private static bool OpenIntegratedTribePicker(
        LobbyPopup __instance,
        Il2CppSystem.Nullable<Il2CppSystem.Guid> __1
    ) => IntegratedModdedGames.ShowIntegratedPlayerInfo(__instance, __1);
}

[HarmonyPatch(typeof(LobbyGameInfoRow), nameof(LobbyGameInfoRow.SetData))]
internal static class IntegratedLobbyRowStatePatch
{
    [HarmonyPostfix]
    private static void ShowActualTribeState(LobbyGameInfoRow __instance) =>
        IntegratedModdedGames.RefreshIntegratedLobbyRow(__instance);
}

[HarmonyPatch(typeof(LobbyPopup), "GetBadgeIdForParticipator")]
internal static class IntegratedLobbyBadgePatch
{
    [HarmonyPrefix]
    private static bool HideFalseReadyBadge(
        LobbyPopup __instance,
        ParticipatorViewModel participator,
        ref string __result
    ) => IntegratedModdedGames.GetIntegratedParticipantBadge(__instance, participator, ref __result);
}

[HarmonyPatch]
internal static class IntegratedLobbyPopupStatePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(LobbyPopup), nameof(LobbyPopup.SetData));
        yield return AccessTools.Method(typeof(LobbyPopup), nameof(LobbyPopup.RefreshPopup));
    }

    [HarmonyPostfix]
    private static void KeepDiscordSeatsOnly(LobbyPopup __instance) =>
        IntegratedModdedGames.RefreshIntegratedLobbyPopup(__instance);
}

[HarmonyPatch(typeof(BasicPopupLegacy), nameof(BasicPopupLegacy.RefreshButtonState))]
internal static class IntegratedLobbyButtonStatePatch
{
    [HarmonyPostfix]
    private static void KeepIntegratedActionGated(BasicPopupLegacy __instance)
    {
        LobbyPopup? popup = __instance.TryCast<LobbyPopup>();
        if (popup != null) IntegratedModdedGames.RefreshIntegratedLobbyPopup(popup);
    }
}

[HarmonyPatch(typeof(LobbyPopup), "PrepareGameInfo")]
internal static class IntegratedLobbyDescriptionPatch
{
    [HarmonyPostfix]
    private static void ShowIntegratedSetupState(LobbyPopup __instance, ref string __result) =>
        IntegratedModdedGames.DescribeIntegratedLobby(__instance, ref __result);
}

[HarmonyPatch(typeof(LobbyPopup), "OnStartClicked")]
internal static class IntegratedLobbyStartPatch
{
    [HarmonyPrefix]
    private static bool StartAutomatically(LobbyPopup __instance) =>
        IntegratedModdedGames.BlockIntegratedLobbyAction(__instance, "start");
}

[HarmonyPatch(typeof(LobbyPopup), "OnStartGame")]
internal static class IntegratedLobbyStartGamePatch
{
    [HarmonyPrefix]
    private static bool StartAutomatically(LobbyPopup __instance) =>
        IntegratedModdedGames.BlockIntegratedLobbyAction(__instance, "start");
}

[HarmonyPatch(typeof(LobbyPopup), "OnShowInvitePlayer")]
internal static class IntegratedLobbyInvitePatch
{
    [HarmonyPrefix]
    private static bool KeepDiscordSeats(LobbyPopup __instance) =>
        IntegratedModdedGames.BlockIntegratedLobbyAction(__instance, "invite");
}

[HarmonyPatch(typeof(LobbyPopup), nameof(LobbyPopup.OnLeaveClicked))]
internal static class IntegratedLobbyLeavePatch
{
    [HarmonyPrefix]
    private static bool KeepIntegratedMatch(LobbyPopup __instance) =>
        IntegratedModdedGames.BlockIntegratedLobbyAction(__instance, "leave");
}

[HarmonyPatch(
    typeof(MapGenerator),
    "GenerateWithSeed",
    new[] { typeof(int), typeof(GameState), typeof(MapGeneratorSettings), typeof(Il2CppSystem.Action) }
)]
internal static class IntegratedLaunchSeedPatch
{
    [HarmonyPrefix]
    private static void UseServerManifest(ref int __0, GameState __1) =>
        IntegratedModdedGames.ApplyLaunchSeeds(ref __0, __1);
}

[HarmonyPatch]
internal static class IntegratedSessionChangedPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ClientBase), nameof(ClientBase.CreateSession),
            new[] { typeof(GameSettings), typeof(Il2CppSystem.Guid) });
        yield return AccessTools.Method(typeof(ClientBase), nameof(ClientBase.CreateSession),
            new[] { typeof(Il2CppStructArray<byte>), typeof(Il2CppSystem.Guid) });
    }

    [HarmonyPostfix]
    private static void BindTransportToLoadedGame() => IntegratedModdedGames.RefreshActiveSession();
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
