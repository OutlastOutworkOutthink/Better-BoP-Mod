using BepInEx.Logging;
using HarmonyLib;
using Polytopia.Data;

namespace BetterBoPMod;

/// <summary>
/// Centralizes rules that must be identical in local and Integrated games.
/// Polytopia can replace its GameLogicData while opening an online state, so
/// this method is deliberately safe to run more than once.
/// </summary>
internal static class BetterBoPRules
{
    internal const int WarriorHealth = 150;
    internal static ManualLogSource Logger { get; set; } = null!;

    internal static void Apply(GameLogicData data)
    {
        if (data.TryGetData(UnitData.Type.Warrior, out UnitData warrior))
        {
            warrior.health = WarriorHealth;
        }

        TechData basic = data.GetTechData(TechData.Type.Basic);
        TechData strategy = data.GetTechData(TechData.Type.Shields);
        TechData diplomacy = data.GetTechData(TechData.Type.Diplomacy);

        MoveAbility(PlayerAbility.Type.Embassy, diplomacy, strategy);
        MoveAbility(PlayerAbility.Type.CapitalVision, diplomacy, strategy);
        AddAbility(basic, PlayerAbility.Type.PeaceTreaty);

        // Vanilla embassy income is two stars per embassy level. Strategy
        // unlocks a one-star embassy; DiplomacyIncomePatch adds the second star
        // for players who have subsequently researched Diplomacy.
        data.DiplomacyData.embassyIncome = 1;
    }

    private static void MoveAbility(PlayerAbility.Type ability, TechData from, TechData to)
    {
        while (from.abilityUnlocks.Contains(ability)) from.abilityUnlocks.Remove(ability);
        AddAbility(to, ability);
    }

    private static void AddAbility(TechData tech, PlayerAbility.Type ability)
    {
        if (!tech.abilityUnlocks.Contains(ability)) tech.abilityUnlocks.Add(ability);
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
            BetterBoPRules.Logger.LogInfo("Applied Better BoP Strategy, Diplomacy, peace, and embassy rules.");
        }
        catch (Exception exception)
        {
            BetterBoPRules.Logger.LogError($"Failed to apply parsed Better BoP rules: {exception}");
        }
    }
}

[HarmonyPatch(typeof(StartTurnAction), nameof(StartTurnAction.ExecuteDefault))]
internal static class DiplomacyIncomePatch
{
    [HarmonyPostfix]
    private static void AddDiplomacyEmbassyIncome(GameState gameState)
    {
        try
        {
            if (!gameState.TryGetPlayer(gameState.CurrentPlayer, out PlayerState player)) return;
            if (!gameState.GameLogicData.IsUnlocked(TechData.Type.Diplomacy, player)) return;

            int extraIncome = 0;
            foreach (var relation in player.relations)
            {
                extraIncome += Math.Max(0, relation.Value.EmbassyLevel);
            }
            if (extraIncome <= 0) return;

            player.Currency += extraIncome;
            ResourceEvents.ResourceAdded(player.Id, ResourceManager.Type.Currency, extraIncome, player.Currency);
            ResourceEvents.RefreshWallets(player.Id);
        }
        catch (Exception exception)
        {
            BetterBoPRules.Logger.LogError($"Failed to add Diplomacy embassy income: {exception}");
        }
    }
}
