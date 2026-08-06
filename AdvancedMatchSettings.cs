using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Polytopia.Data;
using PolytopiaBackendBase.Game;
using UnityEngine;

namespace BetterBoPMod;

/// <summary>
/// Lightweight, per-game handicap settings for modded multiplayer setup.
/// The UI reuses Polytopia's own advanced-settings toggle and option rows;
/// gameplay is handled at the shared data accessors instead of enumerating
/// every train/build command separately.
/// </summary>
internal static class AdvancedMatchSettings
{
    private const string UnitSelectionKey = "betterbop.advanced.unit-cost.v1";
    private const string BuildingSelectionKey = "betterbop.advanced.building-cost.v1";
    private const string EnemyHealthSelectionKey = "betterbop.advanced.enemy-health.v1";
    private const string GameRulesKeyPrefix = "betterbop.advanced.game.v1.";
    private const string UnitListName = "BetterBoP.UnitCostMultiplier";
    private const string UnitDescriptionName = "BetterBoP.UnitCostDescription";
    private const string BuildingListName = "BetterBoP.BuildingCostMultiplier";
    private const string BuildingDescriptionName = "BetterBoP.BuildingCostDescription";
    private const string HealthListName = "BetterBoP.EnemyHealthMultiplier";
    private const string HealthDescriptionName = "BetterBoP.EnemyHealthDescription";
    private const char RulesMarker = '\u2063';
    private const char RulesValueBase = '\uFE00';
    private const int DefaultIndex = 2;

    private static readonly int[] Percentages = { 25, 50, 100, 150, 200, 300, 500 };
    private static readonly string[] Labels = { "25%", "50%", "100%", "150%", "200%", "300%", "500%" };
    private static readonly Dictionary<IntPtr, Controls> ControlsByView = new();
    private static ManualLogSource logger = null!;
    private static RuleSet pendingRules = RuleSet.Default;
    private static RuleSet activeRules = RuleSet.Default;
    private static byte activeRulesOwner = byte.MaxValue;
    private static bool hasActiveRulesOwner;
    [ThreadStatic] private static int unitCostScopeDepth;
    [ThreadStatic] private static int buildingCostScopeDepth;
    private static DateTime pendingSinceUtc;
    private static bool pending;

    internal static void Initialize(ManualLogSource logSource) => logger = logSource;

    internal static bool IsSupportedSetup()
    {
        GameSettings? settings = GameManager.PreliminaryGameSettings ?? GameManager.Instance?.settings;
        if (settings == null) return false;
        return settings.GameType is GameType.SinglePlayer or GameType.Multiplayer or
            GameType.Matchmaking or GameType.Competitive;
    }

    internal static void ArmNextGame()
    {
        if (!IsSupportedSetup()) return;
        pendingRules = SelectedRules();
        activeRules = pendingRules;
        hasActiveRulesOwner = false;
        pendingSinceUtc = DateTime.UtcNow;
        pending = true;
        GameSettings? settings = GameManager.PreliminaryGameSettings ?? GameManager.Instance?.settings;
        if (settings != null) settings.GameName = EmbedRules(settings.GameName, pendingRules);
        logger.LogInfo(
            $"Advanced match rules armed: units {pendingRules.UnitCostPercent}%, " +
            $"buildings {pendingRules.BuildingCostPercent}%, enemy health {pendingRules.EnemyHealthPercent}%."
        );
    }

    internal static void CapturePendingRules(Il2CppSystem.Guid gameId)
    {
        if (!pending || DateTime.UtcNow - pendingSinceUtc > TimeSpan.FromHours(6))
        {
            pending = false;
            return;
        }

        string id = gameId.ToString();
        hasActiveRulesOwner = false;
        SaveRules(id, pendingRules);
        pending = false;
        logger.LogInfo($"Advanced match rules attached to game {id}.");
    }

    internal static void CapturePendingRulesFromCurrentGame()
    {
        if (GameManager.Client == null)
        {
            if (!pending) activeRules = RuleSet.Default;
            pending = false;
            RefreshRulesOwner(GameManager.GameState);
            return;
        }
        Il2CppSystem.Nullable<Il2CppSystem.Guid> gameId = GameManager.Client.CurrentGameId;
        if (!gameId.HasValue)
        {
            if (!pending) activeRules = RuleSet.Default;
            pending = false;
            RefreshRulesOwner(GameManager.GameState);
            return;
        }
        if (pending) CapturePendingRules(gameId.Value);
        else LoadRulesForGame(gameId.Value.ToString(), GameManager.GameState);
        RefreshRulesOwner(GameManager.GameState);
    }

