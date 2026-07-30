using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Polytopia.Data;
using PolytopiaBackendBase.Common;
using System.Reflection;
using UnityEngine;

namespace BetterBoPMod;

/// <summary>
/// Centralizes rules that must be identical in local and Integrated games.
/// Polytopia can replace its GameLogicData while opening an online state, so
/// this method is deliberately safe to run more than once.
/// </summary>
internal static class BetterBoPRules
{
    internal static ManualLogSource Logger { get; set; } = null!;

    internal static void Apply(GameLogicData data)
    {
        TechData basic = data.GetTechData(TechData.Type.Basic);
        TechData strategy = data.GetTechData(TechData.Type.Shields);
        TechData diplomacy = data.GetTechData(TechData.Type.Diplomacy);
        TechData meditation = data.GetTechData(TechData.Type.Meditation);
        TechData philosophy = data.GetTechData(TechData.Type.Philosophy);

        MoveAbility(PlayerAbility.Type.Embassy, diplomacy, strategy);
        MoveAbility(PlayerAbility.Type.CapitalVision, diplomacy, strategy);

        // Peace is intentionally not attached to any technology. The
        // AlwaysAvailablePeacePatch below supplies the ability to every tribe.
        foreach (var tech in data.AllTechData)
        {
            RemoveAbility(tech.Value, PlayerAbility.Type.PeaceTreaty);
        }
        RemoveAbility(basic, PlayerAbility.Type.PeaceTreaty);

        UnitData mindBender = data.GetUnitData(UnitData.Type.MindBender);
        while (philosophy.unitUnlocks.Contains(mindBender)) philosophy.unitUnlocks.Remove(mindBender);
        if (!meditation.unitUnlocks.Contains(mindBender)) meditation.unitUnlocks.Add(mindBender);

        TribeData aimo = data.GetTribeData(TribeType.Aimo);
        aimo.startingUnit = mindBender;
        aimo.startingTech.Clear();
        aimo.startingTech.Add(meditation);

        // Strategy embassies start at one star per level (one normally, two
        // during peace). DiplomacyEmbassyIncomePatch doubles the final value.
        data.DiplomacyData.embassyIncome = 1;
    }

    private static void MoveAbility(PlayerAbility.Type ability, TechData from, TechData to)
    {
        RemoveAbility(from, ability);
        if (!to.abilityUnlocks.Contains(ability)) to.abilityUnlocks.Add(ability);
    }

    private static void RemoveAbility(TechData tech, PlayerAbility.Type ability)
    {
        while (tech.abilityUnlocks.Contains(ability)) tech.abilityUnlocks.Remove(ability);
    }
}

[HarmonyPatch(typeof(GameLogicData), nameof(GameLogicData.ParseGameLogicObjects))]
internal static class BetterBoPParsedRulesPatch
{
    [HarmonyPostfix]
    private static void ApplyParsedRules(GameLogicData __instance)
    {
        try
        {
            BetterBoPRules.Apply(__instance);
            BetterBoPRules.Logger.LogInfo("Applied Better BoP technology, diplomacy, embassy, Mind Bender, and Ai-Mo rules.");
        }
        catch (Exception exception)
        {
            BetterBoPRules.Logger.LogError($"Failed to apply parsed Better BoP rules: {exception}");
        }
    }
}

[HarmonyPatch(typeof(PlayerDiplomacyExtensions), nameof(PlayerDiplomacyExtensions.GetIncomeFromEmbassy))]
internal static class DiplomacyEmbassyIncomePatch
{
    [HarmonyPostfix]
    private static void DoubleDiplomacyEmbassyIncome(PlayerState playerState, GameState gameState, ref int __result)
    {
        if (__result > 0 && gameState.GameLogicData.IsUnlocked(TechData.Type.Diplomacy, playerState))
        {
            __result *= 2;
        }
    }
}

[HarmonyPatch]
internal static class AlwaysAvailablePeacePatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(GameLogicData),
        nameof(GameLogicData.IsUnlocked),
        new[] { typeof(PlayerAbility.Type), typeof(PlayerState) }
    );

    [HarmonyPostfix]
    private static void MakePeaceAvailable(PlayerAbility.Type abilityType, ref bool __result)
    {
        if (abilityType == PlayerAbility.Type.PeaceTreaty) __result = true;
    }
}

