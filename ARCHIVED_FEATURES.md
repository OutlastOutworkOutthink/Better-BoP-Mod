# Archived features — inactive in Alpha 0.5.5

Alpha 0.5.5 keeps the reset baseline. Only `Main.cs`, `OblivionMode.cs`, and
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
- Bot observing bot: native opinion plus 200, never below the love threshold.
- Bot observing local player: native opinion minus 200, never above the hate
  threshold.
- Human opinions are not modified.
- Existing non-Oblivion saves are not modified.
