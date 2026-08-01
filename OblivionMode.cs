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
        return data != null && index >= 0 && index < data.labels.Count &&
               string.Equals(data.labels[index], Label, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ArmForNewGame()
    {
        armedForNewGame = Selected;
        if (armedForNewGame) ConfigureDominationRules();
    }

    internal static void MarkNewGameReady()
    {
        if (!armedForNewGame) return;
        armedForNewGame = false;

        GameState state = GameManager.GameState;
        if (state == null) return;

        PlayerPrefs.SetInt(SeedKey(state.Seed), 1);
        PlayerPrefs.Save();
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
        if (__result?.data == null || __result.ids == null) return;
        for (int index = 0; index < __result.data.Length; index++)
        {
            if (OblivionMode.IsClassicIndex(__result, index)) return;
        }

        int oldLength = __result.data.Length;
        Il2CppStringArray labels = new(oldLength + 1);
        Il2CppStructArray<int> ids = new(oldLength + 1);
        for (int index = 0; index < oldLength; index++)
        {
            string label = __result.data[index];
            if (__result.useDataAsLocalizationKeys)
            {
                string localized = LocalizationManager.GetTranslation(label);
                if (!string.IsNullOrWhiteSpace(localized)) label = localized;
            }
            labels[index] = label;
            ids[index] = __result.ids[index];
        }

        labels[oldLength] = OblivionMode.Label;
        ids[oldLength] = OblivionMode.ListId;
        int selectedIndex = OblivionMode.Selected ? oldLength : __result.SelectedIndex;
        // The vanilla entries were localized above. Keeping this false lets the
        // custom English label render directly instead of being treated as a
        // missing I2 localization key.
        __result.SetData(labels, ids, selectedIndex, false);
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
        if (GameManager.instance?.settings?.BaseGameMode != GameMode.Custom) return;
        UIHorizontalListData data = __instance.gameModeData;
        if (data == null || !data.HasData()) return;

        for (int index = 0; index < data.labels.Count; index++)
        {
            if (OblivionMode.IsUI2Index(data, index)) return;
        }

        data.AddItem(OblivionMode.Label, OblivionMode.ListId);
        __instance.gameModeData = data;
        int selectedIndex = OblivionMode.Selected
            ? data.labels.Count - 1
            : data.IndexFromId(data.selectedObject);
        __instance.view.SetShowGameModes(data.header, data.GetLabels(), selectedIndex);
        if (!OblivionMode.Selected) return;

        OblivionMode.ConfigureDominationRules();
        __instance.view.SetShowGameModeDescriptionText(OblivionMode.Description);
    }
}

[HarmonyPatch(typeof(GameSetupScreen_UI2), nameof(GameSetupScreen_UI2.OnGameModeChanged))]
internal static class OblivionCreativeModeSelectionUI2Patch
{
    [HarmonyPrefix]
    private static bool SelectOblivion(GameSetupScreen_UI2 __instance, int index)
    {
        if (!OblivionMode.IsUI2Index(__instance.gameModeData, index)) return true;
        OblivionMode.SetSelected(true);
        __instance.view.SetShowGameModeDescriptionText(OblivionMode.Description);
        return false;
    }

    [HarmonyPostfix]
    private static void ClearWhenAnotherRuleIsSelected(GameSetupScreen_UI2 __instance, int index)
    {
        if (!OblivionMode.IsUI2Index(__instance.gameModeData, index))
            OblivionMode.SetSelected(false);
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.CreateSinglePlayerGame))]
internal static class OblivionNewGameArmPatch
{
    [HarmonyPrefix]
    private static void ArmSelectedMode() => OblivionMode.ArmForNewGame();
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.OnGameReady))]
internal static class OblivionNewGameReadyPatch
{
    [HarmonyPostfix]
    private static void PersistOblivionGame() => OblivionMode.MarkNewGameReady();
}
