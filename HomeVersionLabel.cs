using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace BetterBoPMod;

/// <summary>
/// Adds one non-interactive label only after the home screen has opened. This
/// deliberately avoids every StartScreen lifecycle hook that proved unsafe on
/// IL2CPP builds in earlier alphas.
/// </summary>
internal static class HomeVersionLabel
{
    internal const string DisplayText = "BBoP Alpha 0.6.12";
    private const string ObjectName = "BetterBoP.HomeVersion";
    private static ManualLogSource logger = null!;
    private static TextMeshProUGUI? label;
    private static bool warned;

    internal static void Initialize(ManualLogSource logSource) => logger = logSource;

    internal static void TryAdd(UIConstants.Screens openedScreen)
    {
        if (openedScreen != UIConstants.Screens.StartScreen) return;
        if (label != null && label.gameObject != null) return;

        try
        {
            StartScreen_UI2? screen = UIManager.Instance
                .GetScreen(openedScreen, true)?
                .TryCast<StartScreen_UI2>();
            TextField_UI2? template = screen?.aboutButton?.titleTextField ??
                                      screen?.settingsButton?.titleTextField;
            if (screen?.rectTransform == null || template?.gameObject == null) return;

            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, screen.rectTransform);
            clone.name = ObjectName;
            TextMeshProUGUI? field = clone.GetComponent<TextField_UI2>()?.textField ??
                                     clone.GetComponent<TextMeshProUGUI>();
            RectTransform? transform = clone.GetComponent<RectTransform>();
            if (field == null || transform == null)
            {
                UnityEngine.Object.Destroy(clone);
                return;
            }

            transform.anchorMin = new Vector2(1f, 0f);
            transform.anchorMax = new Vector2(1f, 0f);
            transform.pivot = new Vector2(1f, 0f);
            transform.anchoredPosition = new Vector2(-24f, 20f);
            transform.sizeDelta = new Vector2(320f, 32f);
            transform.SetAsLastSibling();

            field.text = DisplayText;
            field.alignment = TextAlignmentOptions.BottomRight;
            field.fontSize = 18f;
            field.enableAutoSizing = false;
            field.enableWordWrapping = false;
            field.raycastTarget = false;
            Color color = field.color;
            color.a = 0.78f;
            field.color = color;
            clone.SetActive(true);
            label = field;
            logger.LogInfo($"Displayed {DisplayText} in the home screen's bottom-right corner.");
        }
        catch (Exception exception)
        {
            if (warned) return;
            warned = true;
            logger.LogWarning($"Optional home version label was skipped safely: {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(UIEvents), nameof(UIEvents.ScreenOpen))]
internal static class HomeVersionScreenOpenPatch
{
    [HarmonyPostfix]
    private static void AddVersionAfterOpen(UIConstants.Screens screen) =>
        HomeVersionLabel.TryAdd(screen);
}

[HarmonyPatch(typeof(UIManager), nameof(UIManager.ShowScreen))]
internal static class HomeVersionShowScreenPatch
{
    [HarmonyPostfix]
    private static void AddVersionAfterShow(UIConstants.Screens screen) =>
        HomeVersionLabel.TryAdd(screen);
}
