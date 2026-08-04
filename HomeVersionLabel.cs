using BepInEx.Logging;
using HarmonyLib;

namespace BetterBoPMod;

/// <summary>
/// Shows the Better BoP build by reusing Polytopia's existing About label.
/// No Unity objects or components are created, so this cannot interrupt the
/// native StartScreen lifecycle.
/// </summary>
internal static class HomeVersionLabel
{
    internal const string DisplayText = "BBoP Alpha 0.6.2";
    private static ManualLogSource logger = null!;
    private static bool missingLabelLogged;

    internal static void Initialize(ManualLogSource logSource) => logger = logSource;

    internal static void Apply(StartScreen_UI2? screen)
    {
        try
        {
            var field = screen?.aboutButton?.titleTextField?.textField;
            if (field == null)
            {
                if (!missingLabelLogged)
                {
                    missingLabelLogged = true;
                    logger.LogWarning("Home screen has no native About label; version text skipped safely.");
                }
                return;
            }

            string label = $"About\n{DisplayText}";
            if (string.Equals(field.text, label, StringComparison.Ordinal)) return;

            field.text = label;
            logger.LogInfo($"Displayed {DisplayText} in the native About label.");
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Could not update the optional home version text: {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(StartScreen_UI2), "OnShowAfterLayout")]
internal static class HomeVersionShowPatch
{
    [HarmonyPostfix]
    private static void ShowVersion(StartScreen_UI2 __instance) => HomeVersionLabel.Apply(__instance);
}
