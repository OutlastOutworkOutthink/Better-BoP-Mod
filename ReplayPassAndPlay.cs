using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BetterBoPMod;

/// <summary>
/// Creates a local what-if branch from the exact frame of a replay. The stock
/// pass-and-play client owns turns, reactions and camera hand-offs; this class
/// only snapshots/restores the replay and swaps the two replay controls.
/// </summary>
internal static class ReplayPassAndPlay
{
    private static readonly Type WrapperType = typeof(ClientSerializationWrapper);
    private static readonly MethodInfo EncodeMethod = typeof(DiskSerializationHelpers)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == "ToLZ4CompressedByteArray" &&
                          method.IsGenericMethodDefinition && method.GetParameters().Length == 2)
        .MakeGenericMethod(WrapperType);
    private static readonly MethodInfo DecodeMethod = typeof(DiskSerializationHelpers)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == "FromLZ4CompressedByteArray" &&
                          method.IsGenericMethodDefinition && method.GetParameters().Length == 3)
        .MakeGenericMethod(WrapperType);

    private static ManualLogSource logger = null!;
    private static ReplayClient? restoreClient;
    private static bool active;
    private static bool branching;
    private static bool restoring;

    internal static bool Active => active;
    internal static void Initialize(ManualLogSource logSource) => logger = logSource;

    internal static bool ReadyToBranch
    {
        get
        {
            ClientBase? client = GameManager.Client;
            return !active && !branching && !restoring && client?.IsReplay == true;
        }
    }

    internal static bool CanReceive(CommandBase? command, GameState? state)
    {
        return ReadyToBranch && state != null && IsTrigger(command) &&
               command!.PlayerId == state.CurrentPlayer &&
               command.IsValid(state);
    }

    internal static bool TryBeginFromInput(string source) =>
        TryBegin(GameManager.Client, source);

    internal static bool MoveFromReplay(UnitState replayUnit, WorldCoordinates destination)
    {
        if (replayUnit == null) return false;
        if (!TryBegin(GameManager.Client, "move-target")) return restoring;

        try
        {
            GameState state = GameManager.GameState;
            if (!state.TryGetUnit(replayUnit.id, out UnitState branchUnit) || branchUnit == null)
                throw new InvalidOperationException($"Unit {replayUnit.id} was missing from the replay branch.");

            MoveCommand command = new(state.CurrentPlayer, branchUnit, destination);
            if (!command.IsValid(state))
                throw new InvalidOperationException($"Move for unit {replayUnit.id} became invalid after branching.");

            GameManager.Client.SendCommand(command);
        }
        catch (Exception exception)
        {
            logger.LogError($"Could not apply the first replay-branch move: {exception}");
            ReturnToReplay();
        }

        return true;
    }

    internal static bool RouteCommand(ClientBase source, CommandBase command)
    {
        if (active)
        {
            if (source.clientType == ClientBase.ClientType.PassAndPlay) return true;
            TrySendOnBranch(command);
            return false;
        }

        if (!source.IsReplay || !IsTrigger(command)) return true;
        if (!TryBegin(source, command is MoveCommand ? "move-command" : "research-command"))
            return !restoring;

        TrySendOnBranch(command);
        return false;
    }

    private static void TrySendOnBranch(CommandBase command)
    {
        try
        {
            GameState state = GameManager.GameState;
            CommandBase mapped = command;
            if (command is MoveCommand move)
            {
                if (!state.TryGetUnit(move.UnitId, out UnitState unit) || unit == null)
                    throw new InvalidOperationException($"Unit {move.UnitId} was missing from the replay branch.");
                mapped = new MoveCommand(state.CurrentPlayer, unit, move.To);
            }
            else if (command is ResearchCommand research)
            {
                mapped = new ResearchCommand(state.CurrentPlayer, research.Type);
            }

            if (!mapped.IsValid(state))
                throw new InvalidOperationException($"{mapped.GetType().Name} became invalid after branching.");
            GameManager.Client.SendCommand(mapped);
        }
        catch (Exception exception)
        {
            logger.LogError($"Could not apply the first replay-branch command: {exception}");
            ReturnToReplay();
        }
    }

    private static bool TryBegin(ClientBase? client, string source)
    {
        if (!ReadyToBranch || client == null || !client.IsReplay) return false;

        branching = true;
        try
        {
            Timeline? timeline = UnityEngine.Object.FindObjectOfType<ReplayInterface>()?.timeline;
            timeline?.Pause();
            if (timeline?.isSimulating == true || client.ActionManager.IsSimulating)
            {
                logger.LogWarning("Replay branch input was ignored while the replay was seeking.");
                return false;
            }

            Il2CppStructArray<byte> snapshot = CaptureSnapshot(client);
            restoreClient = DecodeSnapshot(snapshot) as ReplayClient ??
                throw new InvalidOperationException("Replay snapshot did not restore as a replay client.");

            ClientBase stateClone = DecodeSnapshot(snapshot);
            if (stateClone.GameState.CurrentTurn != client.GameState.CurrentTurn ||
                stateClone.GameState.CurrentCommand != client.GameState.CurrentCommand ||
                stateClone.GameState.CurrentPlayer != client.GameState.CurrentPlayer)
                throw new InvalidOperationException("Replay snapshot did not preserve the selected position.");

            byte player = stateClone.GameState.CurrentPlayer;
            ClientBase branch = ClientBase.CreateFakeSession(stateClone.GameState, player) ??
                throw new InvalidOperationException("Polytopia did not create a local branch client.");
            branch.clientType = ClientBase.ClientType.PassAndPlay;
            branch.doAutoSwitchPlayers = true;
            GameManager.Instance.fakeSession = branch;
            active = true;
            branch.SetNewLocalPlayerTurnForPassAndPlay(player);
            if (branch.ActionManager.IsRecap) branch.ActionManager.EndRecap();
            branch.ActionManager.Resume();
            if (GameManager.Client?.clientType != ClientBase.ClientType.PassAndPlay)
                throw new InvalidOperationException("Polytopia did not activate the local replay branch.");
            logger.LogMessage(
                $"Replay branch started from {source} at turn {branch.GameState.CurrentTurn}, " +
                $"command {branch.GameState.CurrentCommand}, player {branch.GameState.CurrentPlayer}."
            );
        }
        catch (Exception exception)
        {
            logger.LogError($"Could not start replay pass-and-play: {exception}");
            RestoreAfterFailedStart();
            return false;
        }
        finally
        {
            branching = false;
        }

        try { RefreshHud(); }
        catch (Exception exception) { logger.LogWarning($"Could not refresh replay-branch controls: {exception.Message}"); }
        return true;
    }

    internal static void ReturnToReplay()
    {
        if (!active || restoreClient == null) return;

        ClientBase? branch = GameManager.Instance.fakeSession;
        try
        {
            ReplayClient replay = restoreClient;
            active = false;
            restoring = true;
            GameManager.ClearFakeSession();
            GameManager.Instance.SetReplayClient(replay);
            GameManager.Instance.LoadLevel();
            logger.LogMessage(
                $"Returned to replay turn {replay.GameState.CurrentTurn}, " +
                $"command {replay.GameState.CurrentCommand}."
            );
        }
        catch (Exception exception)
        {
            if (branch != null) GameManager.Instance.fakeSession = branch;
            active = branch != null;
            restoring = false;
            logger.LogError($"Could not return to the replay snapshot: {exception}");
        }
    }

    internal static void OnGameReady()
    {
        if (restoring)
        {
            if (GameManager.Client?.IsReplay != true) return;
            restoring = false;
            restoreClient = null;
            UnityEngine.Object.FindObjectOfType<ReplayInterface>()?.timeline?.Pause();
            return;
        }

        if (active && GameManager.Client?.clientType != ClientBase.ClientType.PassAndPlay)
        {
            active = false;
            restoreClient = null;
        }
    }

    internal static void Configure(ReplayInterface? replayInterface)
    {
        if (!active || replayInterface == null) return;

        Timeline? timeline = replayInterface.timeline;
        if (timeline != null)
        {
            timeline.Pause();
            timeline.gameObject.SetActive(false);
        }

        UIRoundButton? back = replayInterface.viewmodeSelectButton;
        if (back == null) return;
        back.gameObject.SetActive(true);
        back.ClearCallbacks();
        back.ButtonEnabled = true;
        back.BlockButton = false;
        back.buttonActive = true;
        back.SetSprite(GameManager.GetSpriteAtlasManager().GetSprite(SpriteRef.UI_BACK), false);
        back.SetText("Back");
        back.OnClickedSignal.Add(
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(ReturnToReplay)
        );

        RectTransform transform = back.rectTransform;
        transform.anchorMin = new Vector2(0f, 1f);
        transform.anchorMax = new Vector2(0f, 1f);
        transform.pivot = new Vector2(0f, 1f);
        transform.anchoredPosition = new Vector2(26f, -26f);
    }

    private static void RefreshHud()
    {
        UIEvents.ForceRefreshHud();
        HudScreen? hud = UnityEngine.Object.FindObjectOfType<HudScreen>();
        hud?.buttonBar?.RefreshComponents();
        hud?.buttonBar?.RefreshNextTurnButton();
        Configure(UnityEngine.Object.FindObjectOfType<ReplayInterface>());
    }

    private static Il2CppStructArray<byte> CaptureSnapshot(ClientBase source)
    {
        ClientSerializationWrapper wrapper = new(source);
        return EncodeMethod.Invoke(
            null,
            new object[] { wrapper, source.GameState.Version }
        ) as Il2CppStructArray<byte> ??
            throw new InvalidOperationException("Replay snapshot encoding returned no data.");
    }

    private static ClientBase DecodeSnapshot(Il2CppStructArray<byte> encoded)
    {
        object[] arguments =
        {
            encoded,
            new ClientSerializationWrapper(),
            0,
        };
        if (DecodeMethod.Invoke(null, arguments) is not true)
            throw new InvalidOperationException("Replay snapshot decoding failed.");

        return ((ClientSerializationWrapper)arguments[1]).GetDeserializedClient() ??
               throw new InvalidOperationException("Replay snapshot restored without a client.");
    }

    private static bool IsTrigger(CommandBase? command) =>
        command is MoveCommand or ResearchCommand;

    private static void RestoreAfterFailedStart()
    {
        active = false;
        branching = false;
        if (restoreClient == null)
        {
            try { GameManager.ClearFakeSession(); }
            catch (Exception exception)
            {
                logger.LogWarning($"Could not clear a partial replay branch: {exception.Message}");
            }
            return;
        }

        try
        {
            restoring = true;
            GameManager.ClearFakeSession();
            GameManager.Instance.SetReplayClient(restoreClient);
            GameManager.Instance.LoadLevel();
        }
        catch (Exception exception)
        {
            restoring = false;
            try { GameManager.ClearFakeSession(); }
            catch { }
            logger.LogError($"Could not restore replay after branch startup failed: {exception}");
        }
        finally
        {
            if (!restoring) restoreClient = null;
        }
    }
}

