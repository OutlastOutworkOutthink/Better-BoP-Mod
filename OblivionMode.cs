using BepInEx.Logging;
using HarmonyLib;
using I2.Loc;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PolytopiaBackendBase.Game;
using UnityEngine;

namespace BetterBoPMod;

/// <summary>
/// Adds Oblivion to Creative's Perfection / Domination / Infinity row. The
/// custom label is local UI state; actual games use Domination victory rules.
/// </summary>
internal static class OblivionMode
{
    internal const string Label = "Oblivion";
    internal const string Description =
        "Play with Domination rules while every bot unites against you.";
    internal const int ListId = 0x0B110;

    private const string SelectedKey = "betterbop.oblivion.v2.selected";
    private const string SeedPrefix = "betterbop.oblivion.v2.seed.";
    private static bool armedForNewGame;
    private static bool classicUnavailableLogged;
    private static bool ui2UnavailableLogged;

    internal static ManualLogSource Logger { get; set; } = null!;
    internal static bool Selected => PlayerPrefs.GetInt(SelectedKey, 0) == 1;

    internal static void SetSelected(bool selected)
    {
        PlayerPrefs.SetInt(SelectedKey, selected ? 1 : 0);
        PlayerPrefs.Save();
        if (selected) ConfigureDominationRules();
    }

    internal static void ConfigureDominationRules()
    {
        GameSettings? settings = GameManager.instance?.settings;
        if (settings == null) return;
        settings.BaseGameMode = GameMode.Custom;
        settings.RulesGameMode = GameMode.Domination;
    }

    internal static bool IsClassicIndex(UIHorizontalList list, int index)
    {
        if (list?.data == null || index < 0 || index >= list.data.Length) return false;
        if (string.Equals(list.data[index], Label, StringComparison.OrdinalIgnoreCase)) return true;
        return list.ids != null && index < list.ids.Length && list.ids[index] == ListId;
    }

    internal static bool IsUI2Index(UIHorizontalListData data, int index)
    {
        if (data == null || data.labels == null || index < 0) return false;
        if (index < data.labels.Count &&
            string.Equals(data.labels[index], Label, StringComparison.OrdinalIgnoreCase))
            return true;

        // SetShowGameModes is also patched as a last-resort view fallback. If
        // that fallback supplied a fourth visual item before the model could be
        // expanded, index == Count still means the Oblivion choice.
        return index == data.labels.Count && IsCreativeRuleData(data);
    }

