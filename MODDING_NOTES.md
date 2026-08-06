# Better BoP modding notes

Compact implementation notes for repeatable fixes. Keep runtime claims tied to
an actual Polytopia/BepInEx log; a successful build proves compatibility with
the reference assemblies, not that the patched screen executed correctly.

## Creative game-mode UI (Polytopia 122 / PolyMod 1.2.17)

- Current PC Creative setup uses `GameSetupScreen_UI2` and renders its mode row
  through `GameSetupScreenView.SetShowGameModes` / `listGameMode`.
- Do not call `UIHorizontalListData.HasData()` from an `OnShow` postfix. On
  Polytopia 122 its native object may exist while internal fields are still
  null; Alpha 0.5.1 produced a `NullReferenceException` inside `HasData()` and
  aborted the Oblivion insertion.
- Check `labels` and `ids` individually before reading them. Wrap optional UI
  enhancement patches so a failed insertion cannot break the setup screen.
- Alpha 0.5.2 showed that neither model IDs nor the incoming label count are a
  reliable detection boundary: the mod loaded without error, but neither
  insertion path ran. Gate on `GameSettings.BaseGameMode == Custom`, then append
  to `GameSetupScreenView.listGameMode.data` after the vanilla view has rendered.
- Inputs may be localization keys, so never require the English text
  `Perfection`, `Domination`, or `Infinity` to identify the row.
- Alpha 0.5.3 logged that Creative was active while
  `GameSetupScreenView.listGameMode` was still unavailable during `OnShow`.
  Treat `OnShow` as an early diagnostic only. Recheck the legacy
  `GameSetupScreen.gameModeList` after `Show`, `OnScreenUpdated`, and
  `RefreshValuesFromSettings`, and recheck UI2 after its controller/view
  `RunLayout` callbacks.
- A visible Oblivion menu does not prove the game seed was activated. Arm from
  both setup-screen start callbacks as well as `CreateSinglePlayerGame`, keep
  the arm flag set until `GameState` exists, and persist at both `OnGameReady`
  and `OnLevelLoaded`.
- Oblivion relationships are invariants, not additive modifiers: return exactly
  `-200` for a bot observing the local human and exactly `+200` for a bot
  observing another bot. Apply this at last priority so difficulty and normal
  boons cannot override it.
- A null `PlayerState.AccountId` does not prove a player is a bot: offline local
  humans can also have no account ID. Check `GameManager.IsPlayerLocal(id)`
  first, then use `AutoPlay`/account state only for non-local players.
- The AI can consume the cached native `OpinionState.total`, not only the
  managed `GetOpinion` return. Enforce Oblivion totals after opinion refreshes
  and immediately before `AI.GetMove`; patch `AI.ShouldAcceptPeace` separately.
- Without Diplomacy, `PlayerInfoPopup.Refresh` does not call
  `GetLocalizedTopReasons`, so a label postfix alone cannot display reason
  pills. For Oblivion, hide only the locked-info panel and call the popup's
  native `SwapButtons` with the rebuilt three-reason dictionary. Do not fake
  tech ownership.
- If only the rendered list can be expanded, intercept selection of its fourth
  `Oblivion` entry before vanilla handles it. Vanilla's model may still contain
  only three items and would otherwise index past the end.

## Discord profile link (Polytopia 122 / PolyMod 1.2.17)

- `UIRoundButton_UI2.OnPointerClick` does not exist in the shipped Polytopia
  122 IL2CPP metadata. An attributed Harmony patch targeting it logs an
  undefined-target error during startup. Use the button's native
  `OnClickedSignal` plus the verified `UIButtonBase_UI2.OnButtonClicked` hook.
- Treat every optional profile lifecycle hook as an independent Harmony patch.
  One renamed game method must not prevent the other creation paths from
  loading.
- Never call `SetStyle` or `SetButtonSize` from the `RunLayout` postfix. Restore
  only anchors, position, scale, sibling order, and visibility there; style or
  size setters can trigger another native layout pass.
