# Archived features — inactive in Alpha 0.5.6

Alpha 0.5.6 keeps the reset baseline. Only `Main.cs`, `OblivionMode.cs`, and
`OblivionOpinions.cs` are compiled as mod behavior.

The following source files are preserved as notes/history but explicitly
excluded in `BetterBoPMod.csproj`, so they cannot patch or change the game:

| Archived source | Inactive experiments |
| --- | --- |
| `BetterBoPRules.cs` | technologies, embassies, peace, Ai-Mo, unit/rule edits |
| `GiftStars.cs` | star gifting and generous relation behavior |
| `GrowGiant.cs` | Giant Seed and Grow Giant |
| `TribeRelations.cs` | respected, hated, dominating, generous, old Oblivion code |
| `DiscordIntegration.cs` | in-game Discord account-link UI |
| `IntegratedMultiplayer.cs` | server-created game integration |
| `MultiplayerRestrictions.cs` | friend/random/manual multiplayer restrictions |

Do not reactivate an archived file wholesale. Future features should be rebuilt
one at a time on top of the Oblivion-only baseline, with a focused in-game smoke
test before the next feature is enabled.

## Oblivion invariants

- Menu location: Creative rule selector, beside Perfection/Domination/Infinity.
- Game rules: `BaseGameMode = Custom`, `RulesGameMode = Domination`.
- Bot observing bot: exact stored and returned opinion of +200.
- Bot observing local player: exact stored and returned opinion of -200.
- Local identity is checked before the null-account bot heuristic.
- AI peace responses, diplomacy command choices, and pre-move stored opinions
  follow the same invariant.
- Oblivion always exposes three relation reasons without unlocking Diplomacy.
- Human opinions are not modified.
- Existing non-Oblivion saves are not modified.