    internal static bool EnsureControls(GameSetupScreen_UI2 screen)
    {
        GameSetupScreenView? view = screen.view;
        if (view == null) return false;

        if (!IsSupportedSetup())
        {
            SetVisible(view, false);
            return false;
        }

        try
        {
            ControlsByView.TryGetValue(view.Pointer, out Controls? controls);
            if (controls != null && !controls.IsAlive)
            {
                ControlsByView.Remove(view.Pointer);
                controls = null;
            }

            if (controls == null)
            {
                UIHorizontalList_UI2? listTemplate = view.listMapSize ?? view.listGameMode ?? view.listNetwork;
                TextField_UI2? descriptionTemplate = view.mapSizeDescriptionText ?? view.gameConfigurationDescriptionText;
                if (listTemplate == null || descriptionTemplate == null || view.holder == null ||
                    view.allComponents == null || view.allLists == null)
                    return false;

                controls = new Controls(
                    CloneList(view, listTemplate, UnitListName, "Unit cost for you", UnitIndex(), SetUnitIndex),
                    CloneDescription(
                        view,
                        descriptionTemplate,
                        UnitDescriptionName,
                        "Multiplies how much your units cost, rounded up. Only your units are affected; bots pay normal prices."
                    ),
                    CloneList(view, listTemplate, BuildingListName, "Your building cost", BuildingIndex(), SetBuildingIndex),
                    CloneDescription(
                        view,
                        descriptionTemplate,
                        BuildingDescriptionName,
                        "Multiplies every tile-interaction cost, rounded up, including roads and special-tribe buildings."
                    ),
                    CloneList(view, listTemplate, HealthListName, "Enemy unit health", EnemyHealthIndex(), SetEnemyHealthIndex),
                    CloneDescription(
                        view,
                        descriptionTemplate,
                        HealthDescriptionName,
                        "Changes the maximum health of every opposing unit by this percentage."
                    )
                );
                ControlsByView[view.Pointer] = controls;
                logger.LogInfo("Added three native advanced match setting rows.");
            }

            KeepNativeMapRowsVisible(view);
            NormalizeComponentOrder(view, controls);
            RefreshControls(controls);
            SetVisible(view, screen.advancedSettingsExpanded);
            view.SetShowAdvancedSettingsToggleButton(
                screen.advancedSettingsExpanded ? "Hide Advanced Settings" : "Show Advanced Settings"
            );
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Could not render advanced match settings yet: {exception.Message}");
            return false;
        }
    }

    internal static void RefreshVisibility(GameSetupScreen_UI2 screen)
    {
        if (screen?.view == null) return;
        bool target = IsSupportedSetup();
        if (target) KeepNativeMapRowsVisible(screen.view);
        SetVisible(screen.view, target && screen.advancedSettingsExpanded);
        if (target)
        {
            screen.view.SetShowAdvancedSettingsToggleButton(
                screen.advancedSettingsExpanded ? "Hide Advanced Settings" : "Show Advanced Settings"
            );
        }
    }

    internal static void RefreshVisibilityAfterLayout(GameSetupScreen_UI2 screen)
    {
        RefreshVisibility(screen);
    }

    internal static void RefreshAfterToggle(GameSetupScreen_UI2 screen, bool previouslyExpanded)
    {
        if (screen == null || !IsSupportedSetup()) return;

        // The native handler normally flips this field. Some setup variants do
        // not, so guarantee exactly one state change without double-toggling.
        if (screen.advancedSettingsExpanded == previouslyExpanded)
            screen.advancedSettingsExpanded = !previouslyExpanded;

        if (!EnsureControls(screen)) return;
        screen.UpdateLayout();
    }