[HarmonyPatch]
internal static class AlwaysAvailablePeaceHasAbilityPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(GameLogicData),
        nameof(GameLogicData.HasAbility),
        new[] { typeof(PlayerState), typeof(PlayerAbility.Type) }
    );

    [HarmonyPostfix]
    private static void GiveEveryTribePeace(PlayerAbility.Type ability, ref bool __result)
    {
        if (ability == PlayerAbility.Type.PeaceTreaty) __result = true;
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.CreateDiplomacyActionButton))]
internal static class DiplomacyActionButtonRulesPatch
{
    [HarmonyPostfix]
    private static void CorrectDiplomacyActionButton(
        PlayerInfoPopup __instance,
        CommandBase command,
        GameState gameState,
        UIRoundButton __result
    )
    {
        bool isPeace = command is PeaceTreatyCommand;
        bool isEmbassy = command is EstablishEmbassyCommand;
        if (!isPeace && !isEmbassy) return;

        PlayerState local = GameManager.LocalPlayer;
        bool available = isPeace || gameState.GameLogicData.IsUnlocked(TechData.Type.Shields, local);
        __result.buttonActive = available;
        __result.BlockButton = false;
        __result.OnClickedSignal.Clear();
        __result.OnClickedSignal.Add(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() =>
        {
            if (available)
            {
                __instance.DiplomacyButton_OnClicked(command);
            }
            else
            {
                __instance.OnUnavailableDiplomacyCommandClicked(
                    command,
                    gameState.GameLogicData.GetTechData(TechData.Type.Shields)
                );
            }
        }));
    }
}

[HarmonyPatch(typeof(PlayerAbilityExtensions), nameof(PlayerAbilityExtensions.GetDescription))]
internal static class EmbassyDescriptionPatch
{
    [HarmonyPostfix]
    private static void UseExactEmbassyIncome(PlayerAbility.Type __0, ref string __result)
    {
        if (__0 == PlayerAbility.Type.Embassy)
        {
            __result = "Establish an embassy in another tribe's capital. It gives both tribes 1 star per turn, or 2 stars per turn while they have a peace treaty. After researching Diplomacy, those amounts double to 2 and 4 stars per turn.";
        }
    }
}

[HarmonyPatch(typeof(TechPopupContent), nameof(TechPopupContent.CreateTechDataContent))]
internal static class BetterBoPTechPopupPatch
{
    [HarmonyPostfix]
    private static void AddBetterBoPTechInformation(TechData data, UIBasicComponent __result)
    {
        if (__result == null) return;

        if (data.type == TechData.Type.Shields)
        {
            AddInfo(
                __result.rectTransform,
                SpriteRef.UI_STARICON,
                "Gift Stars",
                "Choose another tribe and send 5, 10, or 20 stars. The receiving tribe gets 80% of the amount sent. Star gifts to bots create the Generous boon."
            );
        }
        else if (data.type == TechData.Type.Diplomacy)
        {
            AddInfo(
                __result.rectTransform,
                SpriteRef.UI_EMBASSY,
                "Embassy Income Doubled",
                "All current and future embassies give both tribes 2 stars per turn, or 4 stars per turn while they have a peace treaty."
            );
        }
    }

    private static void AddInfo(RectTransform parent, int sprite, string header, string description)
    {
        UIRoundButton_UI2 button = UILibrary.NewRoundButton(parent)
            .SetStyle(UIButtonBase_UI2.ButtonStyle.Default)
            .SetButtonSize(UIRoundButton_UI2.ButtonSize.ExtraLarge)
            .SetSprite(sprite, 0.7f);
        button.Text = header;
        TechPopupContent.AddInfoPopup(button, header, description);
        button.UpdateLabelVisibility();
        button.RunLayout();
    }
}

[HarmonyPatch(typeof(TribeData), "get_description")]
internal static class AimoDescriptionPatch
{
    [HarmonyPostfix]
    private static void DescribeAimoStart(TribeData __instance, ref string __result)
    {
        if (__instance.type != TribeType.Aimo) return;

        __result = (__result ?? string.Empty)
            .Replace("Philosophy", "Meditation", StringComparison.OrdinalIgnoreCase)
            .Replace("Warrior", "Mind Bender", StringComparison.OrdinalIgnoreCase);
        if (!__result.Contains("Mind Bender", StringComparison.OrdinalIgnoreCase) ||
            !__result.Contains("Meditation", StringComparison.OrdinalIgnoreCase))
        {
            __result = $"{__result.Trim()}\n\nStarts with Meditation and a Mind Bender.";
        }
    }
}
