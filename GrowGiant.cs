using HarmonyLib;
using Polytopia.Data;
using PolytopiaBackendBase.Common;
using System.Reflection;
using UnityEngine;
using GameTerrainData = Polytopia.Data.TerrainData;

namespace BetterBoPMod;

/// <summary>
/// Grow Giant deliberately reuses NullBuilding, an otherwise unused native
/// improvement type. That keeps construction, saves, replays, and Integrated
/// multiplayer on Polytopia's normal serialized BuildCommand path.
/// </summary>
internal static class GrowGiant
{
    internal const int SeedCost = 20;
    internal const string AbilityName = "Grow Giant";
    internal const string SeedName = "Giant Seed";
    internal const string Description =
        "Build a Giant Seed on an empty field in your territory for 20 stars. " +
        "The field keeps its normal terrain and grows into a Giant at the start " +
        "of your next turn. A unit on the tile uses normal Giant push rules.";

    internal static void Apply(GameLogicData data)
    {
        TechData spiritualism = data.GetTechData(TechData.Type.Spiritualism);
        ImprovementData seed = data.GetImprovementData(ImprovementData.Type.NullBuilding);

        seed.hidden = false;
        seed.cost = SeedCost;
        seed.work = 0;
        seed.borderSize = 0;
        seed.maxLevel = 1;
        seed.range = 0;
        seed.growthRate = 0;

        seed.improvementAbilities.Clear();
        seed.improvementAbilities.Add(ImprovementAbility.Type.Simple);
        seed.creates.Clear();
        seed.rewards.Clear();
        seed.terrainRequirements.Clear();
        seed.terrainRequirements.Add(new TerrainRequirements
        {
            terrain = data.GetTerrainData(GameTerrainData.Type.Field),
        });
        seed.adjacencyRequirements.Clear();
        seed.adjacencyImprovements.Clear();
        seed.routes.Clear();
        seed.growthRewards.Clear();

        foreach (var tech in data.AllTechData)
        {
            while (tech.Value.improvementUnlocks.Contains(seed))
            {
                tech.Value.improvementUnlocks.Remove(seed);
            }
        }
        spiritualism.improvementUnlocks.Add(seed);
    }

    internal static bool IsBasicTribe(GameLogicData data, PlayerState player)
    {
        return data.GetTribeData(player.tribe).category == TribeData.CategoryEnum.Human;
    }

    internal static Sprite? GetDefaultHeadSprite(SpriteAtlasManager atlasManager)
    {
        try
        {
            SpriteAddress address = SpriteData.GetHeadSpriteAddress(TribeType.None);
            return atlasManager.GetSprite(address.sprite, address.atlas);
        }
        catch (Exception exception)
        {
            BetterBoPRules.Logger.LogError($"Could not load the tribeless head for Giant Seed: {exception}");
            return null;
        }
    }
}

[HarmonyPatch]
internal static class GrowGiantUnlockPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(GameLogicData),
        nameof(GameLogicData.IsUnlocked),
        new[] { typeof(ImprovementData.Type), typeof(PlayerState) }
    );

    [HarmonyPostfix]
    private static void RestrictGrowGiantToBasicTribes(
        GameLogicData __instance,
        ImprovementData.Type type,
        PlayerState player,
        ref bool __result
    )
    {
        if (type != ImprovementData.Type.NullBuilding) return;
        __result = GrowGiant.IsBasicTribe(__instance, player) &&
            __instance.IsUnlocked(TechData.Type.Spiritualism, player);
    }
}

[HarmonyPatch(typeof(GameLogicData), nameof(GameLogicData.CanBuild))]
internal static class GrowGiantBuildRulesPatch
{
    [HarmonyPostfix]
    private static void ValidateGiantSeedTile(
        GameLogicData __instance,
        GameState gameState,
        TileData tile,
        PlayerState playerState,
        ImprovementData improvement,
        ref bool __result
    )
    {
        if (improvement.type != ImprovementData.Type.NullBuilding) return;

        __result = GrowGiant.IsBasicTribe(__instance, playerState) &&
            __instance.IsUnlocked(TechData.Type.Spiritualism, playerState) &&
            tile.owner == playerState.Id &&
            tile.terrain == GameTerrainData.Type.Field &&
            tile.improvement == null &&
            tile.resource == null;
    }
}