    internal static UnitCostScope BeginUnitCostScope(GameState? state, UnitData.Type? only = null)
    {
        UnitCostScope scope = new();
        if (state?.GameLogicData == null || !IsRulesOwnerTurn(state) || activeRules.UnitCostPercent == 100)
            return scope;
        if (unitCostScopeDepth != 0) return scope;
        unitCostScopeDepth = 1;
        scope.OwnsScope = true;

        HashSet<IntPtr> seen = new();
        IEnumerable<UnitData.Type> types = only.HasValue
            ? new[] { only.Value }
            : Enum.GetValues(typeof(UnitData.Type)).Cast<UnitData.Type>();
        foreach (UnitData.Type type in types)
        {
            try
            {
                UnitData? data = state.GameLogicData.GetUnitData(type);
                if (data == null || data.Pointer == IntPtr.Zero || !seen.Add(data.Pointer)) continue;
                int original = data.cost;
                int scaled = Scale(original, activeRules.UnitCostPercent);
                if (scaled == original) continue;
                scope.Add(data, original);
                data.cost = scaled;
            }
            catch
            {
                // Some enum values are intentionally absent for particular rulesets.
            }
        }
        return scope;
    }

    internal static BuildingCostScope BeginBuildingCostScope(GameState? state, ImprovementData.Type? only = null)
    {
        BuildingCostScope scope = new();
        if (state?.GameLogicData == null || !IsRulesOwnerTurn(state) || activeRules.BuildingCostPercent == 100)
            return scope;
        if (buildingCostScopeDepth != 0) return scope;
        buildingCostScopeDepth = 1;
        scope.OwnsScope = true;

        HashSet<IntPtr> seen = new();
        IEnumerable<ImprovementData.Type> types = only.HasValue
            ? new[] { only.Value }
            : Enum.GetValues(typeof(ImprovementData.Type)).Cast<ImprovementData.Type>();
        foreach (ImprovementData.Type type in types)
        {
            try
            {
                ImprovementData? data = state.GameLogicData.GetImprovementData(type);
                if (data == null || data.Pointer == IntPtr.Zero || !seen.Add(data.Pointer)) continue;
                int original = data.cost;
                int scaled = Scale(original, activeRules.BuildingCostPercent);
                if (scaled == original) continue;
                scope.Add(data, original);
                data.cost = scaled;
            }
            catch
            {
                // Some enum values are intentionally absent for particular rulesets.
            }
        }
        return scope;
    }

    internal static int ScaleEnemyMaxHealth(int value, UnitState? unit, GameState? state)
    {
        if (unit == null || state == null || !TryGetRulesOwner(state, out byte rulesOwner) || unit.owner == rulesOwner ||
            unit.owner == PlayerState.NO_PLAYER_ID || unit.owner == PlayerState.NATURE_PLAYER_ID)
            return value;
        return Scale(value, activeRules.EnemyHealthPercent);
    }

    internal static void SetSpawnedUnitHealth(UnitState? unit, GameState? state)
    {
        if (unit == null || state == null || !TryGetRulesOwner(state, out byte rulesOwner) || unit.owner == rulesOwner ||
            unit.owner == PlayerState.NO_PLAYER_ID || unit.owner == PlayerState.NATURE_PLAYER_ID)
            return;

        int maxHealth = UnitDataExtensions.GetMaxHealth(unit, state);
        unit.health = (ushort)Math.Clamp(maxHealth, 1, ushort.MaxValue);
    }

