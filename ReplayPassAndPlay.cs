using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System.Reflection;
using UnityEngine;

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
    private static bool restoring;

    internal static bool Active => active;
    internal static void Initialize(ManualLogSource logSource) => logger = logSource;

    internal static bool CanReceive(CommandBase? command, GameState? state)
    {
        ClientBase? client = GameManager.Client;
        return !active && client != null && client.IsReplay && state != null &&
               IsTrigger(command) && command!.PlayerId == state.CurrentPlayer &&
               !client.ActionManager.IsProcessing && !client.ActionManager.IsSimulating &&
               command.IsValid(state);
    }

    internal static void Begin(ClientBase client, CommandBase command)
    {
        if (active || !client.IsReplay || !IsTrigger(command)) return;

        try
        {
            restoreClient = CloneReplay(client);
            client.ClearTargetState();
            client.clientType = ClientBase.ClientType.PassAndPlay;
            client.doAutoSwitchPlayers = true;
            client.SetNewLocalPlayerTurnForPassAndPlay(client.GameState.CurrentPlayer);
            if (client.ActionManager.IsRecap) client.ActionManager.EndRecap();
            client.ActionManager.Resume();
            active = true;
            logger.LogMessage(
                $"Replay branch started at turn {client.GameState.CurrentTurn}, " +
                $"command {client.GameState.CurrentCommand}, player {client.GameState.CurrentPlayer}."
            );
        }
        catch (Exception exception)
        {
            logger.LogError($"Could not start replay pass-and-play: {exception}");
            RestoreAfterFailedStart();
            return;
        }

        try { RefreshHud(); }
        catch (Exception exception) { logger.LogWarning($"Could not refresh replay-branch controls: {exception.Message}"); }
    }

    internal static void ReturnToReplay()
    {
        if (!active || restoreClient == null) return;

        try
        {
            ReplayClient replay = restoreClient;
            active = false;
            restoring = true;
            restoreClient = null;
            GameManager.Instance.SetReplayClient(replay);
            GameManager.Instance.LoadLevel();
            logger.LogMessage(
                $"Returned to replay turn {replay.GameState.CurrentTurn}, " +
                $"command {replay.GameState.CurrentCommand}."
            );
        }
        catch (Exception exception)
        {
            restoring = false;
            logger.LogError($"Could not return to the replay snapshot: {exception}");
        }
    }

    internal static void OnGameReady()
    {
        if (active && GameManager.Client?.clientType != ClientBase.ClientType.PassAndPlay)
        {
            active = false;
            restoreClient = null;
        }
        if (!restoring) return;
        restoring = false;
        UnityEngine.Object.FindObjectOfType<ReplayInterface>()?.timeline?.Pause();
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

    private static ReplayClient CloneReplay(ClientBase source)
    {
        ClientSerializationWrapper wrapper = new(source);
        object encoded = EncodeMethod.Invoke(
            null,
            new object[] { wrapper, source.GameState.Version }
        ) ?? throw new InvalidOperationException("Replay snapshot encoding returned no data.");

        object[] arguments =
        {
            encoded,
            new ClientSerializationWrapper(),
            0,
        };
        if (DecodeMethod.Invoke(null, arguments) is not true)
            throw new InvalidOperationException("Replay snapshot decoding failed.");

        ClientBase clone = ((ClientSerializationWrapper)arguments[1]).GetDeserializedClient();
        return clone as ReplayClient ??
               throw new InvalidOperationException($"Replay snapshot restored as {clone?.GetType().Name ?? "null"}.");
    }

    private static bool IsTrigger(CommandBase? command) =>
        command is MoveCommand or ResearchCommand;

    private static void RestoreAfterFailedStart()
    {
        active = false;
        if (restoreClient == null) return;

        try
        {
            restoring = true;
            GameManager.Instance.SetReplayClient(restoreClient);
            GameManager.Instance.LoadLevel();
        }
        catch (Exception exception)
        {
            logger.LogError($"Could not restore replay after branch startup failed: {exception}");
        }
        finally
        {
            restoreClient = null;
        }
    }
}

[HarmonyPatch(typeof(ClientActionManager), nameof(ClientActionManager.CanReceiveCommand))]
internal static class ReplayPassAndPlayInputPatch
{
    [HarmonyPostfix]
    private static void AllowReplayBranch(CommandBase command, GameState gameState, ref bool __result)
    {
        if (!__result) __result = ReplayPassAndPlay.CanReceive(command, gameState);
    }
}

[HarmonyPatch(typeof(ClientBase), nameof(ClientBase.SendCommand), new[] { typeof(CommandBase) })]
internal static class ReplayPassAndPlayCommandPatch
{
    [HarmonyPrefix]
    private static void StartBranch(ClientBase __instance, CommandBase command) =>
        ReplayPassAndPlay.Begin(__instance, command);
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

[HarmonyPatch(typeof(ClientBase), nameof(ClientBase.SendCommandRemote), new[] { typeof(CommandBase) })]
[HarmonyPriority(Priority.First)]
internal static class ReplayPassAndPlayNoNetworkPatch
{
    [HarmonyPrefix]
    private static bool KeepBranchLocal() => !ReplayPassAndPlay.Active;
}
