# Better Battle of Polytopia Mod — Alpha 0.5.19

This Alpha keeps the working Oblivion, Discord integration, and universal-peace
behavior locked, then adds the isolated client for bot-created Modded games.
All other earlier gameplay and experimental UI changes remain inactive.

## Oblivion

Open **Creative**, continue to tribe/game setup, and select **Oblivion** in the
same rule row as **Perfection**, **Domination**, and **Infinity**.

Alpha 0.5.19 retains both the visible legacy setup row and UI2's later layout
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

OAuth completion is saved without rebuilding the profile or opening a popup
while Polytopia is unfocused. No native Unity focus delegate is registered at
startup. The already-proven profile lifecycle repaints the circle green when
the profile refreshes or is reopened; if Polytopia is already focused, it
updates immediately.

Each Polytopia profile still owns one permanent Discord identity. A healthy
connection made by an older Alpha—including 0.5.8—carries forward without a new
OAuth login. If the local credential is missing, mismatched, or rejected, the
Modded tab prompts the player to reconnect. OAuth may then rotate the credential
only after proving the exact original Discord/Polytopia pair; it never creates
a second link or another player-updates announcement.

## Universal peace treaties

Peace Treaty is a base ability for every human and bot tribe. It is removed
from Strategy's unlock list and is shown as available in tribe information even
before Strategy is researched. Sending a request no longer opens the obsolete
Strategy warning after the request is sent.

Alpha 0.5.19 retains the legacy tribe-info fix that replaces separate enabled and
disabled callbacks with one deduplicated peace action. The icon therefore uses
its available appearance and a click cannot also reach the old Strategy popup.

Bot diplomacy uses the same base ability, so bots can consider and send peace
requests to other bots without Strategy. Their normal opinion and scoring still
decide whether they actually choose the request. Oblivion remains authoritative:
bots still reject/remove peace with the local enemy and preserve bot alliances.

## Integrated Modded games

Connected players can use `?open Classic Integrated` in Discord and another
connected player can join it. Joining automatically creates the Discord channel
and server game—there is no separate Discord confirmation. Open Multiplayer in
Polytopia and select the new **Modded** tab beside **Ongoing** and **Replays**.

Both players select a tribe there. The Discord opener is permanently the host
and receives the Start button once both tribes are selected. Alpha 0.5.19 creates
a two-player **Tiny Dryland Domination** game; Integrated games are unranked.
The private Better BoP server stores the initial state and ordered command bytes,
then reports an agreed in-game winner to the bot after resignation or capital
capture. It does not collect a Polytopia or Steam password.

The permanent Discord link is identity only. Alpha 0.5.19 separately proves its
current compatible ruleset to the multiplayer server, so players do not need to
relink Discord for every future release.

When no match is active, the tab displays **You have no active modded games.**
The Tournaments bubble is hidden while Modded is selected and restored when the
player returns to Ongoing/Replays or leaves Multiplayer.

## Inactive archive

The previous mod source remains in the repository for reference, but is
explicitly excluded from `BetterBoPMod.dll`. See
[`ARCHIVED_FEATURES.md`](ARCHIVED_FEATURES.md).

## Install

Download the Alpha 0.5.19 release ZIP, extract it, and place the
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
Better BoP Alpha 0.5.19 loaded: locked gameplay baselines plus automatically created Integrated Modded games.
```

## Development

Build with `dotnet build -c Release`, then copy
`bin/Release/net6.0/BetterBoPMod.dll` into the project root before packaging.