    private static UIHorizontalList_UI2 CloneList(
        GameSetupScreenView view,
        UIHorizontalList_UI2 template,
        string name,
        string header,
        int selectedIndex,
        Action<int> onSelected
    )
    {
        GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, view.holder);
        clone.name = name;
        UIHorizontalList_UI2 list = clone.GetComponent<UIHorizontalList_UI2>();
        SignalPayload<int> signal = new();
        signal.Add(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>(onSelected));
        list.onItemSelected = signal;
        list.onItemHighlighted = new SignalPayload<int>();
        list.onDisabledItemClicked = new Signal();
        list.SetData(header, PercentageLabels(), selectedIndex);
        view.allLists.Add(list);
        return list;
    }

    private static TextField_UI2 CloneDescription(
        GameSetupScreenView view,
        TextField_UI2 template,
        string name,
        string text
    )
    {
        GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, view.holder);
        clone.name = name;
        TextField_UI2 description = clone.GetComponent<TextField_UI2>();
        description.SetText(text);
        description.UpdateSize();
        return description;
    }

    private static void KeepNativeMapRowsVisible(GameSetupScreenView view)
    {
        // Map controls remain outside Better BoP's advanced section, but only
        // when vanilla included them for this particular setup variant.
        if ((view.whatToShow & GameSetupScreenView.Show.MapTypeList) != 0 && view.listMapType != null)
            view.listMapType.ActiveSelf = true;
        if ((view.whatToShow & GameSetupScreenView.Show.MapSizeList) != 0 && view.listMapSize != null)
            view.listMapSize.ActiveSelf = true;
    }

    private static void NormalizeComponentOrder(GameSetupScreenView view, Controls controls)
    {
        if (view.allComponents == null || view.advancedSettingsToggle == null) return;

        List<UIBasicComponent> ordered = controls.Components.ToList();
        int toggleIndex = FindComponentIndex(view, view.advancedSettingsToggle);
        int mapTypeIndex = FindComponentIndex(view, view.listMapType);
        int mapSizeIndex = FindComponentIndex(view, view.listMapSize);
        bool alreadyOrdered = toggleIndex > Math.Max(mapTypeIndex, mapSizeIndex);
        for (int offset = 0; alreadyOrdered && offset < ordered.Count; offset++)
            alreadyOrdered = FindComponentIndex(view, ordered[offset]) == toggleIndex + offset + 1;
        if (alreadyOrdered) return;

        IUILayoutable? toggleLayout = null;
        for (int index = view.allComponents.Count - 1; index >= 0; index--)
        {
            IUILayoutable? entry = view.allComponents[index];
            if (Matches(entry, view.advancedSettingsToggle))
            {
                toggleLayout ??= entry;
                view.allComponents.RemoveAt(index);
                continue;
            }

            if (!ordered.Any(component => Matches(entry, component))) continue;
            view.allComponents.RemoveAt(index);
        }

        int anchor = Math.Max(
            FindComponentIndex(view, view.listMapType),
            Math.Max(
                FindComponentIndex(view, view.listMapSize),
                FindComponentIndex(view, view.mapSizeDescriptionText)
            )
        );
        int insertAt = Math.Clamp(anchor + 1, 0, view.allComponents.Count);
        view.allComponents.Insert(insertAt++, toggleLayout ?? AsLayoutable(view.advancedSettingsToggle));
        foreach (UIBasicComponent component in ordered)
            view.allComponents.Insert(insertAt++, AsLayoutable(component));
    }

    private static int FindComponentIndex(GameSetupScreenView view, UIBasicComponent? component)
    {
        if (component == null || view.allComponents == null) return -1;
        for (int index = 0; index < view.allComponents.Count; index++)
        {
            if (Matches(view.allComponents[index], component)) return index;
        }
        return -1;
    }

    private static bool Matches(IUILayoutable? layout, UIBasicComponent component)
    {
        if (layout == null || component == null) return false;
        if (layout.Pointer == component.Pointer) return true;
        try { return layout.TryCast<UIBasicComponent>()?.Pointer == component.Pointer; }
        catch { return false; }
    }

    private static IUILayoutable AsLayoutable(UIBasicComponent component)
    {
        try { return component.TryCast<IUILayoutable>() ?? new IUILayoutable(component.Pointer); }
        catch { return new IUILayoutable(component.Pointer); }
    }

    private static void SetVisible(GameSetupScreenView view, bool visible)
    {
        if (!ControlsByView.TryGetValue(view.Pointer, out Controls? controls) || !controls.IsAlive) return;
        foreach (UIBasicComponent component in controls.Components) component.ActiveSelf = visible;
    }

    private static void RefreshControls(Controls controls)
    {
        int unit = UnitIndex();
        int building = BuildingIndex();
        int health = EnemyHealthIndex();
        if (controls.UnitCost.SelectedIndex != unit)
            controls.UnitCost.SetData("Unit cost for you", PercentageLabels(), unit);
        if (controls.BuildingCost.SelectedIndex != building)
            controls.BuildingCost.SetData("Your building cost", PercentageLabels(), building);
        if (controls.EnemyHealth.SelectedIndex != health)
            controls.EnemyHealth.SetData("Enemy unit health", PercentageLabels(), health);
    }

    private static Il2CppSystem.Collections.Generic.List<string> PercentageLabels()
    {
        Il2CppSystem.Collections.Generic.List<string> result = new();
        foreach (string label in Labels) result.Add(label);
        return result;
    }

    private static RuleSet SelectedRules() => new(
        Percentages[UnitIndex()],
        Percentages[BuildingIndex()],
        Percentages[EnemyHealthIndex()]
    );

    private static void LoadRulesForGame(string id, GameState? state)
    {
        if (state?.Settings != null && TryReadEmbeddedRules(state.Settings.GameName, out RuleSet embedded))
        {
            activeRules = embedded;
            SaveRules(id, activeRules);
        }
        else
        {
            string serialized = PlayerPrefs.GetString(GameRulesKeyPrefix + id, string.Empty);
            activeRules = RuleSet.TryParse(serialized, out RuleSet parsed) ? parsed : RuleSet.Default;
        }
    }

    private static string EmbedRules(string? gameName, RuleSet rules)
    {
        string clean = StripEmbeddedRules(gameName ?? string.Empty);
        return clean + RulesMarker + (char)(RulesValueBase + IndexOfPercent(rules.UnitCostPercent)) +
            (char)(RulesValueBase + IndexOfPercent(rules.BuildingCostPercent)) +
            (char)(RulesValueBase + IndexOfPercent(rules.EnemyHealthPercent));
    }

    private static string StripEmbeddedRules(string value)
    {
        int marker = FindEmbeddedRulesMarker(value);
        return marker < 0 ? value : value[..marker];
    }

    private static bool TryReadEmbeddedRules(string? gameName, out RuleSet rules)
    {
        rules = RuleSet.Default;
        if (string.IsNullOrEmpty(gameName)) return false;
        int marker = FindEmbeddedRulesMarker(gameName);
        if (marker < 0) return false;
        int unit = gameName[marker + 1] - RulesValueBase;
        int building = gameName[marker + 2] - RulesValueBase;
        int health = gameName[marker + 3] - RulesValueBase;
        if (unit < 0 || unit >= Percentages.Length || building < 0 || building >= Percentages.Length ||
            health < 0 || health >= Percentages.Length)
            return false;
        rules = new RuleSet(Percentages[unit], Percentages[building], Percentages[health]);
        return true;
    }

    private static int FindEmbeddedRulesMarker(string value)
    {
        int marker = value.LastIndexOf(RulesMarker);
        while (marker >= 0)
        {
            if (marker + 3 < value.Length)
            {
                bool valid = true;
                for (int offset = 1; offset <= 3; offset++)
                {
                    int index = value[marker + offset] - RulesValueBase;
                    if (index >= 0 && index < Percentages.Length) continue;
                    valid = false;
                    break;
                }
                if (valid) return marker;
            }
            if (marker == 0) break;
            marker = value.LastIndexOf(RulesMarker, marker - 1);
        }
        return -1;
    }

    private static int IndexOfPercent(int percent)
    {
        int index = Array.IndexOf(Percentages, percent);
        return index < 0 ? DefaultIndex : index;
    }

    private static void SaveRules(string gameId, RuleSet rules)
    {
        PlayerPrefs.SetString(GameRulesKeyPrefix + gameId, rules.Serialize());
        PlayerPrefs.Save();
        activeRules = rules;
    }

    private static bool IsRulesOwnerTurn(GameState state) =>
        TryGetRulesOwner(state, out byte rulesOwner) && state.CurrentPlayer == rulesOwner;

    private static bool TryGetRulesOwner(GameState state, out byte playerId)
    {
        if (hasActiveRulesOwner)
        {
            playerId = activeRulesOwner;
            return true;
        }
        return RefreshRulesOwner(state, out playerId);
    }

    private static void RefreshRulesOwner(GameState? state)
    {
        hasActiveRulesOwner = state != null && RefreshRulesOwner(state, out activeRulesOwner);
    }

    private static bool RefreshRulesOwner(GameState state, out byte playerId)
    {
        playerId = PlayerState.NO_PLAYER_ID;
        if (state.PlayerStates == null) return false;
        foreach (PlayerState player in state.PlayerStates)
        {
            if (player == null || player.AutoPlay) continue;
            playerId = player.Id;
            activeRulesOwner = playerId;
            hasActiveRulesOwner = true;
            return true;
        }
        hasActiveRulesOwner = false;
        return false;
    }

    internal static int Scale(int value, int percent)
    {
        if (value <= 0 || percent == 100) return value;
        long scaled = ((long)value * percent + 99) / 100;
        return (int)Math.Clamp(scaled, 1L, int.MaxValue);
    }

    internal static ConversionHealthSnapshot CaptureConversionHealth(ConvertAction? action, GameState? state)
    {
        UnitState? unit = UnitAt(action?.Target, state);
        return unit == null || state == null
            ? default
            : new ConversionHealthSnapshot(unit.health, UnitDataExtensions.GetMaxHealth(unit, state));
    }

    internal static void RestoreConvertedHealth(ConvertAction? action, GameState? state, ConversionHealthSnapshot snapshot)
    {
        if (!snapshot.IsValid || state == null) return;
        UnitState? unit = UnitAt(action?.Target, state);
        if (unit == null) return;
        int newMaximum = UnitDataExtensions.GetMaxHealth(unit, state);
        if (newMaximum == snapshot.Maximum) return;
        long proportional = ((long)snapshot.Health * newMaximum + snapshot.Maximum - 1) / snapshot.Maximum;
        unit.health = (ushort)Math.Clamp(proportional, 1L, Math.Min(newMaximum, ushort.MaxValue));
    }

    private static UnitState? UnitAt(WorldCoordinates? coordinates, GameState? state)
    {
        if (!coordinates.HasValue || state?.Map?.Tiles == null) return null;
        WorldCoordinates target = coordinates.Value;
        if (!target.IsValid(state.Map.Width, state.Map.Height)) return null;
        return state.Map.Tiles[target.ToIndex(state.Map.Width)]?.unit;
    }

    internal readonly record struct ConversionHealthSnapshot(int Health, int Maximum)
    {
        internal bool IsValid => Health > 0 && Maximum > 0;
    }

    internal sealed class UnitCostScope
    {
        private readonly List<(UnitData Data, int Cost)> entries = new();
        private bool restored;
        internal bool OwnsScope { private get; set; }
        internal void Add(UnitData data, int cost) => entries.Add((data, cost));
        internal void Restore()
        {
            if (restored) return;
            restored = true;
            foreach ((UnitData data, int cost) in entries)
            {
                try { if (data != null && data.Pointer != IntPtr.Zero) data.cost = cost; }
                catch { }
            }
            entries.Clear();
            if (OwnsScope) unitCostScopeDepth = 0;
        }
    }

    internal sealed class BuildingCostScope
    {
        private readonly List<(ImprovementData Data, int Cost)> entries = new();
        private bool restored;
        internal bool OwnsScope { private get; set; }
        internal void Add(ImprovementData data, int cost) => entries.Add((data, cost));
        internal void Restore()
        {
            if (restored) return;
            restored = true;
            foreach ((ImprovementData data, int cost) in entries)
            {
                try { if (data != null && data.Pointer != IntPtr.Zero) data.cost = cost; }
                catch { }
            }
            entries.Clear();
            if (OwnsScope) buildingCostScopeDepth = 0;
        }
    }

    private static int UnitIndex() => ReadIndex(UnitSelectionKey);
    private static int BuildingIndex() => ReadIndex(BuildingSelectionKey);
    private static int EnemyHealthIndex() => ReadIndex(EnemyHealthSelectionKey);
    private static int ReadIndex(string key)
    {
        int value = PlayerPrefs.GetInt(key, DefaultIndex);
        return value >= 0 && value < Percentages.Length ? value : DefaultIndex;
    }

    private static void SetUnitIndex(int index) => SaveIndex(UnitSelectionKey, index);
    private static void SetBuildingIndex(int index) => SaveIndex(BuildingSelectionKey, index);
    private static void SetEnemyHealthIndex(int index) => SaveIndex(EnemyHealthSelectionKey, index);
    private static void SaveIndex(string key, int index)
    {
        if (index < 0 || index >= Percentages.Length) return;
        PlayerPrefs.SetInt(key, index);
        PlayerPrefs.Save();
    }

    private sealed class Controls
    {
        internal readonly UIHorizontalList_UI2 UnitCost;
        internal readonly TextField_UI2 UnitDescription;
        internal readonly UIHorizontalList_UI2 BuildingCost;
        internal readonly TextField_UI2 BuildingDescription;
        internal readonly UIHorizontalList_UI2 EnemyHealth;
        internal readonly TextField_UI2 HealthDescription;

        internal Controls(
            UIHorizontalList_UI2 unitCost,
            TextField_UI2 unitDescription,
            UIHorizontalList_UI2 buildingCost,
            TextField_UI2 buildingDescription,
            UIHorizontalList_UI2 enemyHealth,
            TextField_UI2 healthDescription
        )
        {
            UnitCost = unitCost;
            UnitDescription = unitDescription;
            BuildingCost = buildingCost;
            BuildingDescription = buildingDescription;
            EnemyHealth = enemyHealth;
            HealthDescription = healthDescription;
        }

        internal bool IsAlive
        {
            get
            {
                try
                {
                    return UnitCost != null && UnitCost.Pointer != IntPtr.Zero &&
                        UnitCost.gameObject != null && UnitCost.gameObject.Pointer != IntPtr.Zero;
                }
                catch
                {
                    return false;
                }
            }
        }
        internal IEnumerable<UIBasicComponent> Components
        {
            get
            {
                yield return UnitCost;
                yield return UnitDescription;
                yield return BuildingCost;
                yield return BuildingDescription;
                yield return EnemyHealth;
                yield return HealthDescription;
            }
        }
    }

    private readonly record struct RuleSet(int UnitCostPercent, int BuildingCostPercent, int EnemyHealthPercent)
    {
        internal static readonly RuleSet Default = new(100, 100, 100);
        internal string Serialize() => $"{UnitCostPercent},{BuildingCostPercent},{EnemyHealthPercent}";
        internal static bool TryParse(string value, out RuleSet rules)
        {
            rules = Default;
            string[] parts = value.Split(',');
            if (parts.Length != 3 || !int.TryParse(parts[0], out int unit) ||
                !int.TryParse(parts[1], out int building) || !int.TryParse(parts[2], out int health) ||
                !Percentages.Contains(unit) || !Percentages.Contains(building) || !Percentages.Contains(health))
                return false;
            rules = new RuleSet(unit, building, health);
            return true;
        }
    }
}

