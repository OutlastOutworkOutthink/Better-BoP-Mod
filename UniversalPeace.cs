using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Polytopia.Data;
using System.Reflection;
using UnityEngine;

namespace BetterBoPMod;

/// <summary>
/// Makes peace treaties a universal base ability without unlocking Strategy or
/// changing any other Strategy reward. Safe to apply repeatedly because game
/// data is reparsed when Polytopia opens different local/online states.
/// </summary>
internal static class UniversalPeaceRules
{
    internal static ManualLogSource Logger { get; set; } = null!;

    internal static void Apply(GameLogicData data)
    {
        if (data == null || data.AllTechData == null) return;

        TechData basic = data.GetTechData(TechData.Type.Basic);
        foreach (var entry in data.AllTechData)
        {
            RemovePeaceUnlock(entry.Value);
        }

        if (basic?.abilityUnlocks != null &&
            !basic.abilityUnlocks.Contains(PlayerAbility.Type.PeaceTreaty))
        {
            basic.abilityUnlocks.Add(PlayerAbility.Type.PeaceTreaty);
        }
    }

    internal static void RemovePeaceUnlock(TechData? tech)
    {
        if (tech?.abilityUnlocks == null) return;
        while (tech.abilityUnlocks.Contains(PlayerAbility.Type.PeaceTreaty))
        {
            tech.abilityUnlocks.Remove(PlayerAbility.Type.PeaceTreaty);
        }
    }

    internal static void PrepareVisibleTech(TechData? tech)
    {
        if (tech == null || tech.type == TechData.Type.Basic) return;
        RemovePeaceUnlock(tech);
    }

    internal static bool IsPeaceCommand(CommandBase? command)
    {
        if (command == null) return false;

        // Prefer the command enum because it remains reliable even when an
        // IL2CPP wrapper arrives as its CommandBase view rather than as the
        // generated PeaceTreatyCommand managed subtype.
        return command.GetCommandType() == CommandType.PeaceTreaty;
    }
}

/// <summary>
/// Owns only the legacy tribe-info Peace Treaty buttons. Polytopia wires both
/// an enabled and a disabled delegate into these controls; changing their
/// colour flags leaves the stale Strategy delegate alive. Registered buttons
/// are intercepted before either native delegate can run and routed through
/// the one correct diplomacy action.
/// </summary>
internal static class UniversalPeaceButtonRegistry
{
    private static readonly Dictionary<IntPtr, Action> Actions = new();
    private static IntPtr lastExecutedButton;
    private static int lastExecutedFrame = -1;

    internal static void Clear()
    {
        Actions.Clear();
        lastExecutedButton = IntPtr.Zero;
        lastExecutedFrame = -1;
    }

    internal static void Register(UIRoundButton button, Action action)
    {
        Actions[button.Pointer] = action;
    }

    internal static bool TryExecute(UIButtonBase button)
    {
        IntPtr buttonPointer = button.Pointer;
        if (!Actions.TryGetValue(buttonPointer, out Action? action))
        {
            return false;
        }

        // Pointer, Unity Button, and controller signal routes can converge in
        // one frame. Consume every route but execute the treaty command once.
        if (lastExecutedButton == buttonPointer &&
            lastExecutedFrame == Time.frameCount)
        {
            return true;
        }

        lastExecutedButton = buttonPointer;
        lastExecutedFrame = Time.frameCount;
        action();
        return true;
    }
}

[HarmonyPatch(typeof(GameLogicData), nameof(GameLogicData.ParseGameLogicObjects))]
internal static class UniversalPeaceParsedRulesPatch
{
    [HarmonyPostfix]
    private static void ApplyUniversalPeace(GameLogicData __instance)
    {
        try
        {
            UniversalPeaceRules.Apply(__instance);
            UniversalPeaceRules.Logger.LogInfo(
                "Moved Peace Treaty from Strategy to the universally available Basic ability set."
            );
        }
        catch (Exception exception)
        {
            UniversalPeaceRules.Logger.LogError($"Could not apply universal peace rules: {exception}");
        }
    }
}

[HarmonyPatch]
internal static class UniversalPeaceAbilityUnlockPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(GameLogicData),
        nameof(GameLogicData.IsUnlocked),
        new[] { typeof(PlayerAbility.Type), typeof(PlayerState) }
    );

    [HarmonyPostfix]
    private static void UnlockPeaceForEveryone(PlayerAbility.Type __0, ref bool __result)
    {
        if (__0 == PlayerAbility.Type.PeaceTreaty) __result = true;
    }
}

[HarmonyPatch]
internal static class UniversalPeaceHasAbilityPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(GameLogicData),
        nameof(GameLogicData.HasAbility),
        new[] { typeof(PlayerState), typeof(PlayerAbility.Type) }
    );

    [HarmonyPostfix]
    private static void GivePeaceToEveryone(PlayerAbility.Type __1, ref bool __result)
    {
        if (__1 == PlayerAbility.Type.PeaceTreaty) __result = true;
    }
}

