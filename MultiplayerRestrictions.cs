using HarmonyLib;
using UnityEngine;

namespace BetterBoPMod;

internal static class MultiplayerRestrictions
{
    internal static void Explain()
    {
        BasicPopup popup = PopupManager.GetBasicPopup()
            .SetHeader("Bot-hosted multiplayer only")
            .SetDescription("Better BoP multiplayer games are currently created through Discord. Use an Integrated game opened by the bot; direct friend invites and random matching are temporarily unavailable.");
        popup.SetMainButton("Okay", Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => { }));
        popup.Show();
    }
}

[HarmonyPatch(typeof(LobbyPopup), nameof(LobbyPopup.RefreshPopup))]
internal static class HideLobbyInvitePatch
{
    [HarmonyPostfix]
    private static void HideAddPlayer(LobbyPopup __instance)
    {
        if (__instance.addPlayerButton != null) __instance.addPlayerButton.gameObject.SetActive(false);
    }
}

[HarmonyPatch]
internal static class BlockLobbyInvitePatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(LobbyPopup), nameof(LobbyPopup.OnShowInvitePlayer));
        yield return AccessTools.Method(typeof(LobbyPopup), nameof(LobbyPopup.OnInviteFriend));
    }

    [HarmonyPrefix]
    private static bool BlockInvite()
    {
        MultiplayerRestrictions.Explain();
        return false;
    }
}

[HarmonyPatch(typeof(MultiplayerSelectionScreen), nameof(MultiplayerSelectionScreen.NewGameButton_OnClicked))]
internal static class BlockManualMultiplayerGamePatch
{
    [HarmonyPrefix]
    private static bool BlockNewGame()
    {
        MultiplayerRestrictions.Explain();
        return false;
    }
}

[HarmonyPatch]
internal static class BlockRandomMatchPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MultiplayerSelectionScreen), nameof(MultiplayerSelectionScreen.OnStartMatchMakingGameRequested));
        yield return AccessTools.Method(typeof(MultiplayerScreen), nameof(MultiplayerScreen.OnNewMatchmakingGame));
    }

    [HarmonyPrefix]
    private static bool BlockRandomMatch()
    {
        MultiplayerRestrictions.Explain();
        return false;
    }
}

[HarmonyPatch(
    typeof(MultiplayerScreen),
    nameof(MultiplayerScreen.AddButtonRow),
    new[] { typeof(string), typeof(UIButtonBase.ButtonAction) }
)]
internal static class RedRandomMatchButtonPatch
{
    [HarmonyPostfix]
    private static void MakeRandomMatchRed(string buttonText, ButtonRow __result)
    {
        if (__result?.buttonComp == null) return;
        string normalized = buttonText?.ToLowerInvariant() ?? string.Empty;
        if (!normalized.Contains("random") && !normalized.Contains("match")) return;

        UIButtonBase.ColorStates colors = __result.buttonComp.BgColorStates;
        colors.defaultColor = new Color(0.72f, 0.08f, 0.08f, 1f);
        colors.hoverColor = new Color(0.92f, 0.12f, 0.12f, 1f);
        colors.highlightedColor = new Color(0.92f, 0.12f, 0.12f, 1f);
        colors.highlightedHoverColor = new Color(1f, 0.2f, 0.2f, 1f);
        colors.disabledColor = new Color(0.32f, 0.04f, 0.04f, 1f);
        __result.buttonComp.UpdateColors();
    }
}