[HarmonyPatch(typeof(GameSetupScreen_UI2), "OnShow")]
internal static class AdvancedSettingsOnShowPatch
{
    [HarmonyPostfix]
    private static void AddRows(GameSetupScreen_UI2 __instance)
    {
        if (AdvancedMatchSettings.EnsureControls(__instance)) __instance.UpdateLayout();
    }
}

[HarmonyPatch(typeof(GameSetupScreen_UI2), "RunLayout")]
internal static class AdvancedSettingsLayoutPatch
{
    [HarmonyPrefix]
    private static void AddRowsBeforeLayout(GameSetupScreen_UI2 __instance) => AdvancedMatchSettings.EnsureControls(__instance);

    [HarmonyPostfix]
    private static void RestoreRowsAfterLayout(GameSetupScreen_UI2 __instance) =>
        AdvancedMatchSettings.RefreshVisibilityAfterLayout(__instance);
}

[HarmonyPatch(typeof(GameSetupScreen_UI2), "OnAdvancedSettingsToggleClicked")]
internal static class AdvancedSettingsTogglePatch
{
    [HarmonyPrefix]
    private static void CaptureState(GameSetupScreen_UI2 __instance, out bool __state) =>
        __state = __instance.advancedSettingsExpanded;

    [HarmonyPostfix]
    private static void RefreshRows(GameSetupScreen_UI2 __instance, bool __state) =>
        AdvancedMatchSettings.RefreshAfterToggle(__instance, __state);
}