    internal static bool IsRenderedUI2Index(GameSetupScreen_UI2 screen, int index)
    {
        if (screen?.view?.listGameMode?.data == null || index < 0) return false;
        Il2CppSystem.Collections.Generic.List<string> labels = screen.view.listGameMode.data;
        return index < labels.Count &&
               string.Equals(labels[index], Label, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsCreativeSetup()
    {
        GameSettings? settings = GameManager.instance?.settings;
        return settings != null && settings.BaseGameMode == GameMode.Custom;
    }

    internal static bool EnsureClassicRow(UIHorizontalList? list)
    {
        if (!IsCreativeSetup()) return false;

        try
        {
            if (list?.data == null || list.ids == null)
            {
                if (!classicUnavailableLogged)
                {
                    classicUnavailableLogged = true;
                    Logger.LogWarning(
                        "Creative setup's legacy game-mode list was unavailable " +
                        $"(list={list != null}, data={list?.data != null}, ids={list?.ids != null})."
                    );
                }
                return false;
            }

            for (int index = 0; index < list.data.Length; index++)
            {
                if (IsClassicIndex(list, index)) return true;
            }

            int oldLength = list.data.Length;
            Il2CppStringArray labels = new(oldLength + 1);
            Il2CppStructArray<int> ids = new(oldLength + 1);
            for (int index = 0; index < oldLength; index++)
            {
                string label = list.data[index];
                if (list.useDataAsLocalizationKeys)
                {
                    string? localized = LocalizationManager.GetTranslation(label);
                    if (!string.IsNullOrWhiteSpace(localized)) label = localized;
                }
                labels[index] = label;
                ids[index] = index < list.ids.Length ? list.ids[index] : index;
            }

            labels[oldLength] = Label;
            ids[oldLength] = ListId;
            int selectedIndex = Selected ? oldLength : list.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex > oldLength) selectedIndex = 0;
            list.SetData(labels, ids, selectedIndex, false);
            Logger.LogInfo(
                $"Added Oblivion directly to the legacy Creative game-mode list " +
                $"({oldLength} vanilla entries, {oldLength + 1} rendered entries)."
            );
            return true;
        }
        catch (Exception exception)
        {
            Logger.LogWarning($"Could not update the legacy Creative game-mode list: {exception}");
            return false;
        }
    }

    internal static bool EnsureRenderedUI2Row(
        GameSetupScreenView view,
        string? header = null,
        int? requestedSelection = null
    )
    {
        if (!IsCreativeSetup()) return false;

        try
        {
            UIHorizontalList_UI2? list = view?.listGameMode;
            if (list?.data == null)
            {
                if (!ui2UnavailableLogged)
                {
                    ui2UnavailableLogged = true;
                    Logger.LogWarning(
                        "Creative setup's UI2 game-mode list was unavailable " +
                        $"(view={view != null}, list={view?.listGameMode != null}, " +
                        $"data={view?.listGameMode?.data != null})."
                    );
                }
                return false;
            }

            Il2CppSystem.Collections.Generic.List<string> current = list.data;
            for (int index = 0; index < current.Count; index++)
            {
                if (string.Equals(current[index], Label, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (current.Count == 0)
            {
                Logger.LogWarning("Creative setup's live game-mode list contained no entries.");
                return false;
            }

            Il2CppSystem.Collections.Generic.List<string> expanded = new();
            foreach (string label in current) expanded.Add(label);
            expanded.Add(Label);

            string? renderedHeader = header;
            if (string.IsNullOrWhiteSpace(renderedHeader)) renderedHeader = list.header?.text;
            string safeHeader = string.IsNullOrWhiteSpace(renderedHeader)
                ? "Game Mode"
                : renderedHeader;

            int selected = Selected
                ? expanded.Count - 1
                : requestedSelection ?? list.SelectedIndex;
            if (selected < 0 || selected >= expanded.Count) selected = 0;

            list.SetData(safeHeader, expanded, selected);
            Logger.LogInfo(
                $"Added Oblivion directly to the live Creative game-mode list " +
                $"({current.Count} vanilla entries, {expanded.Count} rendered entries)."
            );
            return true;
        }
        catch (Exception exception)
        {
            Logger.LogWarning($"Could not update the live Creative game-mode list: {exception}");
            return false;
        }
    }

    internal static bool IsCreativeRuleData(UIHorizontalListData data)
    {
        if (data == null || data.labels == null || data.ids == null || data.labels.Count < 3)
            return false;

        bool perfection = false;
        bool domination = false;
        bool infinity = false;
        for (int index = 0; index < data.ids.Count; index++)
        {
            int id = data.ids[index];
            perfection |= id == (int)GameMode.Perfection;
            domination |= id == (int)GameMode.Domination;
            infinity |= id == (int)GameMode.Sandbox;
        }
        return perfection && domination && infinity;
    }

    internal static void ArmForNewGame()
    {
        armedForNewGame = Selected;
        if (!armedForNewGame) return;
        ConfigureDominationRules();
        Logger.LogInfo("Oblivion armed for the next single-player game.");
    }

    internal static void MarkNewGameReady()
    {
        if (!armedForNewGame) return;

        GameState state = GameManager.GameState;
        if (state == null)
        {
            Logger.LogWarning(
                "Oblivion is armed, but GameState is not ready yet; waiting for the next load boundary."
            );
            return;
        }

        PlayerPrefs.SetInt(SeedKey(state.Seed), 1);
        PlayerPrefs.Save();
        armedForNewGame = false;
        foreach (PlayerState player in state.PlayerStates) player.MarkOpinionsDirty();
        Logger.LogMessage($"Oblivion activated with Domination rules for seed {state.Seed}.");
    }

    internal static bool IsActive(GameState state)
    {
        return state != null && PlayerPrefs.GetInt(SeedKey(state.Seed), 0) == 1;
    }

    private static string SeedKey(int seed) => $"{SeedPrefix}{seed}";
}

[HarmonyPatch(typeof(GameSetupScreen), nameof(GameSetupScreen.CreateCustomGameModeList))]
internal static class OblivionCreativeModeListPatch
{
    [HarmonyPostfix]
    private static void AddOblivion(ref UIHorizontalList __result)
    {
        OblivionMode.EnsureClassicRow(__result);
    }
}

/// <summary>
/// Polytopia 122 can create the visible legacy row after UI2.OnShow. Recheck
/// the actual screen field at each late legacy lifecycle boundary.
/// </summary>
[HarmonyPatch]
internal static class OblivionClassicRenderedRowPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GameSetupScreen), nameof(GameSetupScreen.Show));
        yield return AccessTools.Method(
            typeof(GameSetupScreen),
            nameof(GameSetupScreen.OnScreenUpdated)
        );
        yield return AccessTools.Method(
            typeof(GameSetupScreen),
            nameof(GameSetupScreen.RefreshValuesFromSettings)
        );
    }

