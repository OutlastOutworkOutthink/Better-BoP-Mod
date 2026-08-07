# Better Battle of Polytopia Mod — Alpha 0.6.7

This Alpha keeps the working Oblivion, Discord integration, and universal-peace
behavior locked, then adds the isolated client for bot-created Modded games.
All other earlier gameplay and experimental UI changes remain inactive.

## Oblivion

Open **Creative**, continue to tribe/game setup, and select **Oblivion** in the
same rule row as **Perfection**, **Domination**, and **Infinity**.

Alpha 0.6.7 retains both the visible legacy setup row and UI2's later layout
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

Alpha 0.6.6 retains the legacy tribe-info fix that replaces separate enabled and
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

Each match uses Polytopia's native blue lobby row, lobby information popup, and
tribe picker. The unselected Modded list contains only the native game rows; all
setup information and actions stay inside the selected game's popup. The
Discord opener is permanently the host.

An unselected player has a tribeless head and a **Choose Tribe** button in place
of **Start Game**. Choosing a tribe is permanent for that match: the head changes
to the selected tribe, all green/placeholder ready badges remain hidden, and
the picker cannot be reopened. After choosing, no Start button is shown until
the opponent also locks a tribe. Once both choices exist, only the host receives
**Start Game**. Pressing it invokes Polytopia's stock generator and creates a
real two-player **Tiny Dryland Domination** game. Integrated games remain
unranked and require no Discord confirmation.

The server's 121-tile setting is converted to Polytopia's native 11-by-11 side
length and validated before the host uploads it.
The private Better BoP server stores the initial state and ordered command bytes,
then reports an agreed in-game winner to the bot after resignation or capital
capture. It does not collect a Polytopia or Steam password.

The permanent Discord link is identity only. Alpha 0.6.6 separately proves its
current compatible ruleset to the multiplayer server, so players do not need to
relink Discord for every future release.

When no match is active, the tab displays **You have no active modded games.**
The Tournaments bubble is hidden while Modded is selected and restored when the
player returns to Ongoing/Replays or leaves Multiplayer.

## Advanced match settings

Creative/Oblivion and multiplayer game setup keep Polytopia's native Map Type
and Map Size rows above the advanced section. A single native-style,
mod-owned collapsible control follows them with the exact labels **Show Advanced
Settings** and **Hide Advanced Settings**. It is wired directly instead of
depending on Polytopia's optional advanced-settings callback. Expanding it
displays three native percentage rows. Every
row offers **25%**, **50%**, **100%** (default), **150%**, **200%**, **300%**, and
**500%**:

- **Unit cost for you** multiplies the rules owner's training prices and
  rounds upward. Bots retain normal prices. At 500%, a 2-star Warrior costs 10
  stars and an 8-star Knight costs 40.
- **Your building cost** applies the same rounded calculation to every
  paid tile interaction for the rules owner. Because this uses Polytopia's
  common improvement cost, it covers roads and special-tribe buildings such as
  algae and outposts without separate per-building patches.
- **Enemy unit health** multiplies every opposing unit's maximum health. At
  500%, a unit with 100 internal maximum health has 500 internally and displays
  as 50 HP in the game. Converted units preserve their health percentage while
  immediately adopting the correct maximum for their new owner.

The selected percentages are snapshotted when a supported game is created, so
changing the setup defaults does not rewrite older games. Network games embed a
compact invisible marker for modded clients, while a game-ID cache preserves
the values across restarts.
Each list and its description has its own layout row, and only these three
rows collapse. The mod creates clean native controls instead of cloning the
already-populated Map Size row, recovers one named set across setup rebuilds,
and removes incomplete or duplicate sets. Visibility and ordering are restored
immediately before Polytopia's own view layout, so Map Type and Map Size remain
above the toggle and every advanced entry receives its own row. The entire
custom block is parented to Polytopia's page-scroller content, so the rows and
Start Game move together. Vertical gestures over a percentage row route to the
page, while a horizontal drag commits the centered value once when released.
Price changes
use short-lived scopes around Polytopia's native
train/build UI, validation, and execution paths; the original shared data is
restored immediately and bots keep their normal prices. The 100% defaults exit
before allocating a cost scope or resolving an owner, selections are flushed to
disk only when starting a game, and no per-frame scan is added.

Alpha 0.6.7 deliberately does not patch or mutate the home screen. The attempted
version label in Alpha 0.6.2 could still terminate the native IL2CPP process
immediately after `StartScreen.Init()` without producing a managed exception.
The installed version remains visible in PolyMod and `manifest.json`, and the
BepInEx load line records it without touching Unity's home-screen lifecycle.

## Inactive archive

The previous mod source remains in the repository for reference, but is
explicitly excluded from `BetterBoPMod.dll`. See
[`ARCHIVED_FEATURES.md`](ARCHIVED_FEATURES.md).

## Install

Download the Alpha 0.6.7 release ZIP, extract it, and place the
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
Better BoP Alpha 0.6.7 loaded: scroll-safe advanced settings with drag selection.
```

## Development

Build with `dotnet build -c Release`, then copy
`bin/Release/net6.0/BetterBoPMod.dll` into the project root before packaging.