[HarmonyPatch(typeof(ClientInteraction), nameof(ClientInteraction.SelectTile), new[] { typeof(Tile) })]
internal static class ReplayPassAndPlayMoveIntentPatch
{
    [HarmonyPrefix]
    private static bool StartBeforeMove(ClientInteraction __instance, Tile __0)
    {
        if (!ReplayPassAndPlay.ReadyToBranch || __0 == null) return true;
        Unit? unit = __instance.selectedUnit;
        if (unit == null || unit.UnitState.owner != GameManager.GameState.CurrentPlayer ||
            !unit.CanMoveTo(__0.Coordinates)) return true;

        UnitState replayUnit = unit.UnitState;
        if (!ReplayPassAndPlay.MoveFromReplay(replayUnit, __0.Coordinates)) return true;
        if (ReplayPassAndPlay.Active) __instance.ClearSelection();
        return false;
    }
}

[HarmonyPatch(typeof(TechItem), nameof(TechItem.RefreshState), new[] { typeof(bool) })]
internal static class ReplayPassAndPlayTechStatePatch
{
    [HarmonyPrefix]
    private static void AllowReplayResearch(ref bool __0)
    {
        if (ReplayPassAndPlay.ReadyToBranch) __0 = false;
    }
}

[HarmonyPatch(typeof(TechItem), "OnClicked", new[] { typeof(int), typeof(BaseEventData) })]
internal static class ReplayPassAndPlayTechIntentPatch
{
    [HarmonyPrefix]
    private static void StartBeforeResearchPopup(TechItem __instance)
    {
        if (!ReplayPassAndPlay.ReadyToBranch) return;
        GameState state = GameManager.GameState;
        ResearchCommand intent = new(state.CurrentPlayer, __instance.TechData.type);
        if (intent.IsValid(state) && ReplayPassAndPlay.TryBeginFromInput("tech-click"))
            __instance.RefreshState(false);
    }
}

