# Better BoP engineering notes

Living notes for future development and debugging. Keep this compact, factual,
and updated whenever a bug reveals a new constraint. Record the symptom, root
cause, fix, and the test that prevents a repeat.

## Current baseline

- Mod release: **Alpha 0.4.9** (`manifest.json`).
- Build target: .NET 6, PolyMod `1.2.17`.
- Current interop package observed during Alpha 0.4.9 work:
  `TheBattleOfPolytopia 2.17.2.16299`.
- Entry point: `Main.Load`. Every Harmony patch is loaded separately through
  `SafePatch`; preserve that isolation so one incompatible hook cannot remove
  unrelated UI or gameplay features.
- Build: `dotnet build -c Release`, then copy
  `bin/Release/net6.0/BetterBoPMod.dll` to the repository root.
- Release ZIP must contain exactly one top-level `Better-BoP-Mod` folder with
  `BetterBoPMod.dll`, `manifest.json`, `patch.json`, and `README.md`.
- Every participant in an Integrated game must install the same gameplay build.

## Architecture map

| File | Responsibility | Important constraints |
| --- | --- | --- |
| `Main.cs` | Patch registration and online-state rule application | Add every new patch through `SafePatch`. UI patches load first. |
| `BetterBoPRules.cs` | Tech, peace, embassy, Mind Bender, Ai-Mo rules and related UI | Rules must be reapplied after `ParseGameLogicObjects` and before received online states are processed. |
| `GrowGiant.cs` | Spiritualism, Giant Seed build/visuals, delayed Giant spawn | Uses native `NullBuilding` serialization; see dedicated notes below. |
| `GiftStars.cs` | Transfer flow, confirmation UI, 80% receipt, Generous state | Human multiplayer gifts must use the Integrated ordered stream. |
| `TribeRelations.cs` | Opinion values and clickable top-three reasons | Value calculation and displayed reason calculation must remain consistent. |
| `OblivionMode.cs` | Creative-mode UI and persistent Oblivion state | Both legacy and UI2 setup screens require patches. |
| `DiscordIntegration.cs` | Profile button, browser handoff, OAuth polling/token storage | The button must own and clear inherited callbacks. Never store Discord secrets in the client. |
| `IntegratedMultiplayer.cs` | Assignment polling, host state, serialized command relay, result reporting | The opener is host. Preserve command ordering and one shared ruleset. |
| `MultiplayerRestrictions.cs` | Blocks official/manual matchmaking paths | Integrated games remain the supported modded multiplayer route. |

## Rule implementation pattern

Changing only text or button colour is never enough. A complete rule normally
has four layers:

1. **Data:** move/add the unlock in `GameLogicData` (`TechData` unlock lists,
   tribe overrides, costs, terrain requirements).
2. **Validation:** patch the exact `IsUnlocked`, `HasAbility`, `CanBuild`, or
   command-validation path used by the engine.
3. **Execution/state:** use native `CommandBase`, `ActionBase`, `GameState`,
   tile, unit, or improvement state whenever possible.
4. **Presentation:** update action availability, unavailable popups, tech info,
   icons, descriptions, and confirmation/result UI.

Reapply global data mutations whenever Polytopia parses/replaces
`GameLogicData`; otherwise local games may work while online games silently use
stock rules.

### Harmony/IL2CPP safety

- Target overloads explicitly with parameter types or a filtered
  `TargetMethods`; names alone are risky.
- Harmony parameter binding uses original parameter names. Confirm those names
  in the interop assembly before sharing one postfix across overloads.
- IL2CPP wrapper assemblies expose signatures but usually not native gameplay
  bodies. Compilation proves API compatibility, not rule correctness.
- Prefixes that temporarily mutate shared data must restore it in a postfix and
  a finalizer. Embassy income follows this pattern.
- Keep patches idempotent. Online state loading and UI refreshes can execute
  the same hook repeatedly.
- Log failures with the patch/coordinate/feature involved; avoid swallowing a
  gameplay exception without context.

## Native-state and multiplayer rule

Prefer an unused native enum/state slot over client-only dictionaries for any
state that must survive saves, replays, or multiplayer. Integrated multiplayer
relays serialized Polytopia commands; all clients then execute the same patched
logic. A local-only flag will desynchronize.

Custom envelopes are acceptable only when both sender and receiver explicitly
serialize, relay, and apply them in the same ordered stream, as Gift Stars does.

Do not assume the custom server simulates Polytopia. It stores/relays state and
commands; deterministic client behavior and identical mod versions are still
required.

## Grow Giant / Giant Seed (Alpha 0.4.9)

- `ImprovementData.Type.NullBuilding` (`44`) is repurposed as Giant Seed. It was
  unused in the inspected game version and therefore serializes through native
  `BuildCommand`/`ImprovementState` without inventing a save format.
- Spiritualism receives this improvement unlock. `IsUnlocked` additionally
  restricts it to tribes whose `TribeData.category` is `Human` (the basic-tribe
  category, not special tribes).
