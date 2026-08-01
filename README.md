# Better Battle of Polytopia Mod — Alpha 0.5.8

This Alpha keeps the working Oblivion behavior locked and adds one isolated
feature: Discord registration with the PolyEconomic Bot. All earlier gameplay,
multiplayer, and experimental UI changes remain inactive.

## Oblivion

Open **Creative**, continue to tribe/game setup, and select **Oblivion** in the
same rule row as **Perfection**, **Domination**, and **Infinity**.

Alpha 0.5.8 retains both the visible legacy setup row and UI2's later layout
callbacks, after Polytopia has actually created the game-mode controls.

Oblivion keeps all normal Creative setup choices, but the match itself uses
Polytopia's normal **Domination** victory rules. Its only additional behavior is:

- every bot's real AI opinion of every other bot is fixed at **+200**;
- every bot's real AI opinion of the local player is fixed at **-200**, even
  when an offline local player has no account ID;
- bots never offer or accept peace with the player, accept peace from other
  bots, and do not choose to break peace with other bots;
- the normal three-reason row is visible in Oblivion even without researching
  Diplomacy, with **the enemy** plus two native reasons;
- **the enemy** opens the description “You are the enemy.”

The mode is local single-player only in this Alpha.

## Connect Discord

Open the signed-in Polytopia profile. A red **Connect Discord** circle appears
at the top-right. Press it to open a single-use Discord
OAuth page in the default browser. After approval, the game marks the profile
button green, the PolyEconomic Bot registers the player, and the bot announces
the successful integration in the server's player-updates channel.

The link control is restored after eight profile lifecycle paths, UI-library
completion, and every late round-button layout/enable pass. Each hook is patched
independently so one changed game method cannot disable the others. The secure
URL is also copied to the clipboard before browser handoff
so it can be pasted manually if Windows blocks the automatic launch.

## Inactive archive

The previous mod source remains in the repository for reference, but is
explicitly excluded from `BetterBoPMod.dll`. See
[`ARCHIVED_FEATURES.md`](ARCHIVED_FEATURES.md).

## Install

Download the Alpha 0.5.8 release ZIP, extract it, and place the
`Better-BoP-Mod` folder directly inside Polytopia's `Mods` directory. These files
must be together at that folder's top level:

- `BetterBoPMod.dll`
- `manifest.json`
- `patch.json`
- `README.md`

Remove older Better BoP folders before installing this version, enable the mod,
then restart Polytopia. Start a new Creative → Oblivion game; older saves are not
converted into Oblivion games.

The BepInEx log confirms a successful load with:

```text
Better BoP Alpha 0.5.8 loaded: locked Oblivion plus resilient Discord account linking.
```

## Development

Build with `dotnet build -c Release`, then copy
`bin/Release/net6.0/BetterBoPMod.dll` into the project root before packaging.
