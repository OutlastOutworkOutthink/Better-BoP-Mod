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
        // UI entry points are registered first. Every patch is isolated so an
        // incompatible gameplay hook can never make Connect Discord or Gift Stars
        // disappear again.
        SafePatch(typeof(DiscordIntegrationPatch), logger);
        SafePatch(typeof(GiftStarsButtonPatch), logger);
        SafePatch(typeof(EmbassyIncomeDisplayPatch), logger);
        SafePatch(typeof(BetterBoPTechPopupPatch), logger);
        SafePatch(typeof(BetterBoPTechTreeUnlockIconPatch), logger);
        SafePatch(typeof(BetterBoPTechInfoTextPatch), logger);
        SafePatch(typeof(GrowGiantTechPopupPatch), logger);

        SafePatch(typeof(BetterBoPParsedRulesPatch), logger);
        SafePatch(typeof(GrowGiantUnlockPatch), logger);
        SafePatch(typeof(GrowGiantBuildRulesPatch), logger);
        SafePatch(typeof(GrowGiantStartTurnPatch), logger);
        SafePatch(typeof(GiantSeedDisplayNamePatch), logger);
        SafePatch(typeof(GiantSeedImprovementIconPatch), logger);
        SafePatch(typeof(GiantSeedWorldVisualPatch), logger);
        SafePatch(typeof(DiplomacyEmbassyIncomePatch), logger);
        SafePatch(typeof(AlwaysAvailablePeacePatch), logger);
        SafePatch(typeof(AlwaysAvailablePeaceHasAbilityPatch), logger);
        SafePatch(typeof(DiplomacyActionButtonRulesPatch), logger);
        SafePatch(typeof(EmbassyActionButtonRegistryResetPatch), logger);
        SafePatch(typeof(EmbassyActionPointerClickPatch), logger);
        SafePatch(typeof(EmbassyActionButtonClickPatch), logger);
        SafePatch(typeof(EmbassyUnavailableRequirementPatch), logger);
        SafePatch(typeof(EmbassyDescriptionPatch), logger);
        SafePatch(typeof(AimoDescriptionPatch), logger);
        SafePatch(typeof(DisableVanillaDominatingPatch), logger);
        SafePatch(typeof(ClearStoredVanillaDominatingPatch), logger);
        SafePatch(typeof(BetterBoPOpinionValuePatch), logger);
        SafePatch(typeof(RemoveVanillaDominatingReasonPatch), logger);
        SafePatch(typeof(BetterBoPOpinionReasonLabelPatch), logger);
        SafePatch(typeof(BetterBoPOpinionReasonButtonPatch), logger);
        SafePatch(typeof(BetterBoPOpinionReasonClickPatch), logger);
        SafePatch(typeof(OblivionCreativeModeListPatch), logger);
        SafePatch(typeof(OblivionCreativeModeSelectionPatch), logger);
        SafePatch(typeof(OblivionCreativeModeDescriptionPatch), logger);
        SafePatch(typeof(OblivionMainModeResetPatch), logger);
        SafePatch(typeof(OblivionCreativeModeListUI2Patch), logger);
        SafePatch(typeof(OblivionCreativeModeSelectionUI2Patch), logger);
        SafePatch(typeof(OblivionNewGameArmPatch), logger);
        SafePatch(typeof(OblivionNewGameReadyPatch), logger);
        SafePatch(typeof(HideLobbyInvitePatch), logger);
        SafePatch(typeof(BlockLobbyInvitePatch), logger);
        SafePatch(typeof(BlockManualMultiplayerGamePatch), logger);
        SafePatch(typeof(BlockRandomMatchPatch), logger);
        SafePatch(typeof(RedRandomMatchButtonPatch), logger);
        SafePatch(typeof(OnlineGameStatePatch), logger);
        SafePatch(typeof(IntegratedCommandPatch), logger);
        SafePatch(typeof(IntegratedResultPatch), logger);
        logger.LogMessage("Better Battle of Polytopia Mod multiplayer and Discord integration hooks loaded.");
    }

    private static void SafePatch(Type patchType, ManualLogSource logger)
    {
        try
        {
            Harmony.CreateAndPatchAll(patchType);
            logger.LogInfo($"Loaded Better BoP patch: {patchType.Name}");
        }
        catch (Exception exception)
        {
            logger.LogError($"Could not load Better BoP patch {patchType.Name}: {exception}");
        }
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