    [HarmonyPostfix]
    private static void AddToVisibleLegacyRow(GameSetupScreen __instance)
    {
        OblivionMode.EnsureClassicRow(__instance.gameModeList);
    }
}

[HarmonyPatch(typeof(GameSetupScreen), nameof(GameSetupScreen.OnCustomGameModeChanged))]
internal static class OblivionCreativeModeSelectionPatch
{
    [HarmonyPrefix]
    private static bool SelectOblivion(GameSetupScreen __instance, int index)
    {
        if (!OblivionMode.IsClassicIndex(__instance.gameModeList, index)) return true;
        OblivionMode.SetSelected(true);
        __instance.RefreshInfo();
        return false;
    }

    [HarmonyPostfix]
    private static void ClearWhenAnotherRuleIsSelected(GameSetupScreen __instance, int index)
    {
        if (!OblivionMode.IsClassicIndex(__instance.gameModeList, index))
            OblivionMode.SetSelected(false);
    }
}

[HarmonyPatch(typeof(GameSetupScreen), nameof(GameSetupScreen.RefreshInfo))]
internal static class OblivionCreativeModeDescriptionPatch
{
    [HarmonyPostfix]
    private static void ShowDescription(GameSetupScreen __instance)
    {
        if (!OblivionMode.Selected || __instance.gameModeInfoRow == null) return;
        OblivionMode.ConfigureDominationRules();
        __instance.gameModeInfoRow.Text = OblivionMode.Description;
    }
}

[HarmonyPatch(typeof(GameModeScreen), nameof(GameModeScreen.OnGameMode))]
internal static class OblivionMainModeResetPatch
{
    [HarmonyPrefix]
    private static void ClearOutsideCreative(GameMode gameMode)
    {
        if (gameMode != GameMode.Custom) OblivionMode.SetSelected(false);
    }
}

[HarmonyPatch]
internal static class OblivionMainModeResetUI2Patch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GameModeScreen_UI2), nameof(GameModeScreen_UI2.OnPerfection));
        yield return AccessTools.Method(typeof(GameModeScreen_UI2), nameof(GameModeScreen_UI2.OnDomination));
    }

    [HarmonyPrefix]
    private static void ClearOutsideCreative() => OblivionMode.SetSelected(false);
}

[HarmonyPatch(typeof(GameSetupScreen_UI2), nameof(GameSetupScreen_UI2.OnShow))]
internal static class OblivionCreativeModeListUI2Patch
{
    [HarmonyPostfix]
    private static void AddOblivion(GameSetupScreen_UI2 __instance)
    {
        if (!OblivionMode.IsCreativeSetup())
        {
            GameSettings? settings = GameManager.instance?.settings;
            OblivionMode.Logger.LogInfo(
                $"Game setup shown without Creative base mode " +
                $"(base={settings?.BaseGameMode}, rules={settings?.RulesGameMode}); " +
                "Oblivion was not added."
            );
            return;
        }

        try
        {
            UIHorizontalListData data = __instance.gameModeData;
            // UIHorizontalListData.HasData() dereferences partially initialized
            // IL2CPP fields during OnShow in Polytopia 122. Inspect only fields
            // that have completed initialization instead.
            if (data != null && OblivionMode.IsCreativeRuleData(data))
            {
                bool alreadyPresent = false;
                for (int index = 0; index < data.labels.Count; index++)
                {
                    if (!OblivionMode.IsUI2Index(data, index)) continue;
                    alreadyPresent = true;
                    break;
                }

                if (!alreadyPresent)
                {
                    data.AddItem(OblivionMode.Label, OblivionMode.ListId);
                    __instance.gameModeData = data;
                    int selectedIndex = OblivionMode.Selected
                        ? data.labels.Count - 1
                        : data.IndexFromId(data.selectedObject);
                    __instance.view.SetShowGameModes(data.header, data.GetLabels(), selectedIndex);
                    OblivionMode.Logger.LogInfo(
                        "Added Oblivion to the complete UI2 Creative game-mode model."
                    );
                }
            }

            OblivionMode.EnsureRenderedUI2Row(__instance.view);
            if (OblivionMode.Selected)
            {
                OblivionMode.ConfigureDominationRules();
                __instance.view.SetShowGameModeDescriptionText(OblivionMode.Description);
            }
        }
        catch (Exception exception)
        {
            // A UI enhancement must never abort the game's setup screen. The
            // view-boundary patch below remains available as the safe fallback.
            OblivionMode.Logger.LogWarning(
                $"Controller game-mode insertion was unavailable; using view fallback: {exception}"
            );
            OblivionMode.EnsureRenderedUI2Row(__instance.view);
        }
    }
}

