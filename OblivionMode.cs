using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PolytopiaBackendBase.Game;
using UnityEngine;

namespace BetterBoPMod;

/// <summary>
/// Oblivion runs on Polytopia's Creative/Sandbox rules, adding only Better
/// BoP's opinion behavior. The chosen state is recorded against the generated
/// seed so local saves resume in the same mode without changing map settings.
/// </summary>
internal static class OblivionMode
{
    internal const string Label = "Oblivion";
    internal const string Description = "All bot tribes are united. They strongly support every other bot and regard you as the enemy.";
    private const string SelectedKey = "bbp.oblivion.selected";
    private const string SeedPrefix = "bbp.oblivion.game.";
    private static bool armedForNewGame;

    internal static bool Selected => PlayerPrefs.GetInt(SelectedKey, 0) == 1;

    internal static void SetSelected(bool selected)
    {
        PlayerPrefs.SetInt(SelectedKey, selected ? 1 : 0);
        PlayerPrefs.Save();
    }

    internal static void ArmForNewGame() => armedForNewGame = Selected;

    internal static void MarkNewGameReady()
    {
        if (!armedForNewGame) return;
        armedForNewGame = false;
        GameState state = GameManager.GameState;
        if (state == null) return;
        PlayerPrefs.SetInt(SeedKey(state.Seed), 1);
        PlayerPrefs.Save();
        foreach (PlayerState player in state.PlayerStates) player.MarkOpinionsDirty();
        BetterBoPRules.Logger.LogInfo($"Activated Oblivion for Creative game seed {state.Seed}.");
    }

    internal static bool IsActive(GameState state)
    {
        return state != null && PlayerPrefs.GetInt(SeedKey(state.Seed), 0) == 1;
    }

    private static string SeedKey(int seed) => $"{SeedPrefix}{seed}";

    internal static bool IsOblivionIndex(UIHorizontalList list, int index)
    {
        return list != null && list.data != null && index >= 0 && index < list.data.Length &&
            string.Equals(list.data[index], Label, StringComparison.OrdinalIgnoreCase);
    }
}

[HarmonyPatch(typeof(GameSetupScreen), nameof(GameSetupScreen.CreateCustomGameModeList))]
internal static class OblivionCreativeModeListPatch
{
    [HarmonyPostfix]
    private static void AddOblivionToCreative(ref UIHorizontalList __result)
    {
        if (__result == null || __result.data == null || __result.ids == null) return;
        for (int index = 0; index < __result.data.Length; index++)
        {
            if (string.Equals(__result.data[index], OblivionMode.Label, StringComparison.OrdinalIgnoreCase)) return;
        }

        int oldLength = __result.data.Length;
        Il2CppStringArray labels = new(oldLength + 1);
        Il2CppStructArray<int> ids = new(oldLength + 1);
        for (int index = 0; index < oldLength; index++)
        {
            labels[index] = __result.data[index];
            ids[index] = __result.ids[index];
        }
        labels[oldLength] = OblivionMode.Label;
        // Run the standard Creative/Sandbox victory rules. Oblivion changes AI
        // opinions only, leaving every map and setup option untouched.
        ids[oldLength] = (int)GameMode.Sandbox;
        int selected = OblivionMode.Selected ? oldLength : __result.SelectedIndex;
        __result.SetData(labels, ids, selected, __result.useDataAsLocalizationKeys);
    }
}

[HarmonyPatch(typeof(GameSetupScreen), nameof(GameSetupScreen.OnCustomGameModeChanged))]
internal static class OblivionCreativeModeSelectionPatch
{
    [HarmonyPrefix]
    private static bool SelectOblivionWithoutUsingVanillaIndex(GameSetupScreen __instance, int index)
    {
        if (!OblivionMode.IsOblivionIndex(__instance.gameModeList, index)) return true;
        OblivionMode.SetSelected(true);
        GameManager.instance.settings.RulesGameMode = GameMode.Sandbox;
        __instance.RefreshInfo();
        return false;
    }

