using BepInEx.Logging;
using HarmonyLib;
using Polytopia.Data;
using System.Reflection;

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
    private static void ShowPeaceAsAvailable(CommandBase command, UIRoundButton __result)
    {
        if (command is not PeaceTreatyCommand || __result == null) return;

        __result.ButtonEnabled = true;
        __result.BlockButton = false;
        __result.buttonActive = true;
    }
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
        return command is not PeaceTreatyCommand;
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