[HarmonyPatch(typeof(ClientActionManager), nameof(ClientActionManager.CanReceiveCommand))]
internal static class ReplayPassAndPlayInputPatch
{
    [HarmonyPostfix]
    private static void AllowReplayBranch(CommandBase __0, GameState __1, ref bool __result)
    {
        if (!__result) __result = ReplayPassAndPlay.CanReceive(__0, __1);
    }
}

[HarmonyPatch(typeof(ClientBase), nameof(ClientBase.SendCommand), new[] { typeof(CommandBase) })]
internal static class ReplayPassAndPlayCommandPatch
{
    [HarmonyPrefix]
    private static bool RouteBranchCommand(ClientBase __instance, CommandBase __0) =>
        ReplayPassAndPlay.RouteCommand(__instance, __0);
}

[HarmonyPatch(typeof(HudScreen), nameof(HudScreen.ShouldShowReplayInterface))]
internal static class ReplayPassAndPlayHudPatch
{
    [HarmonyPostfix]
    private static void KeepBackButton(ref bool __result) => __result |= ReplayPassAndPlay.Active;
}

[HarmonyPatch]
internal static class ReplayPassAndPlayUiPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ReplayInterface), nameof(ReplayInterface.OnEnable));
        yield return AccessTools.Method(typeof(ReplayInterface), nameof(ReplayInterface.SetData));
        yield return AccessTools.Method(typeof(ReplayInterface), nameof(ReplayInterface.OnForceRefreshHud));
    }

    [HarmonyPostfix]
    private static void ShowBackButton(ReplayInterface __instance) =>
        ReplayPassAndPlay.Configure(__instance);
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.OnGameReady))]
internal static class ReplayPassAndPlayReadyPatch
{
    [HarmonyPostfix]
    private static void RestoreReplayPause() => ReplayPassAndPlay.OnGameReady();
}

[HarmonyPatch]
internal static class ReplayPassAndPlayNoSavePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(
            typeof(ClientBase),
            nameof(ClientBase.SaveSession),
            new[] { typeof(string), typeof(bool), typeof(bool) }
        );
        yield return AccessTools.Method(
            typeof(ClientBase),
            nameof(ClientBase.SaveHotSeatGameState),
            new[] { typeof(string), typeof(Il2CppStructArray<byte>), typeof(bool) }
        );
    }

    [HarmonyPrefix]
    private static bool KeepBranchInMemory() => !ReplayPassAndPlay.Active;
}