[HarmonyPatch(typeof(StartTurnAction), nameof(StartTurnAction.Execute))]
internal static class GrowGiantStartTurnPatch
{
    [HarmonyPrefix]
    private static void HatchReadySeeds(StartTurnAction __instance, GameState state)
    {
        try
        {
            if (!state.TryGetPlayer(__instance.PlayerId, out PlayerState player) ||
                !GrowGiant.IsBasicTribe(state.GameLogicData, player))
            {
                return;
            }

            foreach (TileData tile in state.Map.Tiles)
            {
                ImprovementState seed = tile.improvement;
                if (seed == null ||
                    seed.type != ImprovementData.Type.NullBuilding ||
                    seed.owner != player.Id ||
                    seed.GetAge(state) < 1)
                {
                    continue;
                }

                try
                {
                    if (tile.unit != null)
                    {
                        ActionUtils.TryPushUnitDefault(state, player.Id, tile);
                        if (tile.unit != null)
                        {
                            BetterBoPRules.Logger.LogWarning(
                                $"Giant Seed at {tile.coordinates} could not push its occupying unit; hatch deferred."
                            );
                            continue;
                        }
                    }

                    tile.SetImprovement(null!);
                    var train = new TrainAction(player.Id, UnitData.Type.Giant, tile.coordinates, 0);
                    train.Execute(state);
                    if (train.trainActionAborted || tile.unit == null)
                    {
                        tile.SetImprovement(seed);
                        BetterBoPRules.Logger.LogWarning(
                            $"Giant Seed at {tile.coordinates} could not create its Giant; hatch deferred."
                        );
                    }
                }
                catch (Exception seedException)
                {
                    BetterBoPRules.Logger.LogError(
                        $"Could not hatch Giant Seed at {tile.coordinates}: {seedException}"
                    );
                }
            }
        }
        catch (Exception exception)
        {
            BetterBoPRules.Logger.LogError($"Could not process Grow Giant at turn start: {exception}");
        }
    }
}

[HarmonyPatch(typeof(ImprovementData), "get_displayName")]
internal static class GiantSeedDisplayNamePatch
{
    [HarmonyPostfix]
    private static void NameGiantSeed(ImprovementData __instance, ref string __result)
    {
        if (__instance.type == ImprovementData.Type.NullBuilding) __result = GrowGiant.SeedName;
    }
}

[HarmonyPatch]
internal static class GiantSeedImprovementIconPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredMethods(typeof(UIUtils)).Where(method =>
            method.Name == nameof(UIUtils.GetImprovementSprite)
        );
    }

    [HarmonyPostfix]
    private static void UseTribelessHead(
        ImprovementData.Type improvement,
        SpriteAtlasManager atlasManager,
        ref Sprite __result
    )
    {
        if (improvement != ImprovementData.Type.NullBuilding) return;
        Sprite? head = GrowGiant.GetDefaultHeadSprite(atlasManager);
        if (head != null) __result = head;
    }
}

[HarmonyPatch]
internal static class GiantSeedWorldVisualPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredMethods(typeof(Building)).Where(method =>
            method.Name == nameof(Building.UpdateObject)
        );
    }

    [HarmonyPostfix]
    private static void ShowLargeTribelessHead(Building __instance)
    {
        if (__instance.data == null || __instance.data.type != ImprovementData.Type.NullBuilding) return;
        Sprite? head = GrowGiant.GetDefaultHeadSprite(GameManager.GetSpriteAtlasManager());
        if (head == null) return;

        foreach (PolytopiaSpriteRenderer renderer in __instance.spriteRenderers)
        {
            renderer.Sprite = head;
        }
    }
}

[HarmonyPatch(typeof(TechPopupContent), nameof(TechPopupContent.SetBuildingData))]
internal static class GrowGiantTechPopupPatch
{
    [HarmonyPostfix]
    private static void DescribeGrowGiant(UIRoundButton_UI2 button, ImprovementData data)
    {
        if (data.type != ImprovementData.Type.NullBuilding) return;

        Sprite? head = GrowGiant.GetDefaultHeadSprite(GameManager.GetSpriteAtlasManager());
        if (head != null) button.SetSprite(head, 0.58f);
        button.Text = GrowGiant.AbilityName;
        button.ClearCallbacks();
        // SetBuildingData already added Polytopia's normal small info badge.
        // Replace only its callback so the icon is not duplicated.
        TechPopupContent.AddInfoPopup(
            button,
            GrowGiant.AbilityName,
            GrowGiant.Description
        );
        button.UpdateLabelVisibility();
        button.RunLayout();
    }
}