- OAuth polling finishes while Polytopia is normally unfocused. Persist the
  token/account ID first, do not open a completion popup in that callback, and
  do not force a profile layout while Windows is returning focus to Unity.
- Do not subscribe a managed/IL2CPP delegate to `Application.focusChanged` from
  mod startup. Alpha 0.5.9 loaded every patch and then terminated before the
  Polytopia bootloader began, immediately after registering that new delegate.
  Let the existing profile lifecycle repaint persisted state instead.
- A reboot retaining the green state proves `PlayerPrefs` persistence and the
  server-side link succeeded; it does not prove the live focus-return UI path
  was safe. Check the log around `ApplicationFocused False/True` separately.
- Treat a green account-link control as status, not as permission to start a
  second OAuth flow. Guard the shared click entry point before creating a link
  session; every pointer/controller route already converges there.
- The API is the authoritative second guard: reject a link-session request for
  an existing Polytopia ID, enforce immutable one-to-one Discord/Polytopia
  uniqueness in the callback transaction, and announce only when the insert
  actually returns a new row. Same-pair race/legacy callbacks may refresh the
  credential but must not insert or announce again.

## Universal peace treaties (Polytopia 122 / PolyMod 1.2.17)

- Peace is `PlayerAbility.Type.PeaceTreaty`, normally stored in a visible
  technology's `abilityUnlocks`. Remove it from every visible tech and attach
  it to hidden `TechData.Type.Basic`; this removes Strategy's unlock icon while
  preserving command validation and AI discovery.
- Cover all native lookup paths: `IsUnlocked(PlayerAbility.Type, PlayerState)`,
  `HasAbility`, `GetUnlockedAbilities`, and the ability overload of
  `GetRequiredTech`. A UI-only override is insufficient for bots and command
  validation.
- `PlayerInfoPopup.CreateDiplomacyActionButton` may retain both action and
  unavailable callbacks. `ClearCallbacks`, register one correct peace action,
  and intercept both `UIButtonBase.OnPointerClick` and `OnButtonClicked` for
  registered peace buttons. Deduplicate by button pointer plus frame because a
  physical click can reach both routes. Merely marking the button active leaves
  the stale Strategy callback alive. Keep the unavailable-popup suppression as
  a final guard, and do not invoke the command again from it.
- Identify treaty commands through `CommandBase.GetCommandType()` rather than
  relying on an IL2CPP managed subtype check in the legacy UI path.
- Clean the passed `TechData` before both `TechItem.GetUnlockItems` and
  `TechPopupContent.CreateTechDataContent`; these protect cached game data that
  predates the parse postfix.
- Prepare the base ability before `AI.AddPossibleDiplomacyCommands`. Leave
  native opinion/scoring intact. Oblivion's last-priority command filter still
  removes bot-to-human peace and bot-to-bot peace breaking.

## Verification checklist

1. Build Release with zero warnings and errors.
2. Copy the Release DLL to the mod root and compare SHA-256 hashes.
3. Confirm archived features do not appear as symbols in the compiled DLL.
4. Package one `Better-BoP-Mod` folder with the DLL, manifest, patch, and README.
5. Test ZIP integrity and inspect the packaged manifest version.
6. In game, inspect the BepInEx log for both the Alpha load line and the
   `Added Oblivion ...` insertion line before treating the UI as verified.

## Integrated Modded games (Alpha 0.5.21)

- Keep the Discord link permanent and version-independent. Compatibility is
  established by `/v1/auth/exchange` from the running client; never require a
  player to recreate OAuth just because the mod version changed.
- Discord joining provisions the server match and channel immediately. Tribe
  selection is the confirmation: new matches go `waiting_for_tribes` →
  `provisioning` as soon as both tribes exist, then `active` →
  `completed|disputed`. Keep `/start` and `ready_to_start` support only as
  recovery for older clients/rows; no player-facing Start step remains.