/// <summary>
/// The current PC layout renders the exact row through GameSetupScreenView.
/// This fallback patches that final boundary, so future changes in the setup
/// controller cannot silently hide Oblivion again.
/// </summary>
[HarmonyPatch(typeof(GameSetupScreenView), nameof(GameSetupScreenView.SetShowGameModes))]
internal static class OblivionGameModeViewFallbackPatch
{
    [HarmonyPostfix]
    private static void AddOblivionToRenderedRow(
        GameSetupScreenView __instance,
        string header,
        int selected
    )
    {
        OblivionMode.EnsureRenderedUI2Row(__instance, header, selected);
    }
}

/// <summary>
/// OnShow can run before UI2 creates its controls. These layout hooks are the
/// late boundary at which the view's live game-mode list is finally available.
/// </summary>
[HarmonyPatch(typeof(GameSetupScreen_UI2), nameof(GameSetupScreen_UI2.RunLayout))]
internal static class OblivionLateUI2LayoutPatch
{
    [HarmonyPostfix]
    private static void AddAfterControllerLayout(GameSetupScreen_UI2 __instance)
    {
        if (__instance.view?.listGameMode?.data != null)
            OblivionMode.EnsureRenderedUI2Row(__instance.view);
    }
}

[HarmonyPatch(typeof(GameSetupScreenView), nameof(GameSetupScreenView.RunLayout))]
internal static class OblivionLateViewLayoutPatch
{
    [HarmonyPostfix]
    private static void AddAfterViewLayout(GameSetupScreenView __instance)
    {
        if (__instance.listGameMode?.data != null)
            OblivionMode.EnsureRenderedUI2Row(__instance);
    }
}

[HarmonyPatch(typeof(GameSetupScreen_UI2), nameof(GameSetupScreen_UI2.OnGameModeChanged))]
internal static class OblivionCreativeModeSelectionUI2Patch
{
    [HarmonyPrefix]
    private static bool SelectOblivion(GameSetupScreen_UI2 __instance, int index)
    {
        bool isOblivion = OblivionMode.IsUI2Index(__instance.gameModeData, index) ||
                           OblivionMode.IsRenderedUI2Index(__instance, index);
        if (!isOblivion) return true;
        OblivionMode.SetSelected(true);
        __instance.view.SetShowGameModeDescriptionText(OblivionMode.Description);
        return false;
    }

    [HarmonyPostfix]
    private static void ClearWhenAnotherRuleIsSelected(GameSetupScreen_UI2 __instance, int index)
    {
        bool isOblivion = OblivionMode.IsUI2Index(__instance.gameModeData, index) ||
                           OblivionMode.IsRenderedUI2Index(__instance, index);
        if (!isOblivion)
            OblivionMode.SetSelected(false);
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.CreateSinglePlayerGame))]
internal static class OblivionNewGameArmPatch
{
    [HarmonyPrefix]
    private static void ArmSelectedMode() => OblivionMode.ArmForNewGame();
}

/// <summary>
/// Both setup implementations can bypass GameManager.CreateSinglePlayerGame's
/// public wrapper. Arm at their concrete start callbacks as well.
/// </summary>
[HarmonyPatch]
internal static class OblivionSetupStartArmPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(
            typeof(GameSetupScreen),
            nameof(GameSetupScreen.OnStartGameClicked)
        );
        yield return AccessTools.Method(
            typeof(GameSetupScreen),
            nameof(GameSetupScreen.OnStartGameClickedAndFaded)
        );
        yield return AccessTools.Method(
            typeof(GameSetupScreen_UI2),
            nameof(GameSetupScreen_UI2.OnContinueClicked_StartSingleplayerGame)
        );
    }

    [HarmonyPrefix]
    private static void ArmFromSetupScreen() => OblivionMode.ArmForNewGame();
}

[HarmonyPatch]
internal static class OblivionNewGameReadyPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GameManager), nameof(GameManager.OnGameReady));
        yield return AccessTools.Method(typeof(GameManager), nameof(GameManager.OnLevelLoaded));
    }

    [HarmonyPostfix]
    private static void PersistOblivionGame() => OblivionMode.MarkNewGameReady();
}
