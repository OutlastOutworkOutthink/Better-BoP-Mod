using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Polytopia.Data;
using PolytopiaBackendBase.Game;
using System.Reflection;

namespace BetterBoPMod;

/// <summary>
/// Loads the runtime patches needed to keep Better Battle of Polytopia rules
/// active when Polytopia loads fresh game-logic data for an online match.
/// </summary>
public static class Main
{
    private static bool loaded;

    public static void Load(ManualLogSource logger)
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        GameLogicDataPatch.Logger = logger;
        OnlineGameStatePatch.Logger = logger;
        DiscordIntegrationPatch.Initialize(logger);
        Harmony.CreateAndPatchAll(typeof(GameLogicDataPatch));
        Harmony.CreateAndPatchAll(typeof(OnlineGameStatePatch));
        Harmony.CreateAndPatchAll(typeof(DiscordIntegrationPatch));
        logger.LogMessage("Better Battle of Polytopia Mod multiplayer and Discord integration hooks loaded.");
    }
}

/// <summary>
/// Covers the other online path: a fully constructed state received from the
/// Polytopia backend. The state must contain the same rules before commands are
/// replayed or displayed by the client.
/// </summary>
[HarmonyPatch]
internal static class OnlineGameStatePatch
{
    private const int WarriorHealth = 150;

    internal static ManualLogSource Logger { get; set; } = null!;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredMethods(typeof(ClientBase)).Where(method =>
            (method.Name == nameof(ClientBase.UpdateGameState) ||
             method.Name == nameof(ClientBase.UpdateGameStateImmediate)) &&
            method.GetParameters().Length > 0 &&
            method.GetParameters()[0].ParameterType == typeof(GameState)
        );
    }

    [HarmonyPrefix]
    private static void ApplyToReceivedState(GameState __0)
    {
        try
        {
            if (!__0.GameLogicData.TryGetData(UnitData.Type.Warrior, out UnitData warrior))
            {
                Logger.LogWarning(
                    "Could not apply Warrior health to an online game state."
                );
                return;
            }

            int previousHealth = warrior.health;
            warrior.health = WarriorHealth;
            Logger.LogInfo(
                $"Applied Warrior health to received game state: " +
                $"{previousHealth} -> {WarriorHealth}."
            );
        }
        catch (Exception exception)
        {
            Logger.LogError($"Failed to patch a received online state: {exception}");
        }
    }
}

/// <summary>
/// PolyMod normally merges patch.json during its initial game-logic load. An
/// online session can parse another copy later, so this prefix applies the
/// required rule on every parse rather than only the first one.
/// </summary>
[HarmonyPatch(typeof(GameLogicData), nameof(GameLogicData.AddGameLogicPlaceholders))]
internal static class GameLogicDataPatch
{
    private const int WarriorHealth = 150;

    internal static ManualLogSource Logger { get; set; } = null!;

    [HarmonyPrefix]
    private static void ApplyBetterBoPRules(ref JObject rootObject)
    {
        try
        {
            if (rootObject["unitData"]?["warrior"] is not JObject warrior)
            {
                Logger.LogWarning(
                    "Could not apply Warrior health: unitData.warrior was not present."
                );
                return;
            }

            int? previousHealth = warrior.Value<int?>("health");
            warrior["health"] = WarriorHealth;

            Logger.LogInfo(
                $"Applied Warrior health during game-logic load: " +
                $"{previousHealth?.ToString() ?? "missing"} -> {WarriorHealth}."
            );
        }
        catch (Exception exception)
        {
            Logger.LogError($"Failed to apply multiplayer game logic: {exception}");
        }
    }
}