- Only the immutable host generates and uploads the initial game state. Before
  upload, require `CreateSessionResult.Success`, two players, non-zero map
  dimensions, and a non-empty tile array. A failed upload must retry the same
  in-memory session instead of generating a second map.
- Render server matches as synthetic `LobbyGameViewModel` objects through
  `MultiplayerScreen.AddLobbyRow`. This reuses Polytopia's blue row, LobbyPopup,
  map/mode/timer/more-info controls, and player slots. Intercept lobby start,
  invite, and leave actions so synthetic lobbies never call Midjiwan's backend.
- Open the stock `TribeSelectorScreen` for the local player slot, submit its
  selected tribe to Better BoP, and automatically load the initial state for
  the guest after the host's map upload makes the game active.
- Add Modded by extending `MultiplayerSelectionScreen.ScreenSelectionList` and
  render rows through the stock `MultiplayerScreen`. Do not alter the Ongoing or
  Replays models, and restore the stock New Game button when leaving Modded.
- Alpha 0.5.14 proved that `Awake`, `OnEnable`, and `Show` can all execute while
  `ScreenSelectionList.data/ids` are still null: every Harmony patch loaded but
  no insertion or exception was logged. Recheck after the selected-screen and
  content `OnScreenUpdated` boundaries. Also guard the owned
  `UIHorizontalList.SetData` postfix so a later asynchronous vanilla refresh
  cannot replace Modded with only Ongoing/Replays again.
- Alpha 0.5.15 proved that the visible prefab row can remain usable while both
  `data` and `ids` stay null and `SetData` never runs. Reconstruct the two
  vanilla labels from `keys` or the rendered `items` text, then call `SetData`
  once with explicit IDs and the appended Modded entry. Recheck from the owned
  list's `OnEnable` and `CreateItems` boundaries because serialized prefab
  lists may never invoke `SetData` themselves.
- Alpha 0.5.16 proved the tab injection, but clicking Modded crashed in
  `MultiplayerScreen.AddInfoRow`: the HTTP continuation reached the native UI
  while PolyMod's captured `SynchronizationContext` was null. Never mutate
  IL2CPP UI or game state from an async continuation. Queue it and drain from
  the verified `GameManager.Update` main-thread boundary; clear pending renders
  when the multiplayer selection screen disables.
- Alpha 0.5.18 proved that reaching the Unity main thread is necessary but not
  sufficient. `MultiplayerScreen.AddInfoRow` and `AddButtonRow` use
  `ListReuseHelper`; wrap the entire custom clear/build pass in
  `listReuse.BeginRefresh()` unless a vanilla refresh is already active. The
  guard must be disposed after all custom rows have been added.
- Alpha 0.5.19 then proved the helper itself can still be null when the custom
  tab suppresses `BuildListAsync` before the stock list's first build. Once the
  stock container and row prefabs exist, construct `ListReuseHelper` from that
  container, assign it back to the screen, and only then begin the refresh.
- A saved Discord link is version-independent and requires both the account ID
  and integration token. Treat a partial/mismatched pair as repairable, prompt
  once when Modded opens, and invalidate both local credentials only after the
  auth exchange explicitly returns 401/403. The OAuth callback may rotate the
  token only for the exact existing Discord/Polytopia pair and must not announce
  recovery as a new integration.
- Existing active games still open explicitly. Only the match whose tribe was
  just chosen in this running client may auto-open; this prevents a restart or
  background poll from unexpectedly loading an older game from the main menu.
- Retry transient `MatchEnded` result failures in memory, then clear the active
  transport as soon as the server acknowledges the result.
- Alpha 0.5.14 locks the first preset to map size 121, Dryland, two players, and
  Domination. The ruleset contains no Warrior or other unit-stat override.
- Command transport is participant-only, ordered, idempotent, and turn-gated.
  Result completion remains dual-report: both clients must name the same winner;
  disagreements are disputed and never change bot Elo.
