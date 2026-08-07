using BepInEx.Logging;
using HarmonyLib;

namespace BetterBoPMod;

/// <summary>
/// Alpha 0.6.8 keeps the locked gameplay baselines and removes all hooks from
/// Polytopia's native home-screen lifecycle after Alpha 0.6.2 still caused a
/// native IL2CPP crash immediately after StartScreen.Init().
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
        AdvancedMatchSettings.Initialize(logger);

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
        SafePatch(typeof(IntegratedLobbyPlayerPatch), logger);
        SafePatch(typeof(IntegratedLobbyRowStatePatch), logger);
        SafePatch(typeof(IntegratedLobbyBadgePatch), logger);
        SafePatch(typeof(IntegratedLobbyPopupStatePatch), logger);
        SafePatch(typeof(IntegratedLobbyButtonStatePatch), logger);
        SafePatch(typeof(IntegratedLobbyDescriptionPatch), logger);
        SafePatch(typeof(IntegratedLobbyStartPatch), logger);
        SafePatch(typeof(IntegratedLobbyStartGamePatch), logger);
        SafePatch(typeof(IntegratedLobbyInvitePatch), logger);
        SafePatch(typeof(IntegratedLobbyLeavePatch), logger);
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

        SafePatch(typeof(AdvancedSettingsOnShowPatch), logger);
        SafePatch(typeof(AdvancedSettingsOnHidePatch), logger);
        SafePatch(typeof(AdvancedSettingsLayoutPatch), logger);
        SafePatch(typeof(AdvancedSettingsViewLayoutPatch), logger);
        SafePatch(typeof(AdvancedSettingsDragSelectionPatch), logger);
        SafePatch(typeof(AdvancedSettingsDragReleasePatch), logger);
        SafePatch(typeof(AdvancedSettingsSingleplayerStartPatch), logger);
        SafePatch(typeof(AdvancedSettingsMultiplayerStartPatch), logger);
        SafePatch(typeof(AdvancedSettingsMatchmakingStartPatch), logger);
        SafePatch(typeof(AdvancedSettingsCreateSessionPatch), logger);
        SafePatch(typeof(AdvancedSettingsOpenSessionPatch), logger);
        SafePatch(typeof(AdvancedSettingsGameReadyPatch), logger);
        SafePatch(typeof(AdvancedUnitCostUiPatch), logger);
        SafePatch(typeof(AdvancedUnitCostValidationPatch), logger);
        SafePatch(typeof(AdvancedUnitCostExecutionPatch), logger);
        SafePatch(typeof(AdvancedBuildingCostUiPatch), logger);
        SafePatch(typeof(AdvancedBuildingCostValidationPatch), logger);
        SafePatch(typeof(AdvancedBuildingCostExecutionPatch), logger);
        SafePatch(typeof(AdvancedEnemyHealthPatch), logger);
        SafePatch(typeof(AdvancedEnemySpawnHealthPatch), logger);
        SafePatch(typeof(AdvancedConvertedUnitHealthPatch), logger);
        logger.LogMessage("Better BoP Alpha 0.6.8 loaded: lifecycle-safe advanced settings with native drag selection.");
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
