# River Features 1.0.0: configuration reference

Every key of both config files, with defaults and ranges, plus the `/riverfeatures` commands. The game also writes
`ModConfig/riverfeatures-settings.txt` at every server start: the same keys with their current values.

## `riverfeatures-rivers.json` (river network)

Written on first start with every value (config version 8); out-of-range values are clamped, a value that is not a number
falls back to its default, and unknown keys are kept in the file. Read at server start only. `Enabled: false` turns the
river network part off. World-creation settings (`rh*` in worldconfig) override some keys per world.

| Key | Default | Range | Meaning |
|---|---|---|---|
| `Enabled` | true | | Master switch for the river network part. |
| `MaxRiverLengthBlocks` | 10000 | 0..30000 | Growth cap for the main stem (sets Rivers' maxNodes in memory), not a target: rivers usually top out around 10000 to 11000 even at higher values. 0 = Rivers' own length (about 5000). |
| `MaxNodesPerRiver` | 900 | 50..5000 | Safety cap on nodes per river. |
| `SourceLakeChance` | -1 | -1 or 0..1 | Chance of a lake at a river's source. -1 = Rivers' `lakeChance`. |
| `VariedSourceLakes` | true | | Oval, finger, winding and lobed source lakes instead of Rivers' teardrop. |
| `LakeRiverClearance` | 32 | 0..96 | Least land between a source lake and any other channel or lake. |
| `TributaryLengthShare` | 0.6 | 0.1..1 | Node budget of the secondary branch at a fork (1 = Rivers' symmetric forks). |
| `TributaryWidthShare` | 0.6 | 0.1..1 | A tributary is at most this share of the river it joins. |
| `TributaryMaxSize` / `TributaryMinSize` | 28 / 8 | 2..200 / 1..max | Absolute width cap and floor for tributaries. |
| `BranchMinSpacing` | 32 | 0..200 | Least land between a tributary and other channels; a crowded tributary is cut back. 0 = off. |
| `DeltaChance` | 0.35 | 0..1 | Delta chance per mouth (x 0.5 + mouth size / Rivers' maxSize). |
| `DeltaMinRiverSize` | 30 | 1..200 | No delta on narrower mouths. |
| `DeltaMinBranches` / `DeltaMaxBranches` | 2 / 4 | 2..8 | Distributaries rolled for carving deltas. |
| `DeltaSpreadDegrees` | 45 | 5..80 | Fan spread (channels are capped at 65 degrees from the sea direction). |
| `DeltaMinLength` / `DeltaMaxLength` | 120 / 260 | 20..1000 / ..2000 | Apex distance up the river from the coast (x scale). |
| `DeltaWidthShare` | 0.5 | 0.1..1 | Distributary width as a share of the mouth. |
| `DeltaMaxRise` | 6 | 0..200 | A channel whose banks stand higher above the sea is bent less, then dropped. |
| `DeltaSmallShare` / `DeltaShallowShare` | 0.334 / 0.333 | 0..1 | Shares of the small and shallow delta types; the rest carve. |
| `ShallowDeltaDepth` | 2 | 1..6 | Depth of the shallow delta plain (the plain varies 1..3 blocks of water). |
| `ShallowDeltaGrooveDepth` | 3 | 0..6 | Extra depth of each shallow channel's centre groove (plus 1..5 per delta). |
| `MouthShallowRange` | 60 | 0..255 | On/off switch for the river-mouth fade (above 0 = on). |
| `MouthFadeBlocks` | 40 | 8..200 | Length of the mouth fade past the coast. |
| `DeltaScaleMin` / `DeltaScaleMax` | 0.6 / 1.6 | 0.3..2.5 | Delta size range. |
| `DeltaSurface` | true | | Delta bank material and plants in the chunk pass. |
| `ClimateInfluence` | 0.7 | 0..1 | How strongly climate leans delta looks and trace kinds (not dry rivers). |
| `OxbowChance` | 0.65 | 0..1 | Oxbow chance per eligible bend. |
| `OxbowMinBendDegrees` | 12 | 1..170 | Least turn at a bend. |
| `OxbowMinRiverSize` | 10 | 1..200 | No oxbows on narrower rivers. |
| `OxbowMaxLevel` | 2 | 0..20 | Deepest branch level that gets oxbows. |
| `OxbowMinRadius` / `OxbowMaxRadius` | 30 / 60 | 8..300 / ..400 | Oxbow radius range. |
| `OxbowWidthShare` | 0.5 | 0.1..1 | Oxbow width as a share of the river. |
| `OxbowMaxRelief` / `OxbowBankMargin` | 7 / 12 | 0..200 / 0..64 | Oxbows and traces only on ground this flat (sampled this far out). |
| `OxbowRiverClearance` | 20 | 0..96 | Least land between an oxbow and other rivers, this river's deltas, traces and other oxbows. |
| `DryOxbowShare` | 0.3 | 0..1 | Share of oxbows that are dry. |
| `TraceChance` / `TraceClearance` | 0.25 / 24 | 0..1 / 0..96 | Traces of the past per eligible node, and their clearance. |
| `DryRiverChance` / `DryRainLimit` | 0.6 / 0.4 | 0..1 | Dry branch chance, scaled by how far rainfall is below the limit. |
| `DryRiverMaxSize` / `DryFloorAboveSea` | 12 / 3 | 2..50 / 1..7 | Widest dry channel; its floor stays this far above the sea. |
| `HeadwaterMaxRise` / `HeadwaterMaxTrimNodes` / `HeadwaterBankMargin` | 8 / 3 / 10 | 0..200 / 0..10 / 0..64 | Source tips on ground this high above the water are trimmed (at most this many nodes). |
| `HeadwaterCalmBlocks` / `HeadwaterMinSpeed` | 800 / 0.25 | 0..5000 / 0.05..1 | Flow speed ramps up from the sources over this distance. |
| `TerrainNoiseShare` | 1 | 0..1 | How much landform noise the terrain estimate includes. |
## `riverfeatures-lakes.json` (terrain pass)

`ModConfig/riverfeatures-lakes.json`. Values outside their range are clamped (or replaced by the default if not a number) with a log line.
Keys are **case-sensitive**. An unknown or wrongly cased key (`lakeChance`) is ignored, named in one log line (with the right spelling when only
the case differs), and **kept in the file as you wrote it** whenever the mod rewrites the file. A file that is not a JSON object (invalid
JSON, empty, `[1]`, `"x"`, `null`) is copied to `riverfeatures-lakes.json.bak` and replaced by defaults; if the copy fails the file is left alone
and the defaults are used for that session. A config problem never stops the server: the worst case is the defaults plus one log line.
Block distances and heights are for a 256-high world; at startup they are scaled by `worldHeight / 256`
(**height** column below), and horizontal distances are additionally multiplied by the world's `landformScale` (**horizontal**; clamped 0.25 to 4).
`MaxRiseAboveSea` is also capped so sea level + rise stays 2 below the world top.

| Key | Default | Range | Meaning | Scaling |
|---|---|---|---|---|
| `Enabled` | true | true/false | Master switch for swamps, plain lakes and delta banks. | none |
| `PlainLakes` | false | true/false | **Deferred in 1.0.0.** Plain lakes (every `Lake*` key below) are planned only when true. Source lakes (`riverfeatures-rivers.json`) and swamps do not depend on it. | none |
| `RiverSpawnChance` | -1 | ignored | **No longer used** (pre-release step 2): River Features never writes another mod's config. Set `riverSpawnChance` in Rivers' own `rivers.json`. `set` refuses it. | none |
| `LakeChance` | 0.12 | 0..1 | Share of the **map** inside active lake outlines. The ceiling (every feature active) is measured at startup from the lake shapes, about 0.07 to 0.12 depending on the seed, and printed in the `worldgen ready` line; above it nothing changes and the log says so. Water covers less than the outlines. | none |
| `RiverLakeChance` | 0 | ignored | Removed (flow-through lakes, flight test 11). Read from old files and ignored; `set` refuses it. | none |
| `SwampChance` | 0.15 | 0..1 | Share of the map the swamp noise mask covers (calibrated by sampling at startup). Protection rules and the rainfall test remove columns, so visible swamp is less. | none |
| `LakeMaxRadius` | 48 | 8..200 | Largest lake disc radius (the shaped outline lies inside it). Minimum radius = max(8, this/3); feature cell = 3 x this. | horizontal |
| `LakeMinDepth` | 3 | 1..20 | Minimum water depth; shallower columns stay dry. | height |
| `LakeMinArea` | 80 | 0..900 | Smallest lake in water columns; smaller ones stay dry (flight test 9: mini-lakes). 0 keeps all. | horizontal squared |
| `LakeRiverGap` | 24 | 0..128 | Least land (blocks) between a lake and any Rivers channel, delta channel, oxbow, trace or source lake. A lake that would come closer shrinks, or is dropped below its minimum radius; decided once per lake per session. 0 = off. | none |
| `OceanBuffer` | 10 | 0..100 | Lakes and swamps keep this far from ocean (any column at or below sea level). **Effectively capped at 32** after scaling (the height data reaches one chunk out). Above 10 it also shrinks the largest floodable basin: span = min(22, 32 - buffer), e.g. 21 at 11, 12 at 20, 2 at 30, 1 from 31. | horizontal |
| `RuinMargin` | 12 | 0..100 | Margin left untouched around every worldgen structure (ruins etc.); one block more on the +X/+Z sides (the structure box's max is exclusive). | height |
| `SwampWaterCoverage` | 0.35 | 0.05..0.9 | Share of swamp ground that gets water pockets. | none |
| `SwampMinRain` | 0.45 | 0..1 | Minimum climate rainfall for a swamp (ramps in over 0.05). | none |
| `MaxRiseAboveSea` | 12 | 0..64 | Lakes and swamps act only up to this many blocks above sea level. | height |
| `MaxSlope` | 1.0 | 0.1..4 | Steepest ground (blocks of height per block) lakes and swamps will touch. | none |
| `MaxCut` | 5 | 0..32 | Deepest lake carve **below the water level** (a bank column up to `MaxCut` - `LakeMinDepth` above the water loses that much more); shore easing and bed smoothing never cut more than this from a column's own surface. Swamps use ceil(this / 2). | height |
| `MaxFill` | 2 | 0..32 | Most ground a swamp column may gain (lakes never fill). **Currently has no effect:** swamps fill only ground below sea level + 1, and every column at or below sea level is ocean-excluded. | height |
| `MaxRimVariation` | 6 | 0..32 | Lake or swamp column rejected if the 5x5 heights around it vary by more than this. | height |
| `FadeMargin` | 8 | 0..64 | Cuts fade to zero over this distance from the feature edge. Swamps use max(this, 3 x swamp cut), capped at 16. | horizontal |
| `SwampPlantCodes` | `tallplant-coopersreed-land-normal-free`, `tallplant-tule-land-normal-free`, `tallgrass-tall-free` | list | Block codes placed on swamp land. Unknown codes are skipped with one log line each. | none |
| `SeedOffset` | 0 | any whole number | Added to the world seed for this mod's noise and grid. | none |

## Commands

`/riverfeatures show | set <key> <value> | reload` (privilege `controlserver`), for `riverfeatures-lakes.json`. The river network file (`riverfeatures-rivers.json`) is edited by hand and read at server start.

- `show` lists every key with its value and type (ranges are in the table above, not printed).
- `set <key> <value>` takes a key (case-insensitive) and a value of the right type (whole number, number with a dot, true/false, comma-separated list). It goes through the same
  parsing and clamping as the file and the reply names the value actually stored. A bad value is rejected and nothing changes.
  **`set` rewrites `riverfeatures-lakes.json` from the loaded settings**, so hand edits made since the last start or reload are overwritten: run `reload` first if you edited the file.
- `reload` re-reads the file. A missing, empty, malformed or non-object file is **not applied**: the old settings and the file stay as they were, and the reply says why.
- **When changes apply:** to **newly generated chunks only**. Existing chunks are never changed. Both `set` and `reload` rebuild the worldgen state live (including `Enabled` and
  the lake and swamp chances).
- **Seam warnings:** changing **any** worldgen setting mid-world (`LakeChance`, `SwampChance`, `FadeMargin`, `MaxCut`, `OceanBuffer`, ... not only
  `SeedOffset` and `LakeMaxRadius`) makes chunks generated afterwards disagree with older ones at their shared border: expect cut-off lakes and swamps,
  steps or strips there. The reply names the worst cases (`SeedOffset`, `LakeMaxRadius` and the feature grid it drives) explicitly; the others are silent.
- If `LakeChance` is above the achievable ceiling (about 0.095), the startup log and the `set`/`reload` reply state the ceiling and the effective share.

## Rivers chance

Rivers' own file is `ModConfig/rivers.json`; set its `riverSpawnChance` there yourself. River Features never writes that file
(the old `RiverSpawnChance` mirror was removed in pre-release step 2). Values River Features changes in Rivers' config
(`maxNodes` from River length, `lakeChance` from Source lakes) are changed in memory only, never saved.


