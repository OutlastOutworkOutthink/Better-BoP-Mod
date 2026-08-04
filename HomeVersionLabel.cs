using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace BetterBoPMod;

/// <summary>
/// Adds the mod version to Polytopia's home-screen version area. The native
/// version text is extended when present; a lightweight cloned text field is
/// used only as a fallback for prefab revisions without that field.
/// </summary>
internal static class HomeVersionLabel
{
    internal const string DisplayText = "BBoP Alpha 0.5.25";
    private const string ObjectName = "BetterBoP.HomeVersion";
    private static readonly Dictionary<IntPtr, LabelBinding> LabelsByScreen = new();
    private static readonly HashSet<IntPtr> ScreensBeingBound = new();
    private static ManualLogSource logger = null!;

    internal static void Initialize(ManualLogSource logSource) => logger = logSource;

    internal static void Ensure(StartScreen_UI2? screen)
    {
        if (screen?.rectTransform == null) return;
        IntPtr pointer = screen.Pointer;
        if (pointer == IntPtr.Zero || !ScreensBeingBound.Add(pointer)) return;
        try
        {
            if (LabelsByScreen.TryGetValue(pointer, out LabelBinding? existing) && existing.IsAlive)
            {
                existing.Refresh();
                return;
            }

            TextMeshProUGUI? nativeVersion = FindNativeVersionText(screen.rectTransform);
            LabelBinding binding = nativeVersion != null
                ? LabelBinding.ForNative(nativeVersion)
                : CreateFallback(screen);
            if (!binding.IsAlive) return;
            LabelsByScreen[pointer] = binding;
            binding.Refresh();
            logger.LogInfo(nativeVersion != null
                ? $"Added {DisplayText} to the native home-screen version text."
                : $"Added {DisplayText} in the home screen's bottom-right corner.");
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Could not add the home-screen mod version yet: {exception.Message}");
        }
        finally
        {
            ScreensBeingBound.Remove(pointer);
        }
    }

    private static TextMeshProUGUI? FindNativeVersionText(RectTransform root)
    {
        foreach (TextMeshProUGUI field in root.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (field == null || field.gameObject == null || field.gameObject.name == ObjectName) continue;
            string name = field.gameObject.name ?? string.Empty;
            string text = field.text ?? string.Empty;
            if (name.Contains("version", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(Application.version) && text.Contains(Application.version, StringComparison.Ordinal)))
                return field;
        }
        return null;
    }

    private static LabelBinding CreateFallback(StartScreen_UI2 screen)
    {
        TextField_UI2? template = screen.aboutButton?.titleTextField ?? screen.settingsButton?.titleTextField;
        if (template == null || template.gameObject == null) return LabelBinding.Empty;

        GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, screen.rectTransform);
        clone.name = ObjectName;
        TextField_UI2? wrapper = clone.GetComponent<TextField_UI2>();
        TextMeshProUGUI? field = wrapper?.textField ?? clone.GetComponent<TextMeshProUGUI>();
        if (field == null) return LabelBinding.Empty;

        RectTransform transform = field.rectTransform;
        transform.anchorMin = new Vector2(1f, 0f);
        transform.anchorMax = new Vector2(1f, 0f);
        transform.pivot = new Vector2(1f, 0f);
        transform.anchoredPosition = new Vector2(-24f, 20f);
        transform.sizeDelta = new Vector2(360f, 34f);
        field.alignment = TextAlignmentOptions.BottomRight;
        field.fontSize = 18f;
        field.enableAutoSizing = false;
        field.enableWordWrapping = false;
        field.raycastTarget = false;
        Color color = field.color;
        color.a = 0.8f;
        field.color = color;
        clone.SetActive(true);
        return LabelBinding.ForFallback(field);
    }

    private sealed class LabelBinding
    {
        internal static readonly LabelBinding Empty = new(null, false, string.Empty);
        private readonly TextMeshProUGUI? field;
        private readonly bool native;
        private string nativeText;

        private LabelBinding(TextMeshProUGUI? field, bool native, string nativeText)
        {
            this.field = field;
            this.native = native;
            this.nativeText = nativeText;
        }

        internal static LabelBinding ForNative(TextMeshProUGUI field) => new(field, true, Clean(field.text));
        internal static LabelBinding ForFallback(TextMeshProUGUI field) => new(field, false, string.Empty);
        internal bool IsAlive => field != null && field.gameObject != null;

        internal void Refresh()
        {
            if (!IsAlive) return;
            if (!native)
            {
                if (!string.Equals(field!.text, DisplayText, StringComparison.Ordinal)) field.text = DisplayText;
                if (!field.gameObject.activeSelf) field.gameObject.SetActive(true);
                return;
            }

            string current = Clean(field!.text);
            if (!string.IsNullOrWhiteSpace(current)) nativeText = current;
            string desired = string.IsNullOrWhiteSpace(nativeText) ? DisplayText : $"{nativeText}\n{DisplayText}";
            if (!string.Equals(field.text, desired, StringComparison.Ordinal)) field.text = desired;
        }

        private static string Clean(string? value)
        {
            string text = value ?? string.Empty;
            int marker = text.IndexOf(DisplayText, StringComparison.Ordinal);
            return marker < 0 ? text.TrimEnd() : text[..marker].TrimEnd('\r', '\n', ' ');
        }
    }
}

[HarmonyPatch(typeof(StartScreen_UI2), "OnShowAfterLayout")]
internal static class HomeVersionAfterLayoutPatch
{
    [HarmonyPostfix]
    private static void AddVersion(StartScreen_UI2 __instance) => HomeVersionLabel.Ensure(__instance);
}
