# Better Battle of Polytopia Mod 0.4.4

A multiplayer-aware PolyMod gameplay overhaul for The Battle of Polytopia.

## Current changes

- Warriors use the normal game health value; the former 15 HP override has
  been removed from both JSON and runtime code.
- Peace treaties can be offered without researching a technology.
- Strategy now unlocks embassies, capital vision, and Gift Stars. Strategy
  embassies produce 1 star per level; Diplomacy doubles all current and future
  embassy income to 2 stars per level.
- Gift Stars offers 5, 10, or 20-star transfers with confirmation. The recipient
  receives 80%, and AI recipients gain a temporary clickable **Generous** boon.
- Mind Benders are unlocked by Meditation instead of Philosophy. Ai-Mo starts
  with Meditation and a Mind Bender.
- Reapplies Better Battle of Polytopia game logic whenever Polytopia parses a
  fresh copy for a multiplayer session.
- Reapplies the same rules before the client processes a game state received
  from the multiplayer backend.
- Adds a text-only **Connect Discord** button at the top-right of the in-game profile screen.
- Links the signed-in Polytopia account through Discord OAuth without collecting
  a Polytopia or Steam password.
- Polls Discord-created **Integrated** assignments from the Better BoP server.
- Automatically fixes the opener as host, loads the two-player match after both
  Discord confirmations, relays serialized commands, and reports the winner.
- Relays Gift Stars through the same ordered Integrated command stream.
- Temporarily disables manual friend invites and official random/manual game
  creation. Integrated games must be created and hosted through the Discord bot.

After linking, the profile button becomes **Check Games**. Discord games opened
with `?open Classic Integrated` or `?open Modern 5 Integrated` appear there
after both players accept the Discord prompt.

## Multiplayer requirements

- Every player must install the same release of this mod.
- Start a new test match after installing version 0.4.4.
- Keep `manifest.json`, `patch.json`, and `BetterBoPMod.dll` together
  in the installed mod folder.

The BepInEx log writes `Applied Better BoP technology, diplomacy, embassy, Mind
Bender, and Ai-Mo rules` when the gameplay rules are loaded.

## Install for development

Copy this project folder into the PolyMod mods directory, keeping the manifest,
patch, and compiled DLL together at the top level of the mod folder. Then
enable the mod and restart the game.

Run `dotnet build -c Release`, then copy
`bin/Release/net6.0/BetterBoPMod.dll` into the project root before
packaging the mod.
