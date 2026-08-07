using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Polytopia.Data;
using PolytopiaBackendBase.Game;
using UnityEngine;

namespace BetterBoPMod;

/// <summary>
/// Lightweight, per-game handicap settings for modded multiplayer setup.
/// The UI uses one mod-owned advanced-settings toggle and native option rows;
/// gameplay is handled at the shared data accessors instead of enumerating
/// every train/build command separately.
/// </summary>
internal static class AdvancedMatchSettings
{
    private const string UnitSelectionKey = "betterbop.advanced.unit-cost.v1";
    private const string BuildingSelectionKey = "betterbop.advanced.building-cost.v1";
    private const string EnemyHealthSelectionKey = "betterbop.advanced.enemy-health.v1";
    private const string GameRulesKeyPrefix = "betterbop.advanced.game.v1.";
    private const string ToggleName = "BetterBoP.AdvancedSettingsToggle";
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
    private static readonly UnitData.Type[] UnitTypes = Enum.GetValues<UnitData.Type>();
    private static readonly ImprovementData.Type[] ImprovementTypes = Enum.GetValues<ImprovementData.Type>();
    private static readonly Dictionary<IntPtr, Controls> ControlsByParent = new();
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
        PlayerPrefs.Save();
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
            if (existing != null) SetAllVisible(existing, false);
            return false;
        }

        RectTransform? parent = ControlParent(view);
        if (parent == null) return false;

        try
        {
            IntPtr parentKey = parent.Pointer;
            PruneOtherControlParents(parentKey);
            ControlsByParent.TryGetValue(parentKey, out Controls? controls);
            if (controls != null && (!controls.IsAlive || !controls.IsUnder(parent)))
            {
                ControlsByParent.Remove(parentKey);
                controls = null;
            }

            if (controls == null)
            {
                controls = FindExistingControls(parent);
                if (controls == null)
                {
                    DiscardPartialOrDuplicateControls(view, parent);
                    controls = CreateControls(parent);
                    logger.LogInfo("Created one clean set of three advanced match setting rows.");
                }
                ControlsByParent[parentKey] = controls;
            }

            BindToggle(screen, controls);
            controls.Expanded = screen.advancedSettingsExpanded;
            controls.ShowMapType = HasListData(screen.mapTypeData);
            controls.ShowMapSize = HasListData(screen.mapSizeData);
            RefreshControls(controls);
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                if (parent != null) ControlsByParent.Remove(parent.Pointer);
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
        SetToggleState(controls);
        SetRowsVisible(controls, controls.Expanded);
    }

    internal static void FinalizeViewLayout(GameSetupScreenView view)
    {
        Controls? controls = ControlsFor(view);
        if (controls == null || !IsSupportedSetup() || view.continueButton == null) return;

        try
        {
            foreach (UIHorizontalList_UI2 list in controls.Lists) list.UpdateLayout();
            foreach (TextField_UI2 description in controls.Descriptions) description.UpdateSize();

            // Continue is the final stock row and has already been positioned by
            // Polytopia. Insert the custom block exactly at that native boundary.
            float cursorTop = view.continueButton.GetTop();
            controls.Toggle.SetPositionTopY(controls.Toggle.GetX(), cursorTop);
            cursorTop = controls.Toggle.GetBottom();

            if (controls.Expanded)
            {
                foreach (UIBasicComponent row in controls.Rows)
                {
                    row.SetPositionTopY(row.GetX(), cursorTop);
                    cursorTop = row.GetBottom();
                }
            }

            view.continueButton.SetPositionTopY(view.continueButton.GetX(), cursorTop);
            view.scroller?.UpdateContentBounds();
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Could not finalize advanced setting row positions: {exception.Message}");
        }
    }

    private static void BindToggle(GameSetupScreen_UI2 screen, Controls controls)
    {
        if (controls.ToggleAction != null && controls.BoundScreen == screen.Pointer) return;

        controls.Toggle.ClearCallbacks();
        controls.Toggle.ButtonEnabled = true;
        controls.Toggle.buttonEnabled = true;
        controls.Toggle.blockClick = false;
        controls.Toggle.eatClickAction = false;
        controls.ToggleAction = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => ToggleAdvanced(screen));
        controls.BoundScreen = screen.Pointer;
        controls.Toggle.OnClickedSignal.Add(controls.ToggleAction);
    }

    private static void ToggleAdvanced(GameSetupScreen_UI2 screen)
    {
        if (screen == null || !IsSupportedSetup()) return;
        Controls? controls = ControlsFor(screen.view);
        if (controls == null)
        {
            if (!EnsureControls(screen)) return;
            controls = ControlsFor(screen.view);
            if (controls == null) return;
        }

        controls.Expanded = !controls.Expanded;
        screen.advancedSettingsExpanded = controls.Expanded;
        SetToggleState(controls);
        SetRowsVisible(controls, controls.Expanded);
        logger.LogInfo($"Advanced match settings {(controls.Expanded ? "expanded" : "collapsed")}.");
        screen.UpdateLayout();
    }

    private static void SetToggleState(Controls controls)
    {
        controls.Toggle.Text = controls.Expanded ? "Hide Advanced Settings" : "Show Advanced Settings";
        controls.Toggle.ButtonEnabled = true;
        controls.Toggle.buttonEnabled = true;
        controls.Toggle.blockClick = false;
        controls.Toggle.eatClickAction = false;
        controls.Toggle.ActiveSelf = true;
        controls.Toggle.RunLayout();
    }

    internal static UnitCostScope? BeginUnitCostScope(GameState? state, UnitData.Type? only = null)
    {
        if (activeRules.UnitCostPercent == 100 || state?.GameLogicData == null || !IsRulesOwnerTurn(state))
            return null;
        if (unitCostScopeDepth != 0) return null;
        unitCostScopeDepth = 1;
        UnitCostScope scope = new() { OwnsScope = true };

        HashSet<IntPtr>? seen = only.HasValue ? null : new();
        ReadOnlySpan<UnitData.Type> types = only.HasValue
            ? stackalloc UnitData.Type[] { only.Value }
            : UnitTypes;
        foreach (UnitData.Type type in types)
        {
            try
            {
                UnitData? data = state.GameLogicData.GetUnitData(type);
                if (data == null || data.Pointer == IntPtr.Zero || (seen != null && !seen.Add(data.Pointer))) continue;
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

    internal static BuildingCostScope? BeginBuildingCostScope(GameState? state, ImprovementData.Type? only = null)
    {
        if (activeRules.BuildingCostPercent == 100 || state?.GameLogicData == null || !IsRulesOwnerTurn(state))
            return null;
        if (buildingCostScopeDepth != 0) return null;
        buildingCostScopeDepth = 1;
        BuildingCostScope scope = new() { OwnsScope = true };

        HashSet<IntPtr>? seen = only.HasValue ? null : new();
        ReadOnlySpan<ImprovementData.Type> types = only.HasValue
            ? stackalloc ImprovementData.Type[] { only.Value }
            : ImprovementTypes;
        foreach (ImprovementData.Type type in types)
        {
            try
            {
                ImprovementData? data = state.GameLogicData.GetImprovementData(type);
                if (data == null || data.Pointer == IntPtr.Zero || (seen != null && !seen.Add(data.Pointer))) continue;
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
        if (activeRules.EnemyHealthPercent == 100 || unit == null || state == null ||
            !TryGetRulesOwner(state, out byte rulesOwner) || unit.owner == rulesOwner ||
            unit.owner == PlayerState.NO_PLAYER_ID || unit.owner == PlayerState.NATURE_PLAYER_ID)
            return value;
        return Scale(value, activeRules.EnemyHealthPercent);
    }

    internal static void SetSpawnedUnitHealth(UnitState? unit, GameState? state)
    {
        if (activeRules.EnemyHealthPercent == 100 || unit == null || state == null ||
            !TryGetRulesOwner(state, out byte rulesOwner) || unit.owner == rulesOwner ||
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
        if (list.scroller != null) list.scroller.routeToParent = true;

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

    private static UILabelButton_UI2 CreateToggle(RectTransform holder)
    {
        UILabelButton_UI2 toggle = UILibrary.NewLabelButton(holder);
        toggle.gameObject.name = ToggleName;
        if (!toggle.Initialized) toggle.Init();
        toggle.ButtonEnabled = true;
        toggle.buttonEnabled = true;
        toggle.blockClick = false;
        toggle.eatClickAction = false;
        toggle.Text = "Show Advanced Settings";
        return toggle;
    }

    private static Controls CreateControls(RectTransform holder) => new(
        CreateToggle(holder),
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
        // The native toggle has a fixed pre-map slot and is not wired in every
        // setup variant. Better BoP owns a single replacement below both maps.
        view.whatToShow &= ~GameSetupScreenView.Show.AdvancedSettingsToggle;
        if (view.advancedSettingsToggle != null) view.advancedSettingsToggle.ActiveSelf = false;

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

    private static RectTransform? ControlParent(GameSetupScreenView? view) => view?.scroller?.content;

    internal static void CommitHighlighted(UIHorizontalList_UI2? list)
    {
        if (list?.gameObject == null) return;
        int index = list.HighlightedIndex;
        if (index < 0 || index >= Percentages.Length || index == list.SelectedIndex) return;
        string? key = list.gameObject.name switch
        {
            UnitListName => UnitSelectionKey,
            BuildingListName => BuildingSelectionKey,
            HealthListName => EnemyHealthSelectionKey,
            _ => null
        };
        if (key == null) return;
        list.SelectedIndex = index;
        list.UpdateAllButtonStyles();
        SaveIndex(key, index);
    }

    private static Controls? ControlsFor(GameSetupScreenView? view)
    {
        RectTransform? parent = ControlParent(view);
        if (parent == null) return null;
        IntPtr key = parent.Pointer;
        if (!ControlsByParent.TryGetValue(key, out Controls? controls)) return null;
        if (controls.IsAlive && controls.IsUnder(parent)) return controls;
        ControlsByParent.Remove(key);
        return null;
    }

    private static void PruneOtherControlParents(IntPtr currentParent)
    {
        foreach ((IntPtr parent, Controls controls) in ControlsByParent.ToArray())
        {
            if (parent == currentParent) continue;
            try { SetAllVisible(controls, false); }
            catch { }
            ControlsByParent.Remove(parent);
        }
    }

    private static Controls? FindExistingControls(RectTransform holder)
    {
        UILabelButton_UI2? toggle = FindOnlyToggle(holder, ToggleName, out int toggleCount);
        UIHorizontalList_UI2? unit = FindOnlyList(holder, UnitListName, out int unitCount);
        UIHorizontalList_UI2? building = FindOnlyList(holder, BuildingListName, out int buildingCount);
        UIHorizontalList_UI2? health = FindOnlyList(holder, HealthListName, out int healthCount);
        TextField_UI2? unitDescription = FindOnlyText(holder, UnitDescriptionName, out int unitDescriptionCount);
        TextField_UI2? buildingDescription = FindOnlyText(holder, BuildingDescriptionName, out int buildingDescriptionCount);
        TextField_UI2? healthDescription = FindOnlyText(holder, HealthDescriptionName, out int healthDescriptionCount);

        int total = toggleCount + unitCount + buildingCount + healthCount + unitDescriptionCount +
            buildingDescriptionCount + healthDescriptionCount;
        if (total == 0) return null;
        if (toggleCount == 1 && unitCount == 1 && buildingCount == 1 && healthCount == 1 &&
            unitDescriptionCount == 1 && buildingDescriptionCount == 1 && healthDescriptionCount == 1)
        {
            logger.LogInfo("Recovered the existing advanced setting rows after a setup-view refresh.");
            return new Controls(toggle!, unit!, unitDescription!, building!, buildingDescription!, health!, healthDescription!);
        }

        logger.LogWarning($"Found an incomplete or duplicated advanced UI set ({total} named controls); rebuilding it once.");
        return null;
    }

    private static UILabelButton_UI2? FindOnlyToggle(RectTransform holder, string name, out int count)
    {
        UILabelButton_UI2? result = null;
        count = 0;
        foreach (UILabelButton_UI2 candidate in holder.GetComponentsInChildren<UILabelButton_UI2>(true))
        {
            if (candidate?.gameObject == null || candidate.gameObject.name != name) continue;
            result = candidate;
            count++;
        }
        return count == 1 ? result : null;
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
        ToggleName or UnitListName or UnitDescriptionName or BuildingListName or BuildingDescriptionName or
        HealthListName or HealthDescriptionName;

    private static void DiscardPartialOrDuplicateControls(GameSetupScreenView view, RectTransform parent)
    {
        List<UIBasicComponent> discarded = new();
        foreach (UILabelButton_UI2 candidate in parent.GetComponentsInChildren<UILabelButton_UI2>(true))
            if (candidate?.gameObject != null && IsCustomName(candidate.gameObject.name)) discarded.Add(candidate);
        foreach (UIHorizontalList_UI2 candidate in parent.GetComponentsInChildren<UIHorizontalList_UI2>(true))
            if (candidate?.gameObject != null && IsCustomName(candidate.gameObject.name)) discarded.Add(candidate);
        foreach (TextField_UI2 candidate in parent.GetComponentsInChildren<TextField_UI2>(true))
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
        if (view.allComponents == null) return;
        IUILayoutable[] ordered = controls.Layouts;
        if (ordered.Length != 7) return;
        IUILayoutable? nativeToggle = AsLayoutable(view.advancedSettingsToggle);
        for (int index = view.allComponents.Count - 1; index >= 0; index--)
        {
            IUILayoutable? entry = view.allComponents[index];
            if (entry == null) continue;
            bool managed = nativeToggle != null && entry.Pointer == nativeToggle.Pointer;
            for (int i = 0; !managed && i < ordered.Length; i++) managed = entry.Pointer == ordered[i].Pointer;
            if (managed) view.allComponents.RemoveAt(index);
        }

        int continueIndex = FindComponentIndex(view, view.continueButton);
        int mapAnchor = Math.Max(
            FindComponentIndex(view, view.listMapType),
            Math.Max(FindComponentIndex(view, view.listMapSize), FindComponentIndex(view, view.mapSizeDescriptionText))
        );
        int insertAt = continueIndex >= 0
            ? continueIndex
            : Math.Clamp(mapAnchor + 1, 0, view.allComponents.Count);
        foreach (IUILayoutable component in ordered) view.allComponents.Insert(insertAt++, component);

        NormalizeSiblingOrder(view, controls);
        NormalizeAllLists(view, controls);
    }

    private static void NormalizeSiblingOrder(GameSetupScreenView view, Controls controls)
    {
        RectTransform? parent = ControlParent(view);
        RectTransform? continueTransform = view.continueButton?.rectTransform;
        if (continueTransform?.parent == null || parent == null || continueTransform.parent.Pointer != parent.Pointer) return;
        RectTransform[] transforms = controls.Transforms;
        if (transforms.Length != 7) return;
        foreach (RectTransform transform in transforms)
            if (transform?.parent == null || transform.parent.Pointer != parent.Pointer) return;

        // Move the whole managed block behind Continue first. Every subsequent
        // move therefore has a stable source after Continue and inserts the next
        // row immediately before it, making repeated layout passes idempotent.
        foreach (RectTransform transform in transforms)
            transform.SetSiblingIndex(parent.childCount - 1);
        foreach (RectTransform transform in transforms)
        {
            transform.SetSiblingIndex(continueTransform.GetSiblingIndex());
        }
    }

    private static void NormalizeAllLists(GameSetupScreenView view, Controls controls)
    {
        if (view.allLists == null) return;
        for (int index = view.allLists.Count - 1; index >= 0; index--)
        {
            UIHorizontalList_UI2? list = view.allLists[index];
            if (list == null) continue;
            foreach (UIHorizontalList_UI2 managed in controls.Lists)
            {
                if (managed.Pointer != list.Pointer) continue;
                view.allLists.RemoveAt(index);
                break;
            }
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
        if (layout.Pointer == component.Pointer) return true;
        try
        {
            UIBasicComponent? layoutComponent = layout.TryCast<UIBasicComponent>();
            if (layoutComponent != null && layoutComponent.Pointer == component.Pointer) return true;
        }
        catch { }
        IUILayoutable? componentLayout = AsLayoutable(component);
        return componentLayout != null && layout.Pointer == componentLayout.Pointer;
    }

    private static IUILayoutable? AsLayoutable(UIBasicComponent? component)
    {
        if (component == null) return null;
        try { return component.TryCast<IUILayoutable>(); }
        catch { return null; }
    }

    private static void SetRowsVisible(Controls controls, bool visible)
    {
        foreach (UIBasicComponent component in controls.Rows) component.ActiveSelf = visible;
    }

    private static void SetAllVisible(Controls controls, bool visible)
    {
        foreach (UIBasicComponent component in controls.LayoutComponents) component.ActiveSelf = visible;
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
        if (activeRules.EnemyHealthPercent == 100) return default;
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
        if (index < 0 || index >= Percentages.Length || ReadIndex(key) == index) return;
        PlayerPrefs.SetInt(key, index);
    }

    private sealed class Controls
    {
        internal readonly UILabelButton_UI2 Toggle;
        internal readonly UIHorizontalList_UI2 UnitCost;
        internal readonly TextField_UI2 UnitDescription;
        internal readonly UIHorizontalList_UI2 BuildingCost;
        internal readonly TextField_UI2 BuildingDescription;
        internal readonly UIHorizontalList_UI2 EnemyHealth;
        internal readonly TextField_UI2 HealthDescription;
        internal readonly UIHorizontalList_UI2[] Lists;
        internal readonly TextField_UI2[] Descriptions;
        internal readonly UIBasicComponent[] Rows;
        internal readonly UIBasicComponent[] LayoutComponents;
        internal readonly IUILayoutable[] Layouts;
        internal readonly RectTransform[] Transforms;
        internal bool Expanded;
        internal bool ShowMapType;
        internal bool ShowMapSize;
        internal IntPtr BoundScreen;
        internal Il2CppSystem.Action? ToggleAction;

        internal Controls(
            UILabelButton_UI2 toggle,
            UIHorizontalList_UI2 unitCost,
            TextField_UI2 unitDescription,
            UIHorizontalList_UI2 buildingCost,
            TextField_UI2 buildingDescription,
            UIHorizontalList_UI2 enemyHealth,
            TextField_UI2 healthDescription
        )
        {
            Toggle = toggle;
            UnitCost = unitCost;
            UnitDescription = unitDescription;
            BuildingCost = buildingCost;
            BuildingDescription = buildingDescription;
            EnemyHealth = enemyHealth;
            HealthDescription = healthDescription;
            Lists = new[] { unitCost, buildingCost, enemyHealth };
            Descriptions = new[] { unitDescription, buildingDescription, healthDescription };
            Rows = new UIBasicComponent[]
                { unitCost, unitDescription, buildingCost, buildingDescription, enemyHealth, healthDescription };
            LayoutComponents = new UIBasicComponent[]
                { toggle, unitCost, unitDescription, buildingCost, buildingDescription, enemyHealth, healthDescription };
            Layouts = LayoutComponents.Select(AsLayoutable).Where(layout => layout != null).Select(layout => layout!).ToArray();
            Transforms = LayoutComponents.Select(component => component.rectTransform).Where(transform => transform != null)
                .Select(transform => transform!).ToArray();
        }

        internal bool IsAlive
        {
            get
            {
                try
                {
                    return LayoutComponents.All(component =>
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
                return holder != null && LayoutComponents.All(component =>
                    component.rectTransform?.parent != null &&
                    component.rectTransform.parent.Pointer == holder.Pointer);
            }
            catch
            {
                return false;
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

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void PositionRowsAfterNativeLayout(GameSetupScreenView __instance) =>
        AdvancedMatchSettings.FinalizeViewLayout(__instance);
}

[HarmonyPatch(typeof(UIHorizontalList_UI2), "OnDragEnded")]
internal static class AdvancedSettingsDragCommitPatch
{
    [HarmonyPostfix]
    private static void CommitSelection(UIHorizontalList_UI2 __instance) =>
        AdvancedMatchSettings.CommitHighlighted(__instance);
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
    private static void Apply(out AdvancedMatchSettings.UnitCostScope? __state) =>
        __state = AdvancedMatchSettings.BeginUnitCostScope(GameManager.GameState);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.UnitCostScope? __state)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(TrainCommand), nameof(TrainCommand.IsValid))]
internal static class AdvancedUnitCostValidationPatch
{
    [HarmonyPrefix]
    private static void Apply(TrainCommand __instance, GameState state, out AdvancedMatchSettings.UnitCostScope? __state) =>
        __state = AdvancedMatchSettings.BeginUnitCostScope(state, __instance.Type);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.UnitCostScope? __state)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(TrainCommand), nameof(TrainCommand.Execute))]
internal static class AdvancedUnitCostExecutionPatch
{
    [HarmonyPrefix]
    private static void Apply(TrainCommand __instance, GameState state, out AdvancedMatchSettings.UnitCostScope? __state) =>
        __state = AdvancedMatchSettings.BeginUnitCostScope(state, __instance.Type);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.UnitCostScope? __state)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(InteractionBar), "RefreshBuildingOptions")]
internal static class AdvancedBuildingCostUiPatch
{
    [HarmonyPrefix]
    private static void Apply(out AdvancedMatchSettings.BuildingCostScope? __state) =>
        __state = AdvancedMatchSettings.BeginBuildingCostScope(GameManager.GameState);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.BuildingCostScope? __state)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(BuildCommand), nameof(BuildCommand.IsValid))]
internal static class AdvancedBuildingCostValidationPatch
{
    [HarmonyPrefix]
    private static void Apply(BuildCommand __instance, GameState state, out AdvancedMatchSettings.BuildingCostScope? __state) =>
        __state = AdvancedMatchSettings.BeginBuildingCostScope(state, __instance.Type);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.BuildingCostScope? __state)
    {
        __state?.Restore();
        return __exception;
    }
}

[HarmonyPatch(typeof(BuildCommand), nameof(BuildCommand.Execute))]
internal static class AdvancedBuildingCostExecutionPatch
{
    [HarmonyPrefix]
    private static void Apply(BuildCommand __instance, GameState state, out AdvancedMatchSettings.BuildingCostScope? __state) =>
        __state = AdvancedMatchSettings.BeginBuildingCostScope(state, __instance.Type);

    [HarmonyFinalizer]
    private static Exception? Restore(Exception? __exception, AdvancedMatchSettings.BuildingCostScope? __state)
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
