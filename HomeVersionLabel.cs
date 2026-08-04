using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterBoPMod;

/// <summary>
/// Adds a self-contained version label after the home screen has initialized.
/// It never scans the UI hierarchy, clones a game component, or triggers the
/// stock layout engine.
/// </summary>
internal static class HomeVersionLabel
{
    internal const string DisplayText = "BBoP Alpha 0.6.0";
    private const string ObjectName = "BetterBoP.HomeVersion";
    private static readonly Dictionary<IntPtr, TextMeshProUGUI> LabelsByScreen = new();
    private static ManualLogSource logger = null!;

    internal static void Initialize(ManualLogSource logSource) => logger = logSource;

    internal static void Ensure(StartScreen_UI2? screen, RectTransform? root)
    {
        if (screen == null || root == null) return;
        IntPtr pointer = screen.Pointer;
        if (pointer == IntPtr.Zero) return;

        try
        {
            if (LabelsByScreen.TryGetValue(pointer, out TextMeshProUGUI? existing) &&
                existing != null && existing.gameObject != null)
            {
                if (!string.Equals(existing.text, DisplayText, StringComparison.Ordinal)) existing.text = DisplayText;
                return;
            }

            TextMeshProUGUI? template = screen.aboutButton?.titleTextField?.textField ??
                                        screen.settingsButton?.titleTextField?.textField;
            if (template == null)
            {
                logger.LogWarning("Home screen initialized without a usable native font template; version label skipped.");
                return;
            }

            Il2CppReferenceArray<Il2CppSystem.Type> components = new(1);
            components[0] = Il2CppType.Of<RectTransform>();
            GameObject labelObject = new(ObjectName, components);
            labelObject.transform.SetParent(root, false);
            labelObject.AddComponent<CanvasRenderer>();
            TextMeshProUGUI field = labelObject.AddComponent<TextMeshProUGUI>();
            field.font = template.font;
            field.fontSharedMaterial = template.fontSharedMaterial;
            field.text = DisplayText;
            field.alignment = TextAlignmentOptions.BottomRight;
            field.fontSize = 18f;
            field.enableAutoSizing = false;
            field.enableWordWrapping = false;
            field.raycastTarget = false;
            Color color = template.color;
            color.a = 0.8f;
            field.color = color;

            RectTransform transform = field.rectTransform;
            transform.anchorMin = new Vector2(1f, 0f);
            transform.anchorMax = new Vector2(1f, 0f);
            transform.pivot = new Vector2(1f, 0f);
            transform.anchoredPosition = new Vector2(-24f, 20f);
            transform.sizeDelta = new Vector2(360f, 34f);

            LabelsByScreen[pointer] = field;
            logger.LogInfo($"Added {DisplayText} with an isolated bottom-right text component.");
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Could not add the optional home-screen version label: {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(StartScreen_UI2), nameof(StartScreen_UI2.Init))]
internal static class HomeVersionInitPatch
{
    [HarmonyPostfix]
    private static void AddVersion(StartScreen_UI2 __instance, RectTransform rectTransform) =>
        HomeVersionLabel.Ensure(__instance, rectTransform);
}
