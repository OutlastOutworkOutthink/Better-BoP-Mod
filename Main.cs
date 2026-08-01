using BepInEx.Logging;
using HarmonyLib;

namespace BetterBoPMod;

/// <summary>
/// Alpha 0.5.7 loads the locked Oblivion baseline plus the isolated Discord
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

        SafePatch(typeof(DiscordAccountLink), logger);

        logger.LogMessage("Better BoP Alpha 0.5.7 loaded: locked Oblivion plus Discord account linking.");
    }

    private static void SafePatch(Type patchType, ManualLogSource logger)
    {
        try
        {
            Harmony.CreateAndPatchAll(patchType);
            logger.LogInfo($"Loaded Oblivion patch: {patchType.Name}");
        }
        catch (Exception exception)
        {
            logger.LogError($"Could not load Oblivion patch {patchType.Name}: {exception}");
        }
    }
}