    [HarmonyPostfix]
    private static void RememberOblivionSelection(GameSetupScreen __instance, int index)
    {
        if (OblivionMode.IsOblivionIndex(__instance.gameModeList, index)) return;
        OblivionMode.SetSelected(false);
    }
}

[HarmonyPatch(typeof(GameSetupScreen), nameof(GameSetupScreen.RefreshInfo))]
internal static class OblivionCreativeModeDescriptionPatch
{
    [HarmonyPostfix]
    private static void ShowOblivionDescription(GameSetupScreen __instance)
    {
        if (!OblivionMode.Selected || __instance.gameModeInfoRow == null) return;
        __instance.gameModeInfoRow.Text = OblivionMode.Description;
    }
}

[HarmonyPatch(typeof(GameModeScreen), nameof(GameModeScreen.OnGameMode))]
internal static class OblivionMainModeResetPatch
{
    [HarmonyPrefix]
    private static void ClearOblivionOutsideCreative(GameMode gameMode)
    {
        if (gameMode != GameMode.Custom) OblivionMode.SetSelected(false);
    }
}

// UI2 is used on newer layouts. It receives the same fourth Creative choice
// while the classic screen above remains compatible with the current build.
[HarmonyPatch(typeof(GameSetupScreen_UI2), nameof(GameSetupScreen_UI2.OnShow))]
internal static class OblivionCreativeModeListUI2Patch
{
    [HarmonyPostfix]
    private static void AddOblivionToCreative(GameSetupScreen_UI2 __instance)
    {
        if (GameManager.instance.settings.BaseGameMode != GameMode.Custom) return;
        UIHorizontalListData data = __instance.gameModeData;
        if (data == null || !data.HasData()) return;
        for (int index = 0; index < data.labels.Count; index++)
        {
            if (string.Equals(data.labels[index], OblivionMode.Label, StringComparison.OrdinalIgnoreCase)) return;
        }

        data.AddItem(OblivionMode.Label, (int)GameMode.Sandbox);
        __instance.gameModeData = data;
        int selected = OblivionMode.Selected ? data.labels.Count - 1 : data.IndexFromId(data.selectedObject);
        __instance.view.SetShowGameModes(data.header, data.GetLabels(), selected);
        if (OblivionMode.Selected) __instance.view.SetShowGameModeDescriptionText(OblivionMode.Description);
    }
}

[HarmonyPatch(typeof(GameSetupScreen_UI2), nameof(GameSetupScreen_UI2.OnGameModeChanged))]
internal static class OblivionCreativeModeSelectionUI2Patch
{
    [HarmonyPrefix]
    private static bool SelectOblivionWithoutUsingVanillaIndex(GameSetupScreen_UI2 __instance, int index)
    {
        UIHorizontalListData data = __instance.gameModeData;
        bool selected = data != null && index >= 0 && index < data.labels.Count &&
            string.Equals(data.labels[index], OblivionMode.Label, StringComparison.OrdinalIgnoreCase);
        if (!selected) return true;
        OblivionMode.SetSelected(true);
        GameManager.instance.settings.RulesGameMode = GameMode.Sandbox;
        __instance.view.SetShowGameModeDescriptionText(OblivionMode.Description);
        return false;
    }

    [HarmonyPostfix]
    private static void RememberOblivionSelection(GameSetupScreen_UI2 __instance, int index)
    {
        UIHorizontalListData data = __instance.gameModeData;
        bool selected = data != null && index >= 0 && index < data.labels.Count &&
            string.Equals(data.labels[index], OblivionMode.Label, StringComparison.OrdinalIgnoreCase);
        if (!selected) OblivionMode.SetSelected(false);
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.CreateSinglePlayerGame))]
internal static class OblivionNewGameArmPatch
{
    [HarmonyPrefix]
    private static void ArmSelectedMode()
    {
        OblivionMode.ArmForNewGame();
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.OnGameReady))]
internal static class OblivionNewGameReadyPatch
{
    [HarmonyPostfix]
    private static void PersistOblivionGame()
    {
        OblivionMode.MarkNewGameReady();
    }
}