- Before release, run the source hash verifier and a clean Release build, then
  perform a real two-PC smoke test: native lobby popup, both tribe picks,
  automatic host generation and guest opening, first turn, end turn in each
  direction, reopen, resignation, capital capture,
  Discord completion, and unchanged Elo. A compile cannot prove native IL2CPP
  session/player ownership behavior.

## Advanced match handicaps (Alpha 0.6.5)

- Reuse `GameSetupScreen_UI2.advancedSettingsExpanded`, but create each custom
  row with `UILibrary.NewHorizontalList`/`NewText`. Never clone the live Map Size
  list: its populated Tiny/Small/etc. children and coordinates survive cloning
  and overlap percentage labels. Key controls by the setup holder, recover them
  by exact names, and discard incomplete or duplicated named sets so game-mode
  rebuilds remain idempotent.
- Normalize `GameSetupScreenView.allComponents` to Map Type, Map Size, advanced
  toggle, then the three list/description pairs. Reassert visibility, the toggle
  label, and that order in a prefix on `GameSetupScreenView.RunLayout`; doing it
  in a screen-layout postfix is too late because the controls retain stale row
  coordinates. Let Polytopia's native view layout position them, and call public
  `UpdateLayout()` only after initial creation or a user toggle—never recursively
  from inside the view-layout hook.
- Snapshot the three selected percentages when the host creates the game.
  Persist by game ID and embed a compact validated marker in `GameName` so a
  modded peer reads the same deterministic rules. Only strip a marker after all
  three encoded indexes validate, or a legitimate game name could be cut.
  For local games without a session ID, consume the pending snapshot once at
  `OnGameReady`; reset to 100% defaults when opening a game that was not armed
  by the setup screen so older saves cannot inherit the previous match's rules.
- Do not Harmony-patch `UnitData.get_cost` or `ImprovementData.get_cost` in an
  IL2CPP build. PolyMod identifies both as generated field accessors and cannot
  create a safe native patch backend. Instead, temporarily substitute scaled
  values around `InteractionBar` price rendering and the matching
  `TrainCommand`/`BuildCommand` validation and execution calls, then restore in
  a Harmony finalizer. Cache the rules and immutable rules owner when a session
  opens; hot paths still require only an owner comparison and ceiling math.
- Enemy units need their scaled current health filled when created. Conversion
  is a separate ownership transition: preserve the unit's health percentage
  across `ConvertAction.Execute`, then adopt the maximum appropriate to its new
  owner. Otherwise converted enemies can retain health above the friendly cap.
- A clean build does not prove that a backend preserves invisible game-name
  code points or that native-created UI rows survive every shipped prefab revision.
  Before publishing, test host and guest values, reopen persistence, all seven
  percentages, bot costs, roads/special improvements, spawning, conversion,
  healing, and narrow/wide setup layouts on the Windows game build.
- The handicap UI must include `GameType.SinglePlayer`; Creative/Oblivion uses
  that type. Restricting it to network types silently removes both the stock
  advanced toggle and all three rows from the Creative setup screen.
- Do not patch `StartScreen` or `StartScreen_UI2` to add a home version label.
  Creating a TMP object crashed Alpha 0.6.1, and even changing the existing
  About label after layout still caused Alpha 0.6.2 to terminate immediately
  after `StartScreen.Init()` with no managed exception. Alpha 0.6.3 removes the
  complete hook; keep the version in `manifest.json` and the BepInEx load line.
- Lobby readiness must be calculated from both nullable tribe selections, not
  merely the server status or two accepted Discord seats. Write the result to
  `LobbyPopup.Description` after `SetData`/`RefreshPopup` so the native
  "Ready to start!" text cannot survive while either tribe is unselected.

## Release numbering

- Keep all releases on **Alpha 0.6.X** until the project owner explicitly asks
  to move to **Alpha 0.7**. Increment only the patch component after 0.6.0.
- Every release must update `manifest.json`, `Main.cs`, `README.md`, and the
  release ZIP/tag to the exact same version. Do not restore a home-screen
  version label until a native-safe hook has been proven on the shipped build.
