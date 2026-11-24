# River Features

A server-side add-on for the **Rivers** mod in Vintage Story. It reshapes Rivers' river networks before the terrain is
carved: longer rivers with a main stem and narrower tributaries, deltas at the coast, oxbow lakes, varied source lakes,
traces of old channels, dry riverbeds and calm headwaters. A small terrain pass adds swamps and gives delta banks their
material and plants.

**Version 1.0.0, a baseline for testing and feedback.** It has been flown in test worlds and has 180 unit tests, but
it has not been tried widely. Bug reports, screenshots with coordinates, and critique are welcome (see [Feedback](#feedback)).

| | |
|---|---|
| Game | Vintage Story **1.22.7** (built against 1.22.7's runtime, net10.0) |
| Requires | **Rivers 5.0.2** (mod id `rivers`) |
| Side | Server (clients only need it for the world-creation settings and their texts) |
| Mod id | `riverfeatures` |

## Install

1. Download `riverfeatures_1.0.0.zip` from [Releases](https://github.com/ChichBleD2/quasimorbhoduck/releases) and put the zip itself (do not unpack it) into your `Mods` folder next to
   Rivers 5.0.2.
2. Create a **new world** (or travel to land that has not been generated yet). Nothing changes in existing chunks.
3. Optional: on the *Rivers & Lakes* tab of the world-creation screen, pick river length, deltas, oxbows, dry rivers,
   source lakes, lake shapes and swamps for that world.

Only one copy may be installed: do not keep an unpacked folder of the mod next to the zip.

## What it does

**River network** (Harmony patches on Rivers' own network generation; nothing is decompiled or copied from Rivers)
- **Longer rivers and a hierarchy:** the longest upstream path becomes the main stem; at forks the secondary branch gets
  a shorter budget and tributaries are narrower than the river they join. Tributaries that run right beside another
  channel are cut back.
- **River length** (default up to 10,000 blocks) is a *growth cap*, not a promised length. Terrain, coastlines, other
  rivers and Rivers' own spacing stop most rivers sooner; in test worlds the longest rivers reached about 10,000 to
  11,000 blocks even with a 15,000 or 20,000 cap. Rivers' spawn chance is never touched.
- **Deltas** on land at the real coast: small, carving or shallow, each with a size and a character (sandy, marshy,
  overgrown, dry, intermittent, rocky) leaned softly by the local climate. Channels are spaced apart, meander gently with
  rounded bends, and shallow deltas deepen gradually up the river. River mouths stop carving at sea level, so there is no
  trench across the sea floor.
- **Oxbow lakes** beside river bends (some dry), on flat ground, clear of other rivers and of each other.
- **Traces of the past:** abandoned dry channels, backwaters and floodplain pockets.
- **Source lakes:** Rivers' lakes at river sources reshaped into oval, finger, winding, lobed or teardrop lakes, kept
  clear of other channels.
- **Dry rivers** (wadis), likelier where rainfall is low; **calm headwaters** whose flow speeds up downstream.

**Terrain pass**
- **Swamps** on low, flat, rainy ground: shallow water pockets, peat and marsh plants.
- **Delta banks:** bank material (sand, mud, gravel, rock) and plants matching each delta's character.
- **Structure check:** ruins, BetterRuins buildings and trader camps are not placed in water, half-submerged, or on
  islands and spits. A building on a river bank or shore is fine.

**Not in 1.0.0:** plain standalone lakes are deferred (switched off, code kept; `PlainLakes` in the lakes config), and
erosion was removed.

## Settings

Two JSON files in `ModConfig`, written on first start with every key:

- `riverfeatures-rivers.json`: the river network (lengths, deltas, oxbows, traces, dry rivers, spacing). Read at server
  start.
- `riverfeatures-lakes.json`: the terrain pass (swamps, structure margin, deferred plain lakes). `/riverfeatures reload`
  or `/riverfeatures set <key> <value>` applies changes live.

**`ModConfig/riverfeatures-settings.txt`** is rewritten at every server start and lists every key of both files with its
current value, default, range and meaning; that is the place to look before changing a number. Edit the JSON files,
not the sheet. A world-creation dropdown other than *Default* overrides its key for that world. Changes apply to newly
generated chunks only. The full reference is in [docs/CONFIG.md](docs/CONFIG.md).

River Features writes only its own files. It never writes another mod's config: Rivers' `rivers.json` stays as you left
it, and the two Rivers values it adjusts (`maxNodes` from River length, `lakeChance` from Source lakes) are changed in
memory only.

## Commands

All need the `controlserver` privilege.

| Command | What it does |
|---|---|
| `/rffind <kind>` | The nearest features placed this session: `delta` (or `delta-shallow`, `delta-marshy`, ...), `oxbow`, `lake` (source lakes), `trace`, `dry-river`, `swamp`. Positions as absolute X,Z and as the map X,Z the HUD shows. |
| `/rffind river [min] [max]` | Rivers near you (within 24,000 blocks) by main-stem length: the longest, or the nearest in a range (`8000`, `5000 9000`, `8k`). `/rffind river all ...` searches the whole session. |
| `/rffind kinds` | What was logged this session, with counts. |
| `/riverfeatures show \| set \| reload` | Show or change `riverfeatures-lakes.json`. |
| `/rfprobe [radius]` | Read-only diagnostics of the ground and water around you. |

The server log ends each session with two summary lines (rivers, deltas by type and look, oxbows, traces, source-lake
shapes, trims, structure vetoes).

## Compatibility

- **Rivers 5.0.2** is required. If Rivers' internals do not match (another Rivers version), the river network part stays
  off with one log line and Rivers runs unchanged.
- **Rivers Extended:** if installed, River Features makes no deltas and leaves river mouths alone (its estuaries shape
  them); everything else stays on.
- **Continental Worlds** works with it but is not needed; without it the game's own ocean map is used to find coasts.
- **Terrain replacers** (TerraPrety and similar) are not supported.
- Other worldgen mods that reshape rivers (for example Watersheds) have not been tested together with it.

## Known limitations

- River length is a cap; very long rivers are rare (see above).
- Lengths reported by `/rffind river` follow straight node lines, so rivers in the world are slightly longer.
- Delta channels are not checked against *other* rivers, and oxbows and traces are checked against points of their own
  river rather than its full course: on crowded coasts features can come close.
- Features are recorded for `/rffind` only when Rivers builds a region in the current session; after a restart,
  explore or wait for chunks to load before searching.
- Plain lakes are deferred; there is no erosion.
- Tested on singleplayer test worlds only.

## Building and testing

Needs the .NET SDK (net10.0) and an installed Vintage Story 1.22.7 (the game folder provides the API DLLs).

```powershell
dotnet test tests\RiverFeatures.Tests -p:VINTAGE_STORY="<Vintage Story folder>"
.\tools\package.ps1 -Game "<Vintage Story folder>"
```

The second command builds a Release DLL and writes `dist\riverfeatures_<version>.zip` (version from `modinfo.json`).
Rivers is not referenced at compile time: its internals are reached by reflection (`src/Rivers/RiversAdapter.cs`).
The tests that bind to Rivers' real DLL run only when the game has unpacked Rivers into its cache, and skip otherwise.

Layout: `src/Rivers/` is the river network part (namespace `RiverFeatures.Rivers`), `src/` the terrain pass,
`tests/` the unit tests, `assets/` the landform patch and world-creation texts.

## Feedback

Please [open an issue](https://github.com/ChichBleD2/quasimorbhoduck/issues) with: what you saw, the block coordinates (`/rffind` prints them), the world seed if you can share
it, your mod list, and whether the world was new. The `[RiverFeatures]` lines of `server-main.log` help a lot.

Background: [docs/HISTORY.md](docs/HISTORY.md) is the development log (flight tests and fixes),
[docs/RIVER_NETWORK_NOTES.md](docs/RIVER_NETWORK_NOTES.md) early research notes on how Rivers builds its networks.