[HarmonyPatch(typeof(GameLogicData), nameof(GameLogicData.GetUnlockedAbilities))]
internal static class UniversalPeaceUnlockedAbilitiesPatch
{
    [HarmonyPostfix]
    private static void IncludePeaceInUnlockedAbilities(
        ref Il2CppSystem.Collections.Generic.List<PlayerAbility.Type> __result
    )
    {
        if (__result != null && !__result.Contains(PlayerAbility.Type.PeaceTreaty))
        {
            __result.Add(PlayerAbility.Type.PeaceTreaty);
        }
    }
}

[HarmonyPatch]
internal static class UniversalPeaceRequiredTechPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(GameLogicData),
        nameof(GameLogicData.GetRequiredTech),
        new[] { typeof(TribeData), typeof(PlayerAbility.Type) }
    );

    [HarmonyPostfix]
    private static void UseHiddenBasicTech(
        GameLogicData __instance,
        PlayerAbility.Type __1,
        ref TechData __result
    )
    {
        if (__1 == PlayerAbility.Type.PeaceTreaty)
        {
            __result = __instance.GetTechData(TechData.Type.Basic);
        }
    }
}

/// <summary>
/// Remove the peace icon before either Strategy UI renderer reads its unlock
/// list. This also protects saves whose GameLogicData was created before the
/// parse postfix ran.
/// </summary>
[HarmonyPatch(typeof(TechItem), nameof(TechItem.GetUnlockItems))]
internal static class UniversalPeaceTechTreePatch
{
    [HarmonyPrefix]
    private static void HidePeaceFromTechTree(TechData techData)
    {
        UniversalPeaceRules.PrepareVisibleTech(techData);
    }
}

[HarmonyPatch(typeof(TechPopupContent), nameof(TechPopupContent.CreateTechDataContent))]
internal static class UniversalPeaceTechPopupPatch
{
    [HarmonyPrefix]
    private static void HidePeaceFromTechPopup(TechData data)
    {
        UniversalPeaceRules.PrepareVisibleTech(data);
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.CreateDiplomacyActionButton))]
internal static class UniversalPeaceDiplomacyButtonPatch
{
    [HarmonyPostfix]
    private static void ShowPeaceAsAvailable(
        PlayerInfoPopup __instance,
        CommandBase command,
        UIRoundButton __result
    )
    {
        if (!UniversalPeaceRules.IsPeaceCommand(command) || __result == null) return;

        // Clear the native unlocked and unavailable callbacks together. The
        // latter is what produced a Strategy warning after a successful send.
        __result.ClearCallbacks();
        __result.ButtonEnabled = true;
        __result.BlockButton = false;
        __result.buttonActive = true;

        Action action = () => __instance.DiplomacyButton_OnClicked(command);
        UniversalPeaceButtonRegistry.Register(__result, action);
        __result.OnClickedSignal.Add(
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                () => UniversalPeaceButtonRegistry.TryExecute(__result)
            )
        );
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.UpdateDiplomacyActionButtons))]
internal static class UniversalPeaceButtonRegistryResetPatch
{
    [HarmonyPrefix]
    private static void ResetPeaceButtons() => UniversalPeaceButtonRegistry.Clear();
}

[HarmonyPatch(typeof(UIButtonBase), nameof(UIButtonBase.OnPointerClick))]
internal static class UniversalPeacePointerClickPatch
{
    [HarmonyPrefix]
    private static bool RoutePeacePointerClick(UIButtonBase __instance) =>
        !UniversalPeaceButtonRegistry.TryExecute(__instance);
}

[HarmonyPatch(typeof(UIButtonBase), nameof(UIButtonBase.OnButtonClicked))]
internal static class UniversalPeaceButtonClickPatch
{
    [HarmonyPrefix]
    private static bool RoutePeaceButtonClick(UIButtonBase __instance) =>
        !UniversalPeaceButtonRegistry.TryExecute(__instance);
}

[HarmonyPatch(
    typeof(PlayerInfoPopup),
    nameof(PlayerInfoPopup.OnUnavailableDiplomacyCommandClicked)
)]
internal static class UniversalPeaceUnavailablePopupPatch
{
    [HarmonyPrefix]
    private static bool SuppressObsoleteStrategyWarning(CommandBase command)
    {
        // The native control can retain both its action and unavailable
        // callbacks. The action already sends the request; suppress only the
        // stale Strategy warning so the command is never executed twice.
        return !UniversalPeaceRules.IsPeaceCommand(command);
    }
}

[HarmonyPatch(typeof(AI), nameof(AI.AddPossibleDiplomacyCommands))]
internal static class UniversalPeaceAIPreparePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void GiveBotsUniversalPeace(GameState gameState)
    {
        if (gameState?.GameLogicData != null)
        {
            UniversalPeaceRules.Apply(gameState.GameLogicData);
        }
    }
}
