using BepInEx.Logging;
using HarmonyLib;

namespace BetterBoPMod;

/// <summary>
/// Alpha 0.5.19 keeps the locked gameplay baselines and fixes stock-list
/// rendering for automatically created Integrated Modded games.
/// </summary>
public static class Main
{
    private static bool loaded;

    public static void Load(ManualLogSource logger)
    {
        if (loaded) return;
        loaded = true;

        OblivionMode.Logger = logger;
        DiscordAccountLink.Initialize(logger);
        IntegratedModdedGames.Initialize(logger);
        UniversalPeaceRules.Logger = logger;

        SafePatch(typeof(OblivionCreativeModeListPatch), logger);
        SafePatch(typeof(OblivionClassicRenderedRowPatch), logger);
        SafePatch(typeof(OblivionCreativeModeSelectionPatch), logger);
        SafePatch(typeof(OblivionCreativeModeDescriptionPatch), logger);
        SafePatch(typeof(OblivionMainModeResetPatch), logger);
        SafePatch(typeof(OblivionMainModeResetUI2Patch), logger);
        SafePatch(typeof(OblivionCreativeModeListUI2Patch), logger);
        SafePatch(typeof(OblivionGameModeViewFallbackPatch), logger);
        SafePatch(typeof(OblivionLateUI2LayoutPatch), logger);
        SafePatch(typeof(OblivionLateViewLayoutPatch), logger);
        SafePatch(typeof(OblivionCreativeModeSelectionUI2Patch), logger);
        SafePatch(typeof(OblivionNewGameArmPatch), logger);
        SafePatch(typeof(OblivionSetupStartArmPatch), logger);
        SafePatch(typeof(OblivionNewGameReadyPatch), logger);
        SafePatch(typeof(OblivionOpinionValuePatch), logger);
        SafePatch(typeof(OblivionPlayerOpinionValuePatch), logger);
        SafePatch(typeof(OblivionOpinionStoragePatch), logger);
        SafePatch(typeof(OblivionAIMovePatch), logger);
        SafePatch(typeof(OblivionAIPeacePatch), logger);
        SafePatch(typeof(OblivionAIDiplomacyCommandPatch), logger);
        SafePatch(typeof(OblivionEnemyReasonLabelPatch), logger);
        SafePatch(typeof(OblivionPopupRelationTextPatch), logger);
        SafePatch(typeof(OblivionPopupRelationSliderPatch), logger);
        SafePatch(typeof(OblivionEnemyReasonButtonPatch), logger);
        SafePatch(typeof(OblivionEnemyReasonClickPatch), logger);

        SafePatch(typeof(DiscordProfileStartPatch), logger);
        SafePatch(typeof(DiscordProfileEnablePatch), logger);
        SafePatch(typeof(DiscordProfileValuesPatch), logger);
        SafePatch(typeof(DiscordProfileScreenUpdatedPatch), logger);
        SafePatch(typeof(DiscordProfileRefreshUserPatch), logger);
        SafePatch(typeof(DiscordProfileLanguagePatch), logger);
        SafePatch(typeof(DiscordProfileSubscribePatch), logger);
        SafePatch(typeof(DiscordProfileRefreshPatch), logger);
        SafePatch(typeof(DiscordUILibraryReadyPatch), logger);
        SafePatch(typeof(DiscordRoundButtonLayoutPatch), logger);
        SafePatch(typeof(DiscordRoundButtonEnablePatch), logger);
        SafePatch(typeof(DiscordControllerClickPatch), logger);

        SafePatch(typeof(ModdedTabAwakePatch), logger);
        SafePatch(typeof(ModdedTabShowPatch), logger);
        SafePatch(typeof(ModdedTabEnablePatch), logger);
        SafePatch(typeof(ModdedTabDisablePatch), logger);
        SafePatch(typeof(ModdedTabLateLifecyclePatch), logger);
        SafePatch(typeof(ModdedTabSetDataPatch), logger);
        SafePatch(typeof(ModdedTabListReadyPatch), logger);
        SafePatch(typeof(ModdedTabSelectionPatch), logger);
        SafePatch(typeof(ModdedListBuildPatch), logger);
        SafePatch(typeof(IntegratedMainThreadPumpPatch), logger);
        SafePatch(typeof(IntegratedModdedCommandPatch), logger);
        SafePatch(typeof(IntegratedModdedResultPatch), logger);

        SafePatch(typeof(UniversalPeaceParsedRulesPatch), logger);
        SafePatch(typeof(UniversalPeaceAbilityUnlockPatch), logger);
        SafePatch(typeof(UniversalPeaceHasAbilityPatch), logger);
        SafePatch(typeof(UniversalPeaceUnlockedAbilitiesPatch), logger);
        SafePatch(typeof(UniversalPeaceRequiredTechPatch), logger);
        SafePatch(typeof(UniversalPeaceTechTreePatch), logger);
        SafePatch(typeof(UniversalPeaceTechPopupPatch), logger);
        SafePatch(typeof(UniversalPeaceDiplomacyButtonPatch), logger);
        SafePatch(typeof(UniversalPeaceButtonRegistryResetPatch), logger);
        SafePatch(typeof(UniversalPeacePointerClickPatch), logger);
        SafePatch(typeof(UniversalPeaceButtonClickPatch), logger);
        SafePatch(typeof(UniversalPeaceUnavailablePopupPatch), logger);
        SafePatch(typeof(UniversalPeaceAIPreparePatch), logger);

        logger.LogMessage("Better BoP Alpha 0.5.19 loaded: locked gameplay baselines plus automatically created Integrated Modded games.");
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