[HarmonyPatch(typeof(GameSetupScreen_UI2), "OnContinueClicked_StartMultiplayerGame")]
internal static class AdvancedSettingsMultiplayerStartPatch
{
    [HarmonyPrefix]
    private static void ArmRules() => AdvancedMatchSettings.ArmNextGame();
}

[HarmonyPatch(typeof(GameSetupScreen_UI2), "OnContinueClicked_StartSingleplayerGame")]
internal static class AdvancedSettingsSingleplayerStartPatch
{
    [HarmonyPrefix]
    private static void ArmRules() => AdvancedMatchSettings.ArmNextGame();
}

[HarmonyPatch(typeof(GameSetupScreen_UI2), "OnContinueClicked_FindRandomMultiplayerGame")]
internal static class AdvancedSettingsMatchmakingStartPatch
{
    [HarmonyPrefix]
    private static void ArmRules() => AdvancedMatchSettings.ArmNextGame();
}

[HarmonyPatch(typeof(ClientBase), nameof(ClientBase.CreateSession), new[] { typeof(GameSettings), typeof(Il2CppSystem.Guid) })]
internal static class AdvancedSettingsCreateSessionPatch
{
    [HarmonyPrefix]
    private static void AttachRules(Il2CppSystem.Guid gameId) => AdvancedMatchSettings.CapturePendingRules(gameId);
}