- Cost: 20. Legal tile: owned, empty, resource-free `Field`; the terrain and an
  existing road remain unchanged.
- The build uses `ImprovementAbility.Type.Simple`; rewards, creates, adjacency,
  routes, and growth data are cleared.
- On `StartTurnAction.Execute`, an owned seed with `GetAge(state) >= 1` tries
  `ActionUtils.TryPushUnitDefault`. Hatch is deferred if the tile remains
  occupied. Otherwise the improvement is removed and a zero-cost native
  `TrainAction` creates `UnitData.Type.Giant`.
- If training aborts, restore the seed instead of deleting the paid structure.
- `SpriteData.GetHeadSpriteAddress(TribeType.None)` supplies the requested
  tribeless head. `UIUtils.GetImprovementSprite`, `Building.UpdateObject`, and
  `TechPopupContent.SetBuildingData` cover build, world, and tech UI.
- `SetBuildingData` already creates the native info badge. Replace its callback;
  do not add a second badge.

Version risk: `NullBuilding` is safe only while the base game leaves enum 44
unused. Recheck it after every Polytopia/PolyMod upgrade. If the base game starts
using it, migrate saves deliberately rather than silently reassigning it.

### Grow Giant smoke matrix

- Basic tribe: absent before Spiritualism; present afterward.
- Special tribe: never available.
- Costs exactly 20; insufficient-star state is disabled by native UI.
- Only empty, owned, resource-free fields work. Forest, mountain, water,
  improvement, resource, neutral, and enemy tiles fail.
- Building preserves field terrain and an existing road.
- Seed is visible as one large tribeless head and is named Giant Seed.
- It does not hatch immediately or at another player's turn start.
- It hatches once at the owner's next turn; multiple seeds all hatch once.
- Occupying unit: valid push destination, blocked push destinations, enemy unit,
  and no unit.
- Save/reload before hatching.
- Integrated host and participant both see identical seed, push, and Giant.
- Destroy/sell interaction during the waiting turn: confirm desired rule before
  changing engine-default behavior.

## UI rules learned from regressions

- Mirror native Polytopia components, parent containers, sizes, spacing, and
  callbacks. Custom large controls tend to overlap or appear in screen centre.
- Tech-tree node icons and the selected-tech popup are separate code paths.
  Test both before and after researching the tech.
- A button can receive one physical click through both pointer handling and its
  backing Button event. If replacing behavior, intercept both or deduplicate by
  button pointer plus `Time.frameCount`.
- `ClearCallbacks` before taking ownership of an inherited button. Failure to
  do so caused Connect Discord to open the stock logfile-location popup and
  embassy buttons to run both new and stale actions.
- Keep confirmation buttons anchored in the popup's normal left/right or bottom
  layout. Never hand-position them relative to the full screen.
- Disabled choices must be visually disabled and command validation must still
  reject them; colour alone is not enforcement.
- Relation summaries show at most three reasons. Every custom boon/penalty must
  use the same clickable green/red component and have a matching description.

## Feature-specific constraints

### Peace and embassies

- Peace is attached to hidden `Basic` and also patched through both
  `GameLogicData.IsUnlocked(PlayerAbility, PlayerState)` and `HasAbility`.
  This prevents the rule from working internally while appearing grey.
- Embassy and Capital Vision belong to Strategy (`Shields` enum), not Diplomacy.
- Embassy action availability, click callback, unavailable popup target,
  description, tech icon, and tech text must all point to Strategy.
- Base embassy income is one per level. During the starting player's turn,
  Diplomacy temporarily doubles the shared income value; always restore it.
- Peace's normal multiplier remains separate, producing 1/2 before Diplomacy
  and 2/4 after Diplomacy.

### Gift Stars and Generous

- Strategy unlocks gifts of 5, 10, or 20. Recipient receives 80%.
- The picker uses three compact native round buttons. Affordable options are
  active/blue; unaffordable choices stay disabled.
- Confirmation and completion layouts must use native popup anchors.
- Generous is a separate clickable relation reason, not text appended to
  Charming. Its description is: “You have shown kindness to them”.
- `GenerousUntilTurn` is client memory reconstructed from Integrated gift
  envelopes; test save/reload expectations before promising persistence.

### Relations and Oblivion

- Respected: +5 per peace treaty above wars, cap +20; current code activates
  when at least two peace treaties are active and the net balance is positive
  (so 2 peace / 1 war gives +5, matching the supplied example).
- Hated mirrors it: at least two active wars and a negative net balance, -5 per
  net war, cap -20.
- Dominating begins turn 6, is disabled in 1v1, and applies by total player
  count: 3–5 `[-100]`, 6–10 `[-100,-50]`, 11+ `[-100,-50,-25]`.
- Remove both vanilla Dominating value and vanilla displayed reason before
  inserting the replacement, or the penalty can be counted twice.
- Oblivion is Creative/Sandbox plus opinion rules: bot-to-bot +200 and local
  player “the enemy” -200. Preserve all Creative map/setup options.

