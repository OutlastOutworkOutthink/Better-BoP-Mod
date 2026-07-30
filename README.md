# PolyEconomic Balance 0.3.0

A PolyMod rules mod for The Battle of Polytopia.

## Current changes

- Regular Warrior maximum health increased to 15 HP (`150` in the game's
  internal stat scale).
- Reapplies PolyEconomic game logic whenever Polytopia parses a fresh copy for
  a multiplayer session.
- Reapplies the same rules before the client processes a game state received
  from the multiplayer backend.
- Adds a **Link Discord** button at the top-right of the in-game profile screen.
- Links the signed-in Polytopia account through Discord OAuth without collecting
  a Polytopia or Steam password.

## Multiplayer requirements

- Every player must install the same release of this mod.
- Start a new test match after installing version 0.3.0.
- Keep `manifest.json`, `patch.json`, and `PolyEconomicMultiplayer.dll` together
  in the installed mod folder.

The PolyScript writes `PolyEconomic multiplayer game-logic hook loaded` and an
`Applied Warrior health` entry to the BepInEx log. These messages confirm which
multiplayer loading path received the patched rules.

## Install for development

Copy this project folder into the PolyMod mods directory, keeping the manifest,
patch, and compiled DLL together at the top level of the mod folder. Then
enable the mod and restart the game.

Run `dotnet build -c Release`, then copy
`bin/Release/net6.0/PolyEconomicMultiplayer.dll` into the project root before
packaging the mod.