[HarmonyPatch(typeof(ClientBase), nameof(ClientBase.OpenSession))]
internal static class AdvancedSettingsOpenSessionPatch
{
    [HarmonyPrefix]
    private static void AttachRules(Il2CppSystem.Guid gameId, GameType gameType)
    {
        if (gameType is GameType.Multiplayer or GameType.Matchmaking or GameType.Competitive)
            AdvancedMatchSettings.CapturePendingRules(gameId);
    }
}

[HarmonyPatch(typeof(GameManager), "OnGameReady")]
internal static class AdvancedSettingsGameReadyPatch
{
    [HarmonyPostfix]
    private static void AttachDeferredRules() => AdvancedMatchSettings.CapturePendingRulesFromCurrentGame();
}

[HarmonyPatch(typeof(InteractionBar), "AddTrainUnitButtons")]
internal static class AdvancedUnitCostUiPatch
{
    [HarmonyPrefix]
    private static void Apply(out AdvancedMatchSettings.UnitCostScope __state) =>
        __state = AdvancedMatchSettings.BeginUnitCostScope(GameManager.GameState);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.UnitCostScope __state)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(TrainCommand), nameof(TrainCommand.IsValid))]
internal static class AdvancedUnitCostValidationPatch
{
    [HarmonyPrefix]
    private static void Apply(TrainCommand __instance, GameState state, out AdvancedMatchSettings.UnitCostScope __state) =>
        __state = AdvancedMatchSettings.BeginUnitCostScope(state, __instance.Type);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.UnitCostScope __state)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(TrainCommand), nameof(TrainCommand.Execute))]
