using BepInEx.Logging;
using HarmonyLib;
using Polytopia.Data;
using PolytopiaBackendBase.Game;
using System.Reflection;

namespace BetterBoPMod;

/// <summary>
/// Loads the runtime patches needed to keep Better Battle of Polytopia rules
/// active when Polytopia loads fresh game-logic data for an online match.
/// </summary>
public static class Main
{
    private static bool loaded;

    public static void Load(ManualLogSource logger)
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        OnlineGameStatePatch.Logger = logger;
        BetterBoPRules.Logger = logger;
        GiftStars.Initialize(logger);
        DiscordIntegrationPatch.Initialize(logger);
        IntegratedMultiplayer.Initialize(logger);
        Harmony.CreateAndPatchAll(typeof(BetterBoPParsedRulesPatch));
        Harmony.CreateAndPatchAll(typeof(DiplomacyEmbassyIncomePatch));
        Harmony.CreateAndPatchAll(typeof(AlwaysAvailablePeacePatch));
        Harmony.CreateAndPatchAll(typeof(AlwaysAvailablePeaceHasAbilityPatch));
        Harmony.CreateAndPatchAll(typeof(DiplomacyActionButtonRulesPatch));
        Harmony.CreateAndPatchAll(typeof(EmbassyDescriptionPatch));
        Harmony.CreateAndPatchAll(typeof(BetterBoPTechPopupPatch));
        Harmony.CreateAndPatchAll(typeof(AimoDescriptionPatch));
        Harmony.CreateAndPatchAll(typeof(GiftStarsButtonPatch));
        Harmony.CreateAndPatchAll(typeof(GenerousOpinionPatch));
        Harmony.CreateAndPatchAll(typeof(GenerousReasonLabelPatch));
        Harmony.CreateAndPatchAll(typeof(GenerousReasonButtonPatch));
        Harmony.CreateAndPatchAll(typeof(EmbassyIncomeDisplayPatch));
        Harmony.CreateAndPatchAll(typeof(HideLobbyInvitePatch));
        Harmony.CreateAndPatchAll(typeof(BlockLobbyInvitePatch));
        Harmony.CreateAndPatchAll(typeof(BlockManualMultiplayerGamePatch));
        Harmony.CreateAndPatchAll(typeof(BlockRandomMatchPatch));
        Harmony.CreateAndPatchAll(typeof(RedRandomMatchButtonPatch));
        Harmony.CreateAndPatchAll(typeof(OnlineGameStatePatch));
        Harmony.CreateAndPatchAll(typeof(DiscordIntegrationPatch));
        Harmony.CreateAndPatchAll(typeof(IntegratedCommandPatch));
        Harmony.CreateAndPatchAll(typeof(IntegratedResultPatch));
        logger.LogMessage("Better Battle of Polytopia Mod multiplayer and Discord integration hooks loaded.");
    }
}

/// <summary>
/// Covers the other online path: a fully constructed state received from the
/// Polytopia backend. The state must contain the same rules before commands are
/// replayed or displayed by the client.
/// </summary>
[HarmonyPatch]
internal static class OnlineGameStatePatch
{
    internal static ManualLogSource Logger { get; set; } = null!;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredMethods(typeof(ClientBase)).Where(method =>
            (method.Name == nameof(ClientBase.UpdateGameState) ||
             method.Name == nameof(ClientBase.UpdateGameStateImmediate)) &&
            method.GetParameters().Length > 0 &&
            method.GetParameters()[0].ParameterType == typeof(GameState)
        );
    }

    [HarmonyPrefix]
    private static void ApplyToReceivedState(GameState __0)
    {
        try
        {
            BetterBoPRules.Apply(__0.GameLogicData);
            Logger.LogInfo("Applied Better BoP rules to received online game state.");
        }
        catch (Exception exception)
        {
            Logger.LogError($"Failed to patch a received online state: {exception}");
        }
    }
}
