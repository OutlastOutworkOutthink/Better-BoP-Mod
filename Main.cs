using BepInEx.Logging;
using HarmonyLib;

namespace BetterBoPMod;

/// <summary>
/// Alpha 0.5.9 loads the locked Oblivion baseline plus the isolated Discord
/// account-link control. Older experiments remain archived and excluded.
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

        logger.LogMessage("Better BoP Alpha 0.5.9 loaded: locked Oblivion plus focus-safe Discord account linking.");
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