internal static class AdvancedUnitCostExecutionPatch
{
    [HarmonyPrefix]
    private static void Apply(TrainCommand __instance, GameState state, out AdvancedMatchSettings.UnitCostScope __state) =>
        __state = AdvancedMatchSettings.BeginUnitCostScope(state, __instance.Type);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.UnitCostScope __state)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(InteractionBar), "RefreshBuildingOptions")]
internal static class AdvancedBuildingCostUiPatch
{
    [HarmonyPrefix]
    private static void Apply(out AdvancedMatchSettings.BuildingCostScope __state) =>
        __state = AdvancedMatchSettings.BeginBuildingCostScope(GameManager.GameState);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.BuildingCostScope __state)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(BuildCommand), nameof(BuildCommand.IsValid))]
internal static class AdvancedBuildingCostValidationPatch
{
    [HarmonyPrefix]
    private static void Apply(BuildCommand __instance, GameState state, out AdvancedMatchSettings.BuildingCostScope __state) =>
        __state = AdvancedMatchSettings.BeginBuildingCostScope(state, __instance.Type);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.BuildingCostScope __state)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(BuildCommand), nameof(BuildCommand.Execute))]
internal static class AdvancedBuildingCostExecutionPatch
{
    [HarmonyPrefix]
    private static void Apply(BuildCommand __instance, GameState state, out AdvancedMatchSettings.BuildingCostScope __state) =>
        __state = AdvancedMatchSettings.BeginBuildingCostScope(state, __instance.Type);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.BuildingCostScope __state)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(UnitDataExtensions), nameof(UnitDataExtensions.GetMaxHealth))]
internal static class AdvancedEnemyHealthPatch
{
    [HarmonyPostfix]
    private static void ScaleHealth(UnitState unitState, GameState gameState, ref int __result) =>
        __result = AdvancedMatchSettings.ScaleEnemyMaxHealth(__result, unitState, gameState);
}

[HarmonyPatch(
    typeof(UnitState),
    nameof(UnitState.Create),
    new[]
    {
        typeof(GameState), typeof(byte), typeof(uint), typeof(UnitData), typeof(WorldCoordinates), typeof(WorldCoordinates)
    }
)]
internal static class AdvancedEnemySpawnHealthPatch
{
    [HarmonyPostfix]
    private static void FillScaledHealth(GameState gameState, UnitState __result) =>
        AdvancedMatchSettings.SetSpawnedUnitHealth(__result, gameState);
}

[HarmonyPatch(typeof(ConvertAction), nameof(ConvertAction.Execute))]
internal static class AdvancedConvertedUnitHealthPatch
{
    [HarmonyPrefix]
    private static void RememberHealth(
        ConvertAction __instance,
        GameState state,
        out AdvancedMatchSettings.ConversionHealthSnapshot __state
    ) => __state = AdvancedMatchSettings.CaptureConversionHealth(__instance, state);

    [HarmonyPostfix]
    private static void ApplyNewMaximum(
        ConvertAction __instance,
        GameState state,
        AdvancedMatchSettings.ConversionHealthSnapshot __state
    ) => AdvancedMatchSettings.RestoreConvertedHealth(__instance, state, __state);
}
