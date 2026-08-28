using BepInEx.Logging;
using HarmonyLib;
using I2.Loc;
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
    internal const string DisplayText = "BBoP Alpha 0.6.14";
    private const string ObjectName = "BetterBoP.HomeVersion";
    private const int RetryDelayFrames = 4;
    private const int MaxAttempts = 240;
    private static ManualLogSource logger = null!;
    private static TextMeshProUGUI? label;
    private static StartScreen_UI2? candidate;
    private static int delayFrames;
    private static int attemptsRemaining;
    private static bool pending;
    private static bool displayed;
    private static bool warned;

    internal static void Initialize(ManualLogSource logSource)
    {
        logger = logSource;
        Schedule();
    }

    internal static void Schedule(
        UIConstants.Screens screen = UIConstants.Screens.StartScreen,
        StartScreen_UI2? liveScreen = null
    )
    {
        if (screen != UIConstants.Screens.StartScreen)
        {
            if (label != null && label.gameObject != null)
                label.gameObject.SetActive(false);
            return;
        }
        candidate = liveScreen;
        delayFrames = RetryDelayFrames;
        attemptsRemaining = MaxAttempts;
        pending = true;
        warned = false;
    }

    /// <summary>
    /// Runs only while the Start screen label is pending, then disables itself.
    /// The existing Integrated main-thread pump calls this, so no extra Unity
    /// Update patch or permanent polling loop is introduced.
    /// </summary>
    internal static void Tick()
    {
        if (!pending || !UIManager.Exists ||
            UIManager.Instance.CurrentScreen != UIConstants.Screens.StartScreen)
            return;
        if (delayFrames-- > 0) return;

        try
        {
            StartScreen_UI2? screen = candidate;
            IScreen? current = UIManager.Instance.GetCurrentScreen();
            StartScreen_UI2? currentStart = current?.TryCast<StartScreen_UI2>();
            if (currentStart != null) screen = currentStart;

            if (TryAdd(screen))
            {
                pending = false;
                candidate = screen;
                if (!displayed)
                {
                    displayed = true;
                    logger.LogInfo($"Displayed {DisplayText} in the home screen's bottom-right corner.");
                }
                return;
            }

            delayFrames = RetryDelayFrames;
            if (--attemptsRemaining > 0) return;
            pending = false;
            WarnOnce("the live Start screen never exposed a usable text template");
        }
        catch (Exception exception)
        {
            delayFrames = RetryDelayFrames;
            if (--attemptsRemaining > 0) return;
            pending = false;
            WarnOnce(exception.Message);
        }
    }

    private static bool TryAdd(StartScreen_UI2? screen)
    {
        if (screen?.rectTransform == null) return false;
        if (label != null && label.gameObject != null && candidate == screen)
        {
            label.text = DisplayText;
            label.gameObject.SetActive(true);
            return true;
        }

        label = null;
        candidate = screen;
        TextField_UI2? template = screen.aboutButton?.titleTextField ??
                                  screen.settingsButton?.titleTextField;
        if (template?.gameObject == null) return false;

        RectTransform parent = screen.rectTransform.GetComponentInParent<Canvas>()?
            .rootCanvas?.GetComponent<RectTransform>() ?? screen.rectTransform;
        Transform? existing = parent.Find(ObjectName);
        GameObject clone = existing?.gameObject ??
            UnityEngine.Object.Instantiate(template.gameObject, parent);
        clone.name = ObjectName;
        TMPLocalizer? localizer = clone.GetComponent<TMPLocalizer>();
        if (localizer != null) localizer.enabled = false;

        TextField_UI2? textField = clone.GetComponent<TextField_UI2>();
        TextMeshProUGUI? field = textField?.textField ?? clone.GetComponent<TextMeshProUGUI>();
        RectTransform? transform = clone.GetComponent<RectTransform>();
        if (field == null || transform == null)
        {
            if (existing == null) UnityEngine.Object.Destroy(clone);
            return false;
        }

        textField?.SetText(DisplayText);
        textField?.UpdateSize();
        transform.anchorMin = new Vector2(1f, 0f);
        transform.anchorMax = new Vector2(1f, 0f);
        transform.pivot = new Vector2(1f, 0f);
        transform.anchoredPosition = new Vector2(-24f, 20f);
        transform.sizeDelta = new Vector2(320f, 32f);
        transform.localScale = Vector3.one;
        transform.SetAsLastSibling();

        field.text = DisplayText;
        field.alignment = TextAlignmentOptions.BottomRight;
        field.fontSize = 18f;
        field.enableAutoSizing = false;
        field.enableWordWrapping = false;
        field.raycastTarget = false;
        field.enabled = true;
        Color color = field.color;
        color.a = 0.78f;
        field.color = color;
        clone.SetActive(true);
        label = field;
        return label.gameObject.activeInHierarchy;
    }

    private static void WarnOnce(string reason)
    {
        if (warned) return;
        warned = true;
        logger.LogWarning($"Optional home version label was skipped safely: {reason}.");
    }
}

[HarmonyPatch(typeof(UIEvents), nameof(UIEvents.ScreenOpen))]
internal static class HomeVersionScreenOpenPatch
{
    [HarmonyPostfix]
    private static void AddVersionAfterOpen(UIConstants.Screens __0) =>
        HomeVersionLabel.Schedule(__0);
}

[HarmonyPatch(typeof(UIEvents), nameof(UIEvents.LoadingScreenHidden))]
internal static class HomeVersionLoadingCompletePatch
{
    [HarmonyPostfix]
    private static void AddVersionAfterLoading() => HomeVersionLabel.Schedule();
}

[HarmonyPatch(typeof(UIManager), nameof(UIManager.ShowScreen),
    new[] { typeof(UIConstants.Screens), typeof(bool), typeof(UIDeepLinkData) })]
internal static class HomeVersionShowScreenPatch
{
    [HarmonyPostfix]
    private static void AddVersionAfterShow(UIConstants.Screens __0, IScreen __result) =>
        HomeVersionLabel.Schedule(__0, __result?.TryCast<StartScreen_UI2>());
}
