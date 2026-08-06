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
    private static readonly Dictionary<IntPtr, Controls> ControlsByHolder = new();
    private static ManualLogSource logger = null!;
    private static RuleSet pendingRules = RuleSet.Default;
    private static RuleSet activeRules = RuleSet.Default;
    private static byte activeRulesOwner = byte.MaxValue;
    private static bool hasActiveRulesOwner;
    [ThreadStatic] private static int unitCostScopeDepth;
    [ThreadStatic] private static int buildingCostScopeDepth;
    private static int discardedControlSerial;
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
            Controls? existing = ControlsFor(view);
            if (existing != null) SetVisible(existing, false);
            return false;
        }

        if (view.holder == null) return false;

        try
        {
            IntPtr holderKey = view.holder.Pointer;
            PruneOtherControlHolders(holderKey);
            ControlsByHolder.TryGetValue(holderKey, out Controls? controls);
            if (controls != null && (!controls.IsAlive || !controls.IsUnder(view.holder)))
            {
                ControlsByHolder.Remove(holderKey);
                controls = null;
            }

            if (controls == null)
            {
                controls = FindExistingControls(view.holder);
                if (controls == null)
                {
                    DiscardPartialOrDuplicateControls(view);
                    controls = CreateControls(view.holder);
                    logger.LogInfo("Created one clean set of three advanced match setting rows.");
                }
                ControlsByHolder[holderKey] = controls;
            }

            controls.Expanded = screen.advancedSettingsExpanded;
            controls.ShowMapType = HasListData(screen.mapTypeData);
            controls.ShowMapSize = HasListData(screen.mapSizeData);
            ApplyNativeSetupRows(view, controls);
            NormalizeComponentOrder(view, controls);
            RefreshControls(controls);
            SetVisible(controls, controls.Expanded);
            view.SetShowAdvancedSettingsToggleButton(
                screen.advancedSettingsExpanded ? "Hide Advanced Settings" : "Show Advanced Settings"
            );
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                if (view.holder != null) ControlsByHolder.Remove(view.holder.Pointer);
            }
            catch { }
            logger.LogWarning($"Could not render advanced match settings yet: {exception.Message}");
            return false;
        }
    }

    internal static void PrepareViewLayout(GameSetupScreenView view)
    {
        Controls? controls = ControlsFor(view);
        if (controls == null || !IsSupportedSetup()) return;
        ApplyNativeSetupRows(view, controls);
        NormalizeComponentOrder(view, controls);
        SetVisible(controls, controls.Expanded);
        view.SetShowAdvancedSettingsToggleButton(
            controls.Expanded ? "Hide Advanced Settings" : "Show Advanced Settings"
        );
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

    private static UIHorizontalList_UI2 CreateList(
        RectTransform holder,
        string name,
        string header,
        int selectedIndex,
        Action<int> onSelected
    )
    {
        UIHorizontalList_UI2 list = UILibrary.NewHorizontalList(holder);
        list.gameObject.name = name;
        if (!list.Initialized) list.Init();
        SignalPayload<int> signal = new();
        signal.Add(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>(onSelected));
        list.onItemSelected = signal;
        list.onItemHighlighted = new SignalPayload<int>();
        list.onDisabledItemClicked = new Signal();
        list.SetData(header, PercentageLabels(), selectedIndex);
        list.UpdateLayout();
        return list;
    }

    private static TextField_UI2 CreateDescription(
        RectTransform holder,
        string name,
        string text
    )
    {
        TextField_UI2 description = UILibrary.NewText(holder, text);
        description.gameObject.name = name;
        description.SetText(text);
        description.UpdateSize();
        return description;
    }

    private static Controls CreateControls(RectTransform holder) => new(
        CreateList(holder, UnitListName, "Unit cost for you", UnitIndex(), SetUnitIndex),
        CreateDescription(
            holder,
            UnitDescriptionName,
            "Multiplies how much your units cost, rounded up. Only your units are affected; bots pay normal prices."
        ),
        CreateList(holder, BuildingListName, "Your building cost", BuildingIndex(), SetBuildingIndex),
        CreateDescription(
            holder,
            BuildingDescriptionName,
            "Multiplies every tile-interaction cost, rounded up, including roads and special-tribe buildings."
        ),
        CreateList(holder, HealthListName, "Enemy unit health", EnemyHealthIndex(), SetEnemyHealthIndex),
        CreateDescription(
            holder,
            HealthDescriptionName,
            "Changes the maximum health of every opposing unit by this percentage."
        )
    );

    private static void ApplyNativeSetupRows(GameSetupScreenView view, Controls controls)
    {
        // The toggle belongs to Better BoP's rows. Native map rows stay outside
        // it and are restored only when vanilla supplied data for this setup.
        view.whatToShow |= GameSetupScreenView.Show.AdvancedSettingsToggle;
        if (view.advancedSettingsToggle != null) view.advancedSettingsToggle.ActiveSelf = true;

        if (controls.ShowMapType)
            view.whatToShow |= GameSetupScreenView.Show.MapTypeList;
        else
            view.whatToShow &= ~GameSetupScreenView.Show.MapTypeList;
        if (view.listMapType != null) view.listMapType.ActiveSelf = controls.ShowMapType;

        if (controls.ShowMapSize)
            view.whatToShow |= GameSetupScreenView.Show.MapSizeList;
        else
            view.whatToShow &= ~GameSetupScreenView.Show.MapSizeList;
        if (view.listMapSize != null) view.listMapSize.ActiveSelf = controls.ShowMapSize;
    }

    private static bool HasListData(UIHorizontalListData? data) =>
        data?.labels != null && data.labels.Count > 0;

    private static Controls? ControlsFor(GameSetupScreenView? view)
    {
        if (view?.holder == null) return null;
        IntPtr key = view.holder.Pointer;
        if (!ControlsByHolder.TryGetValue(key, out Controls? controls)) return null;
        if (controls.IsAlive && controls.IsUnder(view.holder)) return controls;
        ControlsByHolder.Remove(key);
        return null;
    }

    private static void PruneOtherControlHolders(IntPtr currentHolder)
    {
        foreach ((IntPtr holder, Controls controls) in ControlsByHolder.ToArray())
        {
            if (holder == currentHolder) continue;
            try { SetVisible(controls, false); }
            catch { }
            ControlsByHolder.Remove(holder);
        }
    }

    private static Controls? FindExistingControls(RectTransform holder)
    {
        UIHorizontalList_UI2? unit = FindOnlyList(holder, UnitListName, out int unitCount);
        UIHorizontalList_UI2? building = FindOnlyList(holder, BuildingListName, out int buildingCount);
        UIHorizontalList_UI2? health = FindOnlyList(holder, HealthListName, out int healthCount);
        TextField_UI2? unitDescription = FindOnlyText(holder, UnitDescriptionName, out int unitDescriptionCount);
        TextField_UI2? buildingDescription = FindOnlyText(holder, BuildingDescriptionName, out int buildingDescriptionCount);
        TextField_UI2? healthDescription = FindOnlyText(holder, HealthDescriptionName, out int healthDescriptionCount);

        int total = unitCount + buildingCount + healthCount + unitDescriptionCount +
            buildingDescriptionCount + healthDescriptionCount;
        if (total == 0) return null;
        if (unitCount == 1 && buildingCount == 1 && healthCount == 1 &&
            unitDescriptionCount == 1 && buildingDescriptionCount == 1 && healthDescriptionCount == 1)
        {
            logger.LogInfo("Recovered the existing advanced setting rows after a setup-view refresh.");
            return new Controls(unit!, unitDescription!, building!, buildingDescription!, health!, healthDescription!);
        }

        logger.LogWarning($"Found an incomplete or duplicated advanced UI set ({total} named controls); rebuilding it once.");
        return null;
    }

    private static UIHorizontalList_UI2? FindOnlyList(RectTransform holder, string name, out int count)
    {
        UIHorizontalList_UI2? result = null;
        count = 0;
        foreach (UIHorizontalList_UI2 candidate in holder.GetComponentsInChildren<UIHorizontalList_UI2>(true))
        {
            if (candidate?.gameObject == null || candidate.gameObject.name != name) continue;
            result = candidate;
            count++;
        }
        return count == 1 ? result : null;
    }

    private static TextField_UI2? FindOnlyText(RectTransform holder, string name, out int count)
    {
        TextField_UI2? result = null;
        count = 0;
        foreach (TextField_UI2 candidate in holder.GetComponentsInChildren<TextField_UI2>(true))
        {
            if (candidate?.gameObject == null || candidate.gameObject.name != name) continue;
            result = candidate;
            count++;
        }
        return count == 1 ? result : null;
    }

    private static bool IsCustomName(string? name) => name is
        UnitListName or UnitDescriptionName or BuildingListName or BuildingDescriptionName or
        HealthListName or HealthDescriptionName;

    private static void DiscardPartialOrDuplicateControls(GameSetupScreenView view)
    {
        List<UIBasicComponent> discarded = new();
        foreach (UIHorizontalList_UI2 candidate in view.holder.GetComponentsInChildren<UIHorizontalList_UI2>(true))
            if (candidate?.gameObject != null && IsCustomName(candidate.gameObject.name)) discarded.Add(candidate);
        foreach (TextField_UI2 candidate in view.holder.GetComponentsInChildren<TextField_UI2>(true))
            if (candidate?.gameObject != null && IsCustomName(candidate.gameObject.name)) discarded.Add(candidate);
        if (discarded.Count == 0) return;

        HashSet<IntPtr> layoutPointers = discarded
            .Select(AsLayoutable)
            .Where(layout => layout != null)
            .Select(layout => layout!.Pointer)
            .ToHashSet();
        if (view.allComponents != null)
        {
            for (int index = view.allComponents.Count - 1; index >= 0; index--)
                if (view.allComponents[index] != null && layoutPointers.Contains(view.allComponents[index].Pointer))
                    view.allComponents.RemoveAt(index);
        }

        HashSet<IntPtr> listPointers = discarded.OfType<UIHorizontalList_UI2>().Select(list => list.Pointer).ToHashSet();
        if (view.allLists != null)
        {
            for (int index = view.allLists.Count - 1; index >= 0; index--)
                if (view.allLists[index] != null && listPointers.Contains(view.allLists[index].Pointer))
                    view.allLists.RemoveAt(index);
        }

        HashSet<IntPtr> destroyedObjects = new();
        foreach (UIBasicComponent component in discarded)
        {
            try
            {
                GameObject gameObject = component.gameObject;
                if (gameObject == null || !destroyedObjects.Add(gameObject.Pointer)) continue;
                component.ActiveSelf = false;
                gameObject.name = $"BetterBoP.Discarded.{++discardedControlSerial}";
                gameObject.SetActive(false);
                UnityEngine.Object.Destroy(gameObject);
            }
            catch { }
        }
    }

    private static void NormalizeComponentOrder(GameSetupScreenView view, Controls controls)
    {
        if (view.allComponents == null || view.advancedSettingsToggle == null) return;

        IUILayoutable? toggleLayout = AsLayoutable(view.advancedSettingsToggle);
        List<IUILayoutable> ordered = controls.Components
            .Select(AsLayoutable)
            .Where(layout => layout != null)
            .Select(layout => layout!)
            .ToList();
        if (toggleLayout == null || ordered.Count != 6) return;

        HashSet<IntPtr> managedPointers = ordered.Select(layout => layout.Pointer).ToHashSet();
        managedPointers.Add(toggleLayout.Pointer);
        for (int index = view.allComponents.Count - 1; index >= 0; index--)
        {
            IUILayoutable? entry = view.allComponents[index];
            if (entry != null && managedPointers.Contains(entry.Pointer)) view.allComponents.RemoveAt(index);
        }

        int anchor = Math.Max(
            FindComponentIndex(view, view.listMapType),
            Math.Max(
                FindComponentIndex(view, view.listMapSize),
                FindComponentIndex(view, view.mapSizeDescriptionText)
            )
        );
        int insertAt = Math.Clamp(anchor + 1, 0, view.allComponents.Count);
        view.allComponents.Insert(insertAt++, toggleLayout);
        foreach (IUILayoutable component in ordered) view.allComponents.Insert(insertAt++, component);

        NormalizeAllLists(view, controls);
    }

    private static void NormalizeAllLists(GameSetupScreenView view, Controls controls)
    {
        if (view.allLists == null) return;
        HashSet<IntPtr> pointers = controls.Lists.Select(list => list.Pointer).ToHashSet();
        for (int index = view.allLists.Count - 1; index >= 0; index--)
        {
            UIHorizontalList_UI2? list = view.allLists[index];
            if (list != null && pointers.Contains(list.Pointer)) view.allLists.RemoveAt(index);
        }
        foreach (UIHorizontalList_UI2 list in controls.Lists) view.allLists.Add(list);
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
        IUILayoutable? componentLayout = AsLayoutable(component);
        return componentLayout != null && layout.Pointer == componentLayout.Pointer;
    }

    private static IUILayoutable? AsLayoutable(UIBasicComponent? component)
    {
        if (component == null) return null;
        try { return component.TryCast<IUILayoutable>(); }
        catch { return null; }
    }

    private static void SetVisible(Controls controls, bool visible)
    {
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
        internal bool Expanded;
        internal bool ShowMapType;
        internal bool ShowMapSize;

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
                    return Components.All(component =>
                        component != null && component.Pointer != IntPtr.Zero &&
                        component.gameObject != null && component.gameObject.Pointer != IntPtr.Zero);
                }
                catch
                {
                    return false;
                }
            }
        }
        internal bool IsUnder(RectTransform holder)
        {
            try
            {
                return holder != null && Components.All(component =>
                    component.rectTransform?.parent != null &&
                    component.rectTransform.parent.Pointer == holder.Pointer);
            }
            catch
            {
                return false;
            }
        }
        internal IEnumerable<UIHorizontalList_UI2> Lists
        {
            get
            {
                yield return UnitCost;
                yield return BuildingCost;
                yield return EnemyHealth;
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
}

[HarmonyPatch(typeof(GameSetupScreenView), nameof(GameSetupScreenView.RunLayout))]
internal static class AdvancedSettingsViewLayoutPatch
{
    [HarmonyPrefix]
    private static void PrepareRowsForNativeLayout(GameSetupScreenView __instance) =>
        AdvancedMatchSettings.PrepareViewLayout(__instance);
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