### Discord and Integrated versioning

- `manifest.json` is the downloadable mod release version.
- `DiscordIntegration.ModVersion`, `IntegratedMultiplayer.RulesetId`, and the
  Better-BoP-Server `RULESET` are a coupled compatibility contract. Never bump
  only one side; linking/assignment will be rejected.
- Alpha 0.4.9 currently has three different values: manifest `0.4.9`,
  `DiscordIntegration.ModVersion` `0.4.8`, and
  `IntegratedMultiplayer.RulesetId`/token key `0.4.5`. The deployed server
  advertises mod/ruleset `0.4.8`.
- **Open critical compatibility debt:** a fresh server-token exchange can reject
  the stale `better-bop-0.4.5` ruleset; an older cached token can conceal the
  mismatch. The server also cannot distinguish 0.4.8 from 0.4.9 clients even
  though only 0.4.9 has Grow Giant. Update all client and server identifiers
  together before treating Integrated Grow Giant as version-safe.
- OAuth redirect URI must exactly match Discord Developer Portal and Railway.
  Never commit client secrets, bot tokens, database URLs, OAuth tokens, or
  Railway variables.
- The profile button launches an external URL. Test Windows browser handoff,
  label, icon/fallback, single click, OAuth completion, and return polling.

## Regression ledger

| Symptom | Root cause | Durable rule/test |
| --- | --- | --- |
| Warrior appeared with 2 HP after a 15 HP change | Internal/display health scaling was patched at the wrong layer | Do not guess stored stat units. Inspect native data and test displayed and combat health; current Warrior override is removed. |
| Peace/embassy command worked but stayed grey or requested the wrong tech | Gameplay data, UI availability, stale callbacks, and unavailable popup were different paths | Test locked/unlocked appearance, click result, popup text, and actual command validation independently. |
| Gift Stars/tech icons covered other UI | Custom controls used the content root and oversized layout instead of native boon container | Clone/use native-sized components and inspect at multiple resolutions. |
| Gift confirmation buttons appeared at top centre | Buttons were not anchored in native popup layout | Test confirmation and result popups, not only the picker. |
| Generous appeared as plain fourth text | It bypassed the native top-three clickable-reason system | Add custom reasons to both opinion value and standard clickable reason pipeline. |
| Connect Discord was a blue/question-mark circle and opened Logfile location | Reused profile control retained stock sprite/callback routes | Prefer text label fallback and clear/intercept every inherited click route. |
| One broken Harmony hook removed unrelated UI | Patches were registered as one failure-prone group | Keep each patch behind `SafePatch`, with UI entry points first. |
| Manifest and server compatibility numbers drifted | Release version and Integrated ruleset version serve different systems | Check both deliberately on every release and document any temporary mismatch. |

## Efficient verification workflow

Full in-game simulation is slow, so use layered checks and reserve manual play
for behavior Unity/IL2CPP actually controls.

1. **Static:** inspect the exact PolyMod/game package version; search enum values,
   methods, overloads, and original parameter names with metadata tools or
   `strings`.
2. **Compile:** Release build with zero errors/warnings. Rebuild the root DLL and
   verify its checksum matches `bin/Release/net6.0/BetterBoPMod.dll`.
3. **Pure logic tests:** when adding calculations, extract eligibility, prices,
   turn thresholds, rankings, and state transitions into ordinary C# helpers
   with no Unity/IL2CPP dependencies. Test those exhaustively.
4. **Command/state review:** confirm multiplayer state is represented by a
   native serialized command/state field or an explicit Integrated envelope.
5. **Focused in-game smoke:** test only the UI, native action behavior, rendering,
   and lifecycle cases that cannot run outside Polytopia.
6. **Package:** verify manifest version, ZIP layout, `unzip -t`, and asset hash.
7. **Release:** merge first, build/package from the merged commit, upload the
   versioned ZIP, then read the release metadata back from GitHub.

Do not claim a feature was simulated in-game when only compilation/static checks
were possible. Record the untested cases in the smoke matrix.

## Future simulator direction

To reduce repeated manual sessions, keep Harmony patches thin and move rules
into a small engine-independent model:

- immutable tile/player/turn snapshots;
- pure `CanUse`, `Cost`, `AdvanceTurn`, and `Apply` functions;
- event output such as `SpendStars`, `PlaceSeed`, `PushUnit`, `SpawnGiant`;
- adapters that translate events to native Polytopia actions.

This permits fast tests for hundreds of turn/tile/player combinations while one
small in-game smoke test verifies each adapter. Do not attempt to instantiate
the IL2CPP wrapper types in ordinary unit tests; many methods forward to native
function pointers that exist only inside the running game.

## Updating these notes

For every meaningful bug, add one regression-ledger row and update the relevant
smoke matrix. Use this template:

```text
Version / date:
Symptom:
Scope (local / UI / save / Integrated):
Root cause:
Fix:
Automated check:
Manual check still required:
```
