# River Features: development history

> This build was called **2.0.0** throughout development and is published as **1.0.0**; every "2.0.0" below means
> the 1.0.0 release. These notes are kept as written during development: flight-test reports (coordinates are from the
> developer's test worlds), the fixes they led to, and two archived reference parts from before the two earlier
> prototypes were merged. Where anything here disagrees with README.md or docs/CONFIG.md, those are right.

## State just before publication (as written then)

One mod for Vintage Story **1.22.7** on top of the **Rivers** mod (5.0.2): a river network (longer rivers, a main stem
with narrower tributaries, varied source lakes, deltas, oxbow lakes, traces of old channels, dry rivers and calm
headwaters) plus a terrain pass for swamps and delta banks. Plain lakes are deferred and erosion is removed in 2.0.0
(see below). One DLL, `RiverFeatures.dll`, mod id `riverfeatures`.

**STATUS: pre-release, in-world check passed (erosion off, without Continental Worlds). 180 unit tests pass (the tests that
bind to the installed Rivers.dll skip when the game has not unpacked Rivers into its cache). The erosion removal, the
river length floor and `/rffind river` below are not flown yet.**

**Build, test, install:** `dotnet test tests\RiverFeatures.Tests -p:VINTAGE_STORY="<game folder>"`, then
`.\tools\package.ps1 -Game "<game folder>"` writes `dist\riverfeatures_2.0.0.zip`; copy it to `%APPDATA%\VintagestoryData\Mods`.
Other mods are switched on and off in the game's mod menu; nothing here moves their archives.

## Setting the numbers

- Two JSON files in `ModConfig`: `riverfeatures-rivers.json` (river network, read at server start) and
  `riverfeatures-lakes.json` (swamps, plain lakes; `/riverfeatures reload` or `set` applies it live).
- `ModConfig/riverfeatures-settings.txt` is rewritten at every server start: every key of both files with its current
  value, default, range and meaning. Edit the JSON files, not the sheet. (JSON cannot keep comments through the mod's
  rewrite, so the sheet is where the numbers are explained.)
- A world-creation dropdown other than *Default* overrides its key for that world; *Default* uses the file.
- Everything applies to newly generated chunks only.

## /rffind river: rivers by length

- **Relative to you:** every query looks only at rivers whose mouth or source lies within 24000 blocks of your position
  (`RiverLog.NearRadius`), and all distances are from you. `/rffind river` lists the 8 longest of those;
  `/rffind river 8000` the 8 nearest whose main stem is at least 8000 blocks; `/rffind river 5000 9000` the nearest
  between 5000 and 9000 (`8k`, `7.5k` work too). `/rffind river all [min] [max]` searches the whole session instead.
- Every reply starts with a summary of the rivers near you (count, min / avg / max main-stem length) and of the whole
  session; a ranged query adds the same summary for the matches.
- **Far teleports:** Rivers builds a region's rivers (a region is 32768 blocks wide) when its first chunks generate. If
  nothing has been recorded near you yet (say 100000 blocks out, before chunks there load), the reply says "No river has
  been generated near you yet", with the distance to the nearest recorded river, instead of listing far-off rivers. Let
  chunks load and ask again.
- Each line: main-stem length, stem nodes and total river nodes, then the mouth and the source, each as absolute X,Z, map
  X,Z (as the HUD shows, like the other `/rffind` lines) and its distance from you. The mouth is Rivers' start point,
  usually a little offshore; the source is the end of the main stem.
- **Length** is measured along the main stem's nodes (from the mouth, always taking the child with the longest path
  upstream) after headwater trims and branch spacing, without deltas, oxbows or traces. It uses the straight node lines,
  so the river in the world, with Rivers' jitter, is slightly longer. The *River length* setting is only the cap
  (40 nodes x 250 blocks at 10000); most rivers end sooner, and world tests topped out around 10000 to 11000 blocks.
- **Recorded once per river:** only on the first build of a region in a world (the same region-rebuild deduplication as the
  other logs), and keyed by the mouth position as a second guard. Cleared when a world starts; at most 20000 rivers.
- `/rffind kinds` also shows the number of rivers recorded.

## Erosion removed (2.0.0)

- The erosion pass (gullies, rounded ledge lips) is gone from the code (`ErosionPass`, `ErosionRules`), the config
  (`Erosion`, `ErosionStrength`, `ErosionMaxCut`, `ErosionMinSlope`), the settings sheet, the commands, the shutdown
  statistics and the tests. An existing `riverfeatures-lakes.json` loses those four keys at the next start (one log line,
  "removed setting(s) dropped"); `set` refuses them.
- Kept: swamps, delta banks, source lakes and every river feature. The soil check that swamps and lakes used moved to
  `WaterRules.IsSoil`.
- Sections further down that describe erosion are flight-test history.

## River length vs. river spawn chance (checked against Rivers' IL)

- **What to expect (a cap, not a promise):** River length raises the most a river may grow; it does not make rivers that
  long. How far each river actually gets depends on the terrain (it grows uphill from the coast and stops on high
  ground), the coastline and oceans around it, Rivers' spacing from other rivers (128 blocks) and its own generation
  limits. Measured with `/rffind river` in world tests, the longest rivers topped out around **10,000 to 11,000 blocks**
  even at 15,000 and 20,000. Values above 10,000 are harmless but rarely change much, so there is no need to hunt seeds
  or max the setting out expecting every river to reach it.
- **Where the length comes from:** `MaxRiverLengthBlocks` in `riverfeatures-rivers.json` (10000), or the world-creation
  *River length* dropdown (5000 / 7500 / 10000 / 15000 / 20000 blocks; *Rivers default* = 0 = Rivers' own; *Default* = the
  file).
- **How it is applied:** once per Rivers config object, before a region's rivers are generated (`ZonesPrefix`), River
  Features sets Rivers' `maxNodes` **in memory** to length / mean node length (`minLength` + `lengthVariation` / 2 = 250
  blocks with Rivers' defaults): 5000 gives 20 (Rivers' own default), 7500 gives 30, 10000 gives 40, 15000 gives 60,
  20000 gives 80 nodes. `rivers.json` is never written. At a fork the secondary branch gets a share of the remaining
  budget (`TributaryLengthShare`); `MaxNodesPerRiver` (900) is a safety cap.
- **Spawn chance untouched:** River Features never reads or writes `riverSpawnChance`. In Rivers' `GenerateZones` the
  spawn roll (`rand.NextFloat()` against `riverSpawnChance`) runs per coastal zone and plants each river's first node before
  any river grows; `maxNodes` is read only in `GenerateRiver` (how far a river grows). So where and how often rivers start
  does not depend on the length setting.
- **One interaction found and fixed:** `GenerateZones` removes every river with fewer than `minNodes` (8) nodes before
  sizing it. A length under about 2000 blocks gave `maxNodes` below 8, which would have removed every river. `maxNodes` is
  now never set below Rivers' `minNodes`. (River Features' own node trims run after that check, so they cannot remove a
  river either.)
- **Indirect only:** longer rivers occupy more of a region, and Rivers keeps nodes `riverPaddingBlocks` (128) apart, so
  other rivers may stop growing sooner. That changes lengths, not the number of rivers that start.

## Pre-release step 2: Continental Worlds and other mods' configs

- **Continental Worlds:** no code depends on it (the only mention is a comment). Without it the vanilla ocean map is used
  for coast finding, deltas and mouth fades; the in-world check without it passed.
- **Other mods' configs:** River Features writes only `riverfeatures-lakes.json` (with `.bak`/`.tmp` during repair or
  `set`), `riverfeatures-rivers.json` and `riverfeatures-settings.txt`. Config files of
  the earlier standalone mods are not read any more (no longer migrated). The old `RiverSpawnChance` mirror into Rivers'
  `rivers.json` is removed; the key is ignored and `set` refuses it. Rivers' `maxNodes` and `lakeChance` are changed in
  memory only. Tests and `tools\package.ps1` write nothing outside the project folder.

### Next flight checklist (new world)

1. **Mods** (in the mod menu): Rivers 5.0.2 and River Features on; Continental Worlds as you choose.
2. **Startup log** (`server-main.log`): `plain lakes deferred`, `structure water veto active`, `terrain estimate bound`,
   `active: max river length 10000 ... delta chance 0.35`; no erosion line; no `[RiverFeatures]` errors. If the lakes
   file still had erosion keys: one `removed setting(s) dropped` line. `riverfeatures-settings.txt` lists no Erosion keys;
   `rivers.json`'s modified date has not changed.
3. **No erosion:** hillsides show no fresh gullies or shaved ledge lips.
4. **River length:** `/rffind river` after exploring: with *River length* 10000 the longest main stems reach several
   thousand blocks, the longest about 10000 to 11000 (higher settings rarely go further);
   `/rffind river 8000` and teleport to a mouth to follow one. In a second world with 5000,
   rivers are shorter but about as many river mouths appear.
5. **Deltas:** channels round, spaced, not tangled; shallow ones 1 to 3 blocks of water with a deeper groove; the river
   deepens gradually above the fork; no channel along the coast.
6. **Source lakes:** `/rffind lake`: varied shapes, land between them and other rivers; no plain lakes.
7. **Oxbows and traces:** `/rffind oxbow`, `/rffind trace`: on flat ground, not overlapping each other or other rivers.
8. **Tributaries:** no tributary hugging its main stem; forks still look normal.
9. **Dry rivers, swamps:** `/rffind dry-river`, `/rffind swamp`: as before.
10. **Structures:** ruins by a river bank are kept; none on islands or spits, none half in water.
11. **Shutdown log:** the two `this session` lines; deltas and "deltas with bank character" agree; the terrain line has
    no "eroded columns".

## Plain lakes deferred (2.0.0)

- Plain lakes (the standalone lakes of `riverfeatures-lakes.json`: `LakeChance`, `LakeMaxRadius`, shore easing, the
  river gap) are on hold behind a key, `PlainLakes` (default **false**). When false, no lake features are planned: the
  lake pass writes nothing, swamps have no lake keep-out, and the startup line says "plain lakes deferred".
- The *Lakes* world-creation dropdown (`twLakeChance`) is hidden; it is still read, so it comes back with the key.
- **Not affected:** source lakes (Rivers nodes, reshaped by the river network part, `riverfeatures-rivers.json`), swamps,
  deltas, oxbows, traces, dry rivers, the structure veto. `/rffind lake` finds source lakes.
- To bring plain lakes back: `"PlainLakes": true` in `riverfeatures-lakes.json` (or `/riverfeatures set PlainLakes true`,
  new chunks only), and restore the `twLakeChance` entry in `worldconfig.json` (the lang keys are still there).
- The lake code, its tests and all lake keys are unchanged, so nothing has to be rebuilt to re-enable them.

## Files, commands and world settings

- Config: `ModConfig/riverfeatures-rivers.json` (river network) and `ModConfig/riverfeatures-lakes.json` (terrain pass),
  explained key by key in `ModConfig/riverfeatures-settings.txt`.
- Commands: `/riverfeatures show | set | reload`, `/rffind <kind>`, `/rffind river [all] [min] [max]`, `/rfprobe [radius]`.
- Log prefix `[RiverFeatures]`; mod data keys `riverfeatures:*`.
- World settings: seven on the *Rivers & Lakes* tab (Swamps, Source lakes, Lake shapes, River length, River deltas,
  Oxbow lakes, Dry rivers). *Lakes* (plain lakes) is hidden while plain lakes are deferred.
- Two separate passes with separate duties, so nothing runs twice: the river-network part (`src/Rivers/`, namespace
  `RiverFeatures.Rivers`) rewrites Rivers' network before Rivers carves (Harmony patches on Rivers' own classes); the
  terrain pass (`src/`, namespace `RiverFeatures`) works on the finished terrain in the TerrainFeatures pass.

## Compatibility with Rivers and Rivers Extended

- **Rivers 5.0.2**: required. Its config (`rivers.json`) stays in charge of what it controls: `riverSpawnChance` (set it
  there; River Features never touches it), widths (`minSize`/`maxSize`), `lakeChance` (Rivers' own round lakes at river sources;
  0.1 by default, not the mini-lakes), valley shape (`valleyStrengthMin/Max`, `maxValleyWidth`), depth (`baseDepth`,
  `riverDepth`, `heightBoost`). River Features sets only `maxNodes` (from River length) and `lakeChance` (from Source lakes) in memory and patches the network in memory;
  `rivers.json` is never written.
- **Checked against Rivers' own `RiverConfig.cs`** (github.com/glutzer/Rivers-Mod, master):
  - `oceanThreshold` (30) is "blocks of submerged land", and Rivers compares it with ocean-map value x (map height / 256,
    integer division) x 0.33333. River Features first used 30 on the raw 0..255 ocean map, about 3 x too early (coast taken
    for sea). Fixed: the threshold is converted (`RiverNet.OceanMapThreshold`, 30 blocks = map value 90 at height 256 or 320);
    coast finding for deltas and the river-mouth fade use it.
  - `minSize` / `maxSize` are documented as "size of rivers, in width", but Rivers' sampling (SampleSelf, read from IL) counts
    a column as in the channel when its distance to the centre line is at most the size, so the channel is about 2 x size
    wide. `DryRiverMaxSize` 12 therefore means a dry channel about 24 blocks wide.
  - `riverPaddingBlocks` (128) keeps whole river nodes apart while Rivers grows its network. Deltas, oxbows and the delta's
    channels are added after that check, so they are kept clear of their own river only; they can come within 128 blocks of
    another river.
  - `lakeChance` (0.1): "Chance for a lake when nodes stop" (the source lakes), not the mini-lakes. `heightBoost` / `topFactor`
    and `baseDepth` / `riverDepth` are the carving ellipsoid that the dry-channel, shallow-delta and mouth caps work against.
- **Rivers Extended 0.1.0** (not in this setup): read from its DLL, it patches RiverRegion.GenerateZones (rain-based spawn),
  RiversApi.GetRiverSamplesForChunk (cross-region sampling), ModifyLandformWeights (valley falloff, shore smoothing), the
  carving top factor, and extends river mouths into estuaries. All of that works alongside River Features except the mouths:
  when `riversextended` is installed, River Features makes **no deltas and does not change mouth depth** (one log line) so
  the two never shape the same mouth. Everything else stays on.


# Development history (newest first)

The sections below record how 2.0.0 got here, flight test by flight test. Names of features since removed or renamed
(erosion, flow-through lakes, the earlier standalone mods and their file names) appear there as history only.

## Code audit fixes (after flight test 18)

High:
- **Water veto in a second world.** It was attached once per game process (static flag) to the first world's
  GenStructures; a second world loaded in the same session had no veto and no startup line. It is now attached to each
  world's own GenStructures and detached when the world closes.
- **Region rebuilds counted twice.** Rivers rebuilds a region's whole network every time it loads the region. The rebuild
  is still reshaped exactly as before (it must match), but only the first build of a region in a world adds to the
  shutdown counters, `/rffind` entries and the river index (`RiverHierarchySystem.repeatBuild`). "deltas" and "deltas with
  bank character" now agree, and the river index no longer grows during a long session.
- **Test boost removed** from the installed `riverfeatures-rivers.json`: `DeltaChance` 0.35, `DeltaMinRiverSize` 30.

Medium:
- **Oxbows keep clear of each other.** Each accepted oxbow is registered at once, so the next oxbow on the same river
  (a neighbouring bend, a mirrored or closer retry) keeps `OxbowRiverClearance` from it.
- **Branch spacing covers the fork itself.** A tributary is now tested at 25/50/75/100% of its first node, and also
  against its sibling at the same junction (usually the main stem's continuation): beyond the junction it is crowded when
  it runs within about 20Â° of the sibling (`RiverNet.MinForkDegrees`). A normal fork is not touched.
- **Island veto relaxed.** One wet side (a 4Ã—4 hut on a straight shore was about 40% of the ring and was vetoed) or two
  (a corner of the shore) are allowed; vetoed are 3 or 4 wet sides (spit, island), more than 60% of the ring wet, or more
  than 20% of the footprint wet at floor level. Lava no longer counts as water for any part of the veto.
- **Lake/river gap without seams.** The first gap decision for a lake (shrink, drop or keep) is kept for the session, so
  every chunk of that lake agrees even when more Rivers regions load in between. Still approximate: the index stores
  straight node lines, not Rivers' jittered segments, and a lake decided before a neighbouring region is built does not see
  that region's rivers.
- **Climate.** Dry rivers now read rain through `ClimateSources`, so a registered climate source drives them too. Docs
  corrected: `ClimateInfluence` 0 makes delta looks and trace kinds climate-blind (traces then come out about 41% dry
  channels, 26% backwaters, 32% pockets); dry rivers follow `DryRainLimit` regardless; at 0.7, very wet land (rain 0.8+)
  never rolls a dry delta.
- **`MouthShallowRange`** is documented as what it is: an on/off switch (above 0 = on); `MouthFadeBlocks` sets the length.

Cleanup:
- Per-world reset of every session counter and once-flag; the shutdown log reports "delta channels placed N of M" only for
  kept deltas and "river mouths without an ocean map" instead of "deltas skipped".
- Removed: the unused mouth-patch binding (`BindMouth`, `SampleRiverApi`, `CfgHeightBoost`, `CfgOceanThreshold`),
  `NearestNode`, the global `DryChannels.GrooveBank` (every groove sets its own), `StructureColumnsProtected`, the oxbow
  wrapper's unused `points` parameter, the river-lake mask and its log line. `/riverfeatures set RiverLakeChance` is refused
  with a note (it did nothing); the installed lakes file now holds 0.
- Stale doc comments from reverted experiments fixed (CoastDelta, Meander, Channel, MouthBankCap).
- Config: NaN fallbacks for `OxbowChance`, `OxbowMinBendDegrees`, `OxbowMinRiverSize` match the real defaults (0.65, 12,
  10); unknown keys in `riverfeatures-rivers.json` are kept, as in the lakes file; the "config upgraded" line no longer
  lists version-3 key names.
- Left as is: `RiverNet.Delta` (the 0.1.x seaward delta; tests use it for the chance rule), the dormant flow-through path
  in the lake planner (covered by tests, never active), and the oxbow source-lake check (`LakeRiverClearance / 2`, a no-op
  at the defaults but meaningful if `LakeRiverClearance` is raised above 40). Traces and oxbows are still checked against
  points of their own river, not its segments, and delta channels are not checked against other rivers.

## Flight test 18: sawtooth delta channels rounded

- **Cause** (-6939, -4359; 5046, -16668; -9989, -18836). Every meander piece of a delta channel (about 50 blocks) was its
  own `AddChannel` node. Rivers jitters each such node independently and never joins it to the next, so neighbouring
  pieces wobbled in different directions with a hard corner at every joint: a sawtooth instead of a bend.
- **Fix: rounding, not rollback.**
  - A channel is now ONE linked chain, built like oxbows (`AddChain`, now with a flow speed): one straight segment per
    piece, each segment the child of the one downstream, so Rivers' connector sampling rounds every joint.
  - Points are every ~32 blocks (was 50), so each joint turns by less than 30Â°.
  - If the meander leaves low ground, a barely curved path (cap 3%) is tried before the old single jittered node.
- **Roll back if needed:** in the delta block of `RiverHierarchySystem.SizesPostfix`, replace the meander path with the
  two-point path `{ c.End, c.Start }`. Every channel is then the single jittered node of flight test 17b, with no meander.

## Flight test 17b: tangled delta mouths (start points across the river rolled back)

- **What went wrong** (-6955, -4352 and -10313, 11290). Several retries made the mouth a tangle:
  - starts across the river's width (up to 80% of its half-width off the centre line);
  - smaller-angle retries (down to 14Â°, kept only 8Â° apart from the same start);
  - the meander, which could push neighbouring channels into and across each other.

  The result was nearly parallel channels crammed into the mouth, crossing and overlapping, with flooded patches between
  them.
- **Rolled back:** starts across the river and the 16b/17 angle retries. Every channel starts at the apex centre line
  again (the start before item 4).
- **Spacing:** every channel keeps at least `RiverNet.MinBranchSeparation` (12Â°) from every other channel and from the
  main stem's own course to the sea. A channel that fails at its rolled angle gets ONE alternative at 0.75 Ã— that angle,
  only if it still keeps that spacing; otherwise it is dropped, never squeezed in.
- **Meander bounded by spacing:** each channel's bend is capped at 0.22 Ã— sin(angle to its nearest neighbour or the main
  stem), at most 12% of its length (`RiverNet.MeanderCap`). Neighbours bending toward each other stay apart. Channels
  with room around them keep the full meander, the natural look of 10292, 10887.
- **Kept:** meandering channels, the 1 to 3 block plain, per-channel grooves, the shallow rise along the river's path,
  and the ruin veto.

## Flight test 17: ruins on islands, canal-straight channels, too-long shallow rivers, start points, plain depth

- **BetterRuins in shallow water** (-1500, 144, 11749).
  - BetterRuins is content-only: its ruins go through the game's GenStructures, so River Features' water veto ran on
    them. It only looked for water 1 to 3 blocks ABOVE the floor; that house stood one block above the water on a tiny
    island.
  - `StructureGuard.IslandVeto` now also vetoes a surface structure when more than 20% of its footprint is water at floor
    level (half-submerged), or when a 6-block band around it is wet at the floor or one block below (an
    island or a spit). *Threshold changed by the code audit: wet on 3+ sides or over 60% of the band.*
  - Water on one side (a riverbank or shore) is still allowed. Structures meant for water (`shallow`, `underwater`) and
    underground ones are exempt as before.
- **Odd delta at -18246, -18182 / -18280, -18235.** Each distributary was ONE straight Rivers node from its fork to the
  sea. Rivers only jitters a node slightly, so on the map it read as a canal ruled straight across the land, and from above
  as a hard V.
  - Each channel is now a chain of connected pieces (about 50 blocks each; *32 and linked since flight test 18*) along a gently meandering path
    (`RiverNet.Meander`): 5 to 12% of its length sideways, 1 to 2 waves, tapered to zero at the fork and at the mouth.
  - It falls back to the straight line if the curve leaves low ground (`DeltaMaxRise`).
  - The bank character follows the pieces.
- **Deltas way too long (reverted and fixed).**
  - The fork points down the main stem (16b) are reverted.
  - The cause of the long shallow rivers was a bug in the flight-16 rise. It measured distance along the MOUTH's straight
    direction, so on a river that bends, nodes never got "past" the rise, and up to 16 nodes (kilometres) stayed shallow.
  - Each main-stem node now gets its own cap along its own direction, offset by its distance ALONG THE RIVER past the apex
    (`DryChannels.Info.T0`, `RiverNet.StemRise`). The rise ends 160 to 320 river blocks past the apex, as intended.
- **Start points across the river** (*superseded: rolled back in 17b*; replacing the 16b fork points). Each channel tries starts across the main stem at
  the apex: its centre (the old start), then 45% and 80% of its half-width toward its own side, then 45% toward the
  other. These combine with the smaller angles. All starts lie inside the river channel at the same distance up it.
- **Shallow depth 1 to 3.** The plain lies 0, 1 or 2 blocks below `ShallowDeltaDepth` (45 / 35 / 20%), per delta for the
  main stem and Â±1 around that per channel. The deep centre grooves stay as in 16b.
- **Delta start point (apex): unchanged.** The criteria search (coast on the ocean map, then the apex distance up the
  river's own path) is sound. The real problems were the single fork and the rise measurement, both fixed above.

## Flight test 16b: 1-block shallow deltas, more branches, narrower fingers

- **No 1-block shallow deltas (my side effect, not chance).**
  - Flight test 16 added the 1 to 5 extra blocks to the shallow CAP, which is the whole channel and the plain:
    `(heightBoost + ShallowDeltaDepth + extra) / (map height âˆ’ sea)`. Every shallow delta became 3 to 7 blocks deep.
  - The cap is back at `ShallowDeltaDepth`, which gives the 1-block plain.
  - The extra 1 to 5 blocks now go into each channel's own centre groove (`DryChannels.Info.GrooveBank`, per channel:
    `ShallowDeltaGrooveDepth + extra`). That includes the main stem's rise.
- **Too wide 1-block groove along the fingers** (-7044, -4433 and -10155, -18788). That was the capped shallow channel
  body itself, 1.2 Ã— the width share (half-width about 24, so about 50 blocks wide), 1 block deeper than the plain around
  it. It is now 0.6 Ã— (about 24 blocks wide), so the deep groove in the middle reads as the channel. Change it back in
  `RiverNet.CoastDelta` if the old flats are preferred.
- **Never more than 3 branches.** The last session placed 315 channels for 167 deltas, 1.9 per delta. Two things held
  the count down:
  1. The roll: carving deltas rolled `DeltaMinBranches..DeltaMaxBranches` (2 to 4) plus or minus 1, and small deltas
     always 2.
  2. Placement: each rolled channel had ONE try, from the apex at its rolled angle. Outer channels leave Rivers' flat
     valley soonest, so the height check (`DeltaMaxRise` 6 blocks, banks 12 blocks out) or the run-to-sea limit dropped
     most of them.

  What changed:
  - A quarter of carving deltas get 1 to 3 more channels, and small deltas sometimes 3.
  - *Superseded (reverted in 17, angle retries reduced in 17b):* every channel tried up to 3 fork points along the main stem (the apex, then 35% and 60% of the way down to the
    coast, where the delta plain is flattest) and, at each, up to 3 angles (rolled, Ã—0.7, Ã—0.45, at least 14Â°), staying 8Â°
    from channels already on that side. The first try is exactly the old one.
  - The shutdown "stage 2" line reports "delta channels placed N of M rolled".
- **Delta start point (apex): left as is.** The apex is found by walking up the main stem from the mouth to the coast
  (game ocean map, Rivers' oceanThreshold), then the rolled apex distance further along the stem's own path. It sits on
  the main stem's centre line, so every channel joins the river. That is sound within its checks. The one real weakness,
  every channel forking at that single point, is what the fork points above address.
- Delta chances are unchanged.

## Flight test 16: shallow deltas, deltas along the coast, lake and branch spacing

- **Shallow delta fingers.** The count now runs from 1 to 8 per delta, mostly few, with more for bigger and more branched
  deltas (`1 + floor(u^1.25 Ã— 6 Ã— (0.6 + 0.8 branching) Ã— âˆšscale)`). Before it was usually 3. A lone finger may leave on
  either side.
- **Shallow delta depth.** Each delta is 1 to 5 blocks deeper than before, and each of its channels varies by Â±1 around
  that, on top of `ShallowDeltaDepth` and the groove.
- **Gradual rise** (-10058, -19739). The shallow-to-full-depth rise only covered the first 4 main-stem nodes from the mouth,
  which lies out at sea. When the apex was further inland, those nodes ended before the rise did, and the next node was
  carved to full depth at once. Now the rise runs 160 to 320 blocks (per delta) past the apex, with riffles along it (1 to 3
  waves, amplitude up to 0.3 of the rise, zero at both ends). It covers every main-stem node up to the end of the rise.
- **Deltas spreading along the coast** (-17511, 30106). A distributary could leave the apex at up to 85Â° from the sea
  direction and search up to 3 Ã— the apex distance for the sea. Big deltas reach about 540 blocks inland, so a channel
  could skim along the shore for over a kilometre, and the shallow delta plain and its bank character followed it.
  - The angle is now at most 65Â°.
  - The sea must be reached within 1.4 Ã— the straight distance at that angle (at most 2 Ã— the apex distance).
  - A channel that runs along the coast instead is dropped.
- **Lakes beside rivers** (-2081, 9225). Source lakes held their 32 blocks: the log shows 380 fit as planned and none were
  removed. The nearest source lake was about 2000 blocks from that spot, so this was most likely a lake from `lakes.json`.
  Those only stayed `RiverRadius` (10) blocks from river water, a thin strip.
  - Every Rivers channel (with deltas, oxbows, traces and source lakes) now goes into a world river index (`RiverIndex`)
    when Rivers builds a region.
  - A lake that would come closer than `LakeRiverGap` (24 blocks, `riverfeatures-lakes.json`) to a channel shrinks, or is
    dropped below its minimum size.
  - The decision is made per lake from world data, so every chunk agrees.
- **Tributaries beside the main river.** This comes from base Rivers: it keeps whole nodes `riverPaddingBlocks` apart only
  while growing, and only from the node it grows from, so a branch can run right beside its own main stem or another
  branch. River Features now cuts a tributary back (the crowded node and everything upstream of it) where its middle or
  upstream end comes within `BranchMinSpacing` (32 blocks of land, `riverfeatures-rivers.json`, 0 = off) of a channel it
  is not joined to.
  - The main stem is never cut.
  - Of two crowded branches, the more minor one goes.
  - The shutdown "stage 2" line counts the nodes cut.
- **Undercut cliffs** (-17368, 29780): left alone. They come from Rivers' own carving; Rivers Extended is not installed.

## Stage 2: delta character, climate, smarter oxbows, traces of the past

This stage targets the plain game world generator with Rivers. It uses no code from Watersheds and none of its flow
simulation. Everything is added through River Features' existing hooks on Rivers' river network, plus one surface pass per
chunk.

**Climate** (`Rivers/Climate.cs`)
- The default source is the game's climate map layer (`GenMaps.climateGen`): rain from the green channel, temperature from
  the red channel through the game's own conversion. A climate mod that replaces that layer is followed automatically.
- A mod with its own climate model can call `ClimateSources.Register(IClimateSource)`.
- A source that fails or returns unknown values only makes the bias neutral; it never stops world generation.
- `ClimateInfluence` (0 to 1, default 0.7) sets how strongly the climate counts. At 0 generation ignores climate.

**Delta traits** (`Rivers/DeltaTraits.cs`)
- Each delta rolls five parameters instead of picking one of six fixed types: scale (`DeltaScaleMin` to `DeltaScaleMax`),
  bank material (sand, mud, gravel, rock), moisture, vegetation and branching.
- The look is classified from those parameters:
  - **dry:** moisture below 0.22; every distributary is a dry, gravelled channel;
  - **intermittent:** moisture below 0.42; some channels are dry, the rest shallow;
  - **rocky:** rock banks, or gravel banks with few plants;
  - **overgrown:** dense plants;
  - **marshy:** mud banks;
  - **sandy:** everything else.
- The climate leans the roll softly:
  - drier land gives drier deltas;
  - plants follow moisture and warmth;
  - sand likes warm, dry land, mud likes wet land, gravel and rock like cold land.
- Every look still occurs everywhere. The shape (small, carving or shallow, as before) is a separate axis.
- Scale stretches the shape: apex distance times scale, channel width times the square root of scale, the spread, and plus
  or minus one branch. At scale 1 the delta is exactly as before.
- **Bank character** (`DeltaSurface.cs`, chunk pass): on low dry ground near a delta's channels, the top block becomes the
  bank material and plants grow, both thinning out with distance and patchy by noise. Heights are never changed, so there is
  no trench, no step and no chunk-seam effect. Columns used by lakes, near structures or holding water are left alone, and
  the columns it changes are kept from swamps. Turn it off with `DeltaSurface: false`.
- Rivers rebuilds each region's network every time it loads that region (only the zone map is cached in `RiverCache`), so
  the deltas are registered again before any chunk of the region is generated, also after a restart.

**Oxbows**
- Each rolled oxbow tries up to five placements: as rolled, smaller, mirrored, and closer to the river.
- It keeps the first one that passes every check:
  - it lies inside the region;
  - the ground is no steeper than `OxbowMaxRelief`;
  - it stays `OxbowRiverClearance` (20) from every other river's channel and lake in the region;
  - it stays half of `LakeRiverClearance` from source lakes;
  - it stays clear of this river's deltas.
- It is left out only when no placement fits. The shutdown log counts oxbows "placed at another spot" and those with "no
  spot".

**Traces of the past** (`Rivers/Traces.cs`): extra Rivers nodes, so Rivers carves and smooths them like any channel.
- **Dry side channel:** an old course beside the river, gravelled, with its floor above the sea.
- **Backwater:** a still, dead-end arm joined to the river.
- **Floodplain pocket:** a small still pond near the river.

Dry climates lean to dry channels, wet climates to backwaters and pockets. Every trace must pass these checks:
- it keeps `TraceClearance` (24) from other channels, lakes, oxbows, deltas and earlier traces;
- it keeps at least 10 blocks from its own river, except a backwater at its join;
- it lies on flat ground (`OxbowMaxRelief`);
- its river is not a dry river.

`TraceChance` (0.25 per eligible node) controls how many there are.

**`/rffind`**
- Deltas are logged as `delta-<shape>-<look>`, and a query matches by words. `/rffind delta`, `delta-shallow`,
  `delta-marshy` and `delta-carving-dry` all work.
- New kinds: `trace`, `trace-channel`, `trace-backwater`, `trace-pocket`.
- Aliases: `traces`, `swampy` (marshy deltas), `dry-delta`.
- `/rffind kinds` lists every kind logged this session with counts.

**Config** (`riverfeatures-rivers.json`, version 8): `ClimateInfluence`, `DeltaScaleMin`, `DeltaScaleMax`, `DeltaSurface`,
`TraceChance`, `TraceClearance`, `OxbowRiverClearance`. Nothing was added to the world-creation screen.

**Shutdown log:** a "stage 2 this session" line with deltas by look, dry delta channels, deltas with bank character,
traces by kind, and the climate source in use.

**Not verified in game yet:**
- the block codes for the banks (`sand-<rock>`, `gravel-<rock>`, `rock-<rock>`, `muddygravel`, `tallgrass-*`); a missing
  code falls back or is skipped;
- how often the 20-block oxbow clearance turns placements away, since rivers sized later in the region count at Rivers'
  maximum width.

## Flight test 15: stepped banks around lakes (-1210,150,5268)

**What the probe showed.** This was not a chunk-seam problem: no mismatch was logged for that chunk. The lake had joined a
vanilla pond at the same water level. The pond columns are the `muddygravel` columns marked "RF water no" and decided
`Leak`.

**The cause.** Shore shaving cut every dry bank within 3 blocks of the water down to an exact staircase (level + distance âˆ’ 1).
On terraced ground with straight shorelines, that drew three straight steps parallel to the shore, so the lake looked dug
into a stepped pit.

**The fix.** Each bank column now rises 1.0 to 2.2 blocks per block of distance, plus a 0 to 1.4 block offset. Both values
come from fine noise at the column's world position (`LakePass.ShaveNoiseAt`, about 5 blocks), so the result is the same in
every chunk. The shave target can only rise: no column is cut deeper than before, every water column keeps its wall, and
which columns flood is unchanged. Without the noise (tests), the exact staircase stays.

**Seam mismatches in this flight.** 31 log lines covered 619 of 18 423 lake columns (3.4%). Most were the accepted cases:
- 40 sample columns where a structure was placed after the neighbouring chunk had already decided;
- cascades from the small-lake and protection rules;
- 3 cave and 3 river-water cases.

## Flight test 11 changes (dry rivers in /rffind, rivulets in shallow deltas, delta shares)

- `/rffind dry-river` (also `river-dry`, `wadi`): one entry per dry river, at the middle of its most downstream dry node.
- Shallow deltas get rivulets: along each delta channel (and the shallow last stretch of the river) a narrow, round-bottomed
  groove, half-width 1.5 to 3.5 blocks, up to `ShallowDeltaGrooveDepth` (3) blocks below the flat delta floor (0 = off).
  The column's distance from the channel centre comes from Rivers' own elliptical bank profile,
  `max(sqrt(size) x riverDepth, baseDepth) x sqrt(1 - (d/size)^2)`.
- Delta types default to a third each: `DeltaSmallShare` 0.334, `DeltaShallowShare` 0.333 (carving gets the rest). Files
  still holding the old 0.35 / 0.3 are moved over (config version 6).

### Square lake pits (-2428,-8368)

/rfprobe showed a lake in one chunk column dug 4 deep right up to its border, while the neighbour chunk column left its
last 3 columns dry: the two planners decided the border columns differently, so each lake was cut off at the chunk line.
- Mitigation: the shore ramp (and bed smoothing) now treats columns of an already FINISHED neighbour as they really are
  (`LakeWorld.Finished`: dry, or holding this mod's water), so a lake never drops to full depth against ground that stayed
  dry; the border gets the normal 1, 2, 3 ... shore.
- Diagnosis: every chunk column with lakes stores its view of the -4..35 window (decision, height read, level) in mod data
  (`riverfeatures:lakeview`); a later neighbour logs `lake seam mismatch` lines naming both decisions, so the input that
  differs can be found. /rfprobe shows each column's final lake decision (None = flooded, Ocean, Leak, Bank, Cave ...).

Cause, from 41 `lake seam mismatch` lines (4804 columns in one flight), two order dependences:
- "earlier Outside, later None" (and level / RiverWater differences): vanilla ponds (GenPonds, TerrainFeatures pass, order
  0.4) appear in a chunk only when it reaches that pass, so a chunk generated later wrapped a "river-side" lake around a
  pond its finished neighbour never saw. Rivers' water lies at sea level, so lakes now take their level only from water at
  most one block above the sea; ponds still count as existing water. River-side lakes become rarer (most were pond-side).
- "earlier None, later Structure": ruins appear in the region lists over time. Lakes now keep RuinMargin only from story
  structures (known in advance) and block only the footprint of other structures (later ones are vetoed from water anyway).

### Flight test 14: stricter river clearance for source lakes

Rivers generates ALL rivers of a region before it sizes any of them and adds their lakes (GenerateZones IL), so every
channel is known when a lake is shaped. Three gaps let lakes crowd rivers (299,2212):
- the join exemption covered ANY channel passing near the source; now only the lake's own river (Obstacle.Own) is exempt
  there, other rivers are checked everywhere, also right at the source;
- rivers not sized yet (later in the region) read as width 0; they now count at Rivers' maxSize;
- `LakeRiverClearance` 14 -> 32 (config version 7 moves an unchanged 14).
If even a small pond at the source would crowd another river, Rivers' lake is removed (the source stays a plain river end).
Oxbows of rivers sized later keep half the clearance from existing source lakes. Shutdown log: "... small pond/removed",
"oxbow lakes N (... beside a source lake M)".

### Flight test 13: procedural source lakes, clearance from rivers

- Source lakes are generated procedurally (`LakeShapes.Procedural`) instead of picked from 5 templates: a curved spine of
  1..4 pieces (shared curvature plus jitter, S-bends), a width at every vertex, and 0..2 side lobes of 1..2 curved pieces.
  The names (oval, finger, winding, lobed, teardrop) are now only a classification for the log and /rffind.
- Clearance (`LakeRiverClearance`, 14 blocks): the lake must keep that much land between itself and every channel and lake
  of the region's rivers generated so far (RiverRegion.rivers), except where it joins its own river at the start. If the
  plan does not fit: up to 4 other procedural variants, then the plan shrunk (0.75, 0.55, 0.4), then a small pond at the
  source. Rivers generated later in the same region are not known yet and are not checked. Shutdown log: "clear of rivers
  as planned/other variant/shrunk/small pond".

### Flight test 12: source lake shapes, /rffind lake

- Rivers' `AddLake` builds every source lake as one straight node 100 blocks long widening from minSize/2 to 50..75: a
  teardrop each time. A postfix reshapes it (`LakeShapes`, picked by a hash of the position): teardrop 15 %, oval 20 %,
  finger (long, narrow, bent) 20 %, winding (3..4 bends) 23 %, lobed (main basin + 1..2 side lobes) 22 %. Extra pieces are
  still lake nodes chained through Rivers' connector sampling; lobes start inside the basin. `VariedSourceLakes` (rivers
  config) turns it off. Shutdown log: "source lakes N (teardrop/oval/finger/winding/lobed ...)".
- `/rffind lake` (or `lakes`): standalone lakes (one entry per 64-block cell) and source lakes; `/rffind lake-source`
  (or `-oval`, `-finger`, `-winding`, `-lobed`, `-teardrop`) for source lakes only.

### Source lakes replace flow-through lakes

- New world setting **Source lakes** (`rhSourceLakeChance`; config `SourceLakeChance` in riverfeatures-rivers.json, -1 =
  Rivers' own `lakeChance`, 0.1 by default). It sets Rivers' `lakeChance`, which Rivers rolls once per river end node and
  then adds a lake at that source. Dry river tips have no end flag, so they never get one.
- **River lakes / flow-through lakes removed** (world setting `twRiverLakeChance` gone; `RiverLakeChance` is read from old
  files but always 0): Rivers' water is at sea level, below every lake level this mod allows, so they only ever formed
  around vanilla ponds.

## Flight test 10 changes (deltas hard to find, mouths, swamps)

- **Swamps were not lost in the merge**: the flight log shows 56,074 swamp columns (13,757 pockets, 11,935 plants) and the
  *Swamps* setting is on the *Rivers & Lakes* tab. Since flight test 6 the peat is under a grass top, so swamps read as wet
  meadow with reeds and pools rather than brown bog; they need SwampMinRain (0.45) rainfall and ground near sea level.
- **River mouths were never changed**: the 0.1.4 mouth patch sat on `RiversApi.SampleRiver`, which Rivers' own terrain
  (`NewGenTerra.Generate`) never calls; it samples through `RiverGenerator.SampleRiver` with region-local coordinates
  (read from IL), hence `river-mouth columns made shallow 0`. The mouth fade now uses the same per-node cap as dry rivers
  (`RiverGenerator.SampleSelf` / `SampleConnector`, which that path does call): at network time the coast is found along the
  main stem (`RiverNet.FindCoast`), and the stem's cut fades from full depth at the coast to one ending at sea level
  `MouthFadeBlocks` (40) out at sea. Delta channels fade the same way over their last 24 blocks. The dead patch is removed.
- **`/rffind [kind]`** lists the nearest features placed this session: `delta` (or `delta-small`, `delta-carving`,
  `delta-shallow`), `oxbow`, `oxbow-dry`, `swamp`, with absolute and HUD coordinates and the distance. Deltas and oxbows are
  logged when Rivers builds a region's network, so they are listed before you get there.
- **Testing boost (revert later)**: in the installed `riverfeatures-rivers.json`, `DeltaChance` 0.35 -> 1.0 and
  `DeltaMinRiverSize` 30 -> 14 (every mouth of a river at least Rivers' minSize gets a delta).

## Flight test 9 changes

- **Mini-lakes**: a lake body (connected flooded columns) smaller than `LakeMinArea` (80 columns, scaled with the world's
  horizontal scale squared) that touches no existing water is dropped (`LakeSkip.Small`; shutdown line `mini-lake columns
  dropped N`). Bodies that fit a 31 x 31 box lie inside every owning chunk's window, so all chunks agree; larger ones stay.
- **Three delta types** (per mouth, deterministic): **small** (`DeltaSmallShare` 0.35: a simple mouth with two short,
  narrower channels from an apex ~0.4 x the usual distance inland), **carving** (the rest: 2..4 full-depth channels that cut
  through the coast), **shallow** (`DeltaShallowShare` 0.3: one channel more, 1.25 x the spread, 1.3 x the length, 1.2 x the
  width, flow 0.3, and every channel plus the river's last stretch at most `ShallowDeltaDepth` (2) blocks deep, deepening
  gently over 200 blocks upstream of the apex: a flat, slow delta plain with gentle banks). Shutdown line: `deltas by type
  small/carving/shallow`.

---

# Archive: terrain pass notes before 2.0.0 (superseded)

> **Historical, kept for reference only. Where anything below disagrees with the top of this README, the top is right.**
> This part was written while the terrain pass was a separate mod, before erosion was removed and plain lakes were
> deferred. It still describes erosion, flow-through lakes, plain lakes as active, old test counts, old log prefixes and
> old file and test-project names. For 2.0.0 use: the install line and status at the top, *Setting the numbers*,
> *Configuration reference (2.0.0)*, and the *Next flight checklist*.

Lakes, swamps and a few new landforms for Vintage Story **1.22.7**, as an add-on for the Rivers mod.
Server-side code mod (`"side": "Server"`, `"requiredOnClient": false`), built for one player's own worlds.

*Reference for the terrain pass, with its development history. File names, commands and the package below are the
current ones; the status paragraph and the flight-test sections are kept as written then.*

**STATUS: compiled against the 1.22.7 game DLLs, unit tests run (83 passing), six flight tests done (2026-11-22); the
flight test 6 changes below are not flown yet.**
Side `"Universal"` with `"requiredOnClient": false` since flight test 6: the code still runs on the server only, but the
client needs the mod's `worldconfig.json` and lang file for the world-creation settings.
The first compile needed one fix (`Func<,,>` is also declared by `Vintagestory.API.Common`, so `System.Func` is written out).
The flight test found swamp and lake walls, no flow-through lakes, circle-like lakes and ditch-like gullies; see
[Flight test round 1 changes](#flight-test-round-1-changes). Those fixes are unit tested but **not yet flown**. Read
[Unverified assumptions](#unverified-assumptions) and run the First-run verification checklist (archived below) again on a new world.

## Install

1. Put `riverfeatures_2.0.0.zip` (do not unpack it) into the game's `Mods` folder next to **Rivers** (5.0.2). The mod depends on modid `rivers`.
   The tested setup does **not** use Rivers Extended (early and rough per its author); the river network part of this mod
   adds deltas, oxbows and a river hierarchy instead.
2. **Remove TerraPrety** (it conflicts with Rivers; there is no compatibility work for it, Watersheds, Strata or other terrain replacers).
3. The release zip holds the compiled `RiverFeatures.dll` (with its `.pdb`), the assets, `modinfo.json`, `worldconfig.json` and this
   README. A source zip (`riverfeatures_2.0.0_src.zip`) is built beside it; put only ONE of the two in `Mods`.
4. Start a **new world** (or explore new territory). Nothing is changed in chunks that already exist.
5. Without Rivers the mod does not load at all: `rivers` is a hard dependency in `modinfo.json`.

First start writes `ModConfig/riverfeatures-lakes.json` (swamps, plain lakes) and `ModConfig/riverfeatures-rivers.json` (river network) with every setting.

### Build your own DLL

Needs the .NET SDK and the game installed.

1. Pick the target framework from the game: open `Vintagestory.runtimeconfig.json` in the game folder and read
   `runtimeOptions.framework.version` (or `tfm` if present), e.g. `8.0.x` means `net8.0`. `RiverFeatures.csproj` says `net10.0`,
   which is a guess; override it on the command line rather than guessing again.
2. Set `VINTAGE_STORY` to the game folder and build:
   `dotnet build RiverFeatures.csproj -c Release -p:TargetFramework=net8.0` (use the framework from step 1). The csproj
   `HintPath`s are guesses too; fix them if the game DLLs are not found.
3. Zip it: `.	oolspackage.ps1 -Game "<game folder>"` builds and writes `dist
iverfeatures_2.0.0.zip`
   Layout, all at the zip root except `assets/`:

   ```
   modinfo.json
   RiverFeatures.dll
   RiverFeatures.pdb      (only if it sits next to the dll)
   README.md
   assets/riverfeatures/patches/landforms.json
   assets/game/lang/worldconfig-en.json
   worldconfig.json
   ```

   Put that zip in `Mods` **instead of** the source zip (never both: the mod id would load twice).

## How it works

- **Landforms.** `assets/riverfeatures/patches/landforms.json` adds five variants to the game's landform list by JSON patch:
  `tw-rollinglowlands` (weight 1.0) and `tw-broadplains` (1.2) are gentle; `tw-steepridges` (0.6), `tw-cliffmassif` (0.4) and
  `tw-terracedhills` (0.7) are steep. The numbers are hand-picked and not playtested. Each has 9 octave entries (see UNVERIFIED).
- **Worldgen handler.** One chunk-column handler in the `TerrainFeatures` pass at **execute order 0.55**, i.e. after structure
  placement, so the ruin check sees structures that already exist.
- **Per column of chunks:** exclusions are computed first (structures, rivers, ocean), then the passes **plan** in order
  **lakes -> delta banks -> swamps** (swamps skip every column the earlier passes touched or keep out of), and then **one commit**
  writes the blocks. Planning never writes, so an exception leaves the column exactly as generated (one error is logged per session).
- **Seam-free by construction.** Every decision is a pure function of the world seed and world coordinates (lake features come
  from a deterministic world-coordinate cell grid, masks are noise at world coordinates, fades are exact distances to locked
  columns), so neighbouring chunks agree without sharing state. The exceptions are a few order dependences at chunk borders (see
  [Exclusions](#mountain-protection) and [Known limitations](#known-limitations)).
- **Height maps.** At commit the mod updates `WorldGenTerrainHeightMap` and `RainHeightMap` for changed columns so vegetation sits on the new
  ground, and it keeps the *original* heights of every chunk column it changed (a bounded in-memory cache of 2048 columns, with
  the map chunk's mod data as fallback, about 2 KB per changed column in the save). Neighbouring chunks read those original heights, so both
  sides of a border judge the same terrain. Neighbour heights come, first available wins, from: those stored originals; a copy of the
  neighbour's map-chunk `WorldGenTerrainHeightMap` (every neighbour exists at `TerrainFeatures`, vsapi `EnumWorldGenPass`); the
  request's `NeighbourTerrainHeight`; else unknown. The map copy is never stored as "original".
- **Loose plants.** Tall grass is placed by the block-layers pass one block above the terrain. Where a lake bank, a swamp cut or pocket,
  or a swamp cut lowers or floods a column, a plant there (plant material, or `Replaceable` >= 5000) is cleared so it does not float.
- **Lakes.** Features (centre, radius, water level, shape) come from a world grid with cell size 3 x `LakeMaxRadius`. A feature is on when
  the lake noise mask covers its centre. Each feature has its own outline: an ellipse (elongation 0.45..1, any direction) whose edge is
  pulled in by smooth noise (bays, lobes, peninsulas), always inside the radius disc. Water forms only on suitable ground inside that
  outline: closed natural basins below the level (bounding box at most 22 blocks with default settings) and flat ground at the level
  or up to two blocks above, next to such water or low ground. A column floods only if it *could* hold `LakeMinDepth` of water. The
  water depth is then measured from the **level**, not from the column's own ground: one block next to dry ground, 0.45 to 0.6 more per
  block inward (noise varies it, so depth contours wander), down to a bottom depth of 0.6 to 1 x `MaxCut` (noise again) and the outline
  fade. A natural floor that is already deeper stays, and a hump of natural floor more than one block per block above a deeper floor
  nearby is cut into a slope. Dry ground within 3 blocks of the water that stands well above it is eased down toward it (shore
  easing: water level + distance - 1, at most 3 blocks at the waterline, less further out, never on steep ground or highlands).
  Banks never exceed one block, and lakes only remove ground, never add it. The bed becomes sand or
  gravel of the local rock. Next to a Rivers channel a lake uses the surface of the **nearest** river column, and columns that hold river
  water are never touched.
- **River-side (flow-through) lakes: removed in flight test 11** (replaced by source lakes; the planner path is dormant). Where the river-lake noise mask (`RiverLakeChance`) covered low, flat ground within 9 blocks of river
  water, that ground flooded at the nearest river column's surface: the river ran into the lake and out again. On a river that stepped down,
  each step got its own pool.
- **Swamps.** Where the swamp mask covers ground that is low (within `MaxRiseAboveSea`), not steep, and where the climate rainfall is at least `SwampMinRain`,
  the surface is moved toward sea level + 1 by at most ceil(`MaxCut` / 2) blocks (3 at defaults), the weight fading to zero over
  max(`FadeMargin`, 3 x that cut) blocks (9 at defaults, at most 16), so the swamp never adds more than half a block per block of slope; `MaxFill` currently has no effect, see the table.
  The planned surfaces are then smoothed (a column more than one block per block above a lower surface within 3 blocks is
  lowered, never past its faded cut), so terrace risers inside a bog become one-block steps.
  About `SwampWaterCoverage` of it becomes sealed shallow water pockets; a pocket needs every neighbour's surface within one block above its water (no pits). Peat (or muddy gravel / low-fertility soil, first that exists) is laid
  only around pockets and in noise clumps of the swamp core; elsewhere the grassy top stays (moved down where the ground was cut). Marsh plants are placed.

## Mountain protection

Every terrain-changing step must pass these checks. Each rule is a named function in `src/TerrainGuard.cs` (pure, unit tested).

| Rule | Named check | What it guarantees | Where it is applied |
|---|---|---|---|
| Lakes: ground above `sea + MaxRiseAboveSea`, steeper than `MaxSlope`, at or below sea level, or too high to be cut to the water, is left alone | `TerrainGuard.LakeColumnAllowed` | Highlands and steep ground never get a lake; no lake at sea level; the reachable depth is at least `LakeMinDepth` | `WaterRules.LakeCandidate` (lake planner, every lake column) |
| Swamps: ground above `sea + MaxRiseAboveSea` or steeper than `MaxSlope` is left alone | `TerrainGuard.SwampColumnAllowed` | Mountains and steep slopes keep their terrain | `SwampPass` lock rule |
| Slope is measured from the column's own height map | `TerrainGuard.Slope` | Chunk-edge columns are judged like interior ones (one-sided difference not halved) | Lakes, swamps |
| Cuts are at most `MaxCut` per column and fade to zero toward the feature edge | `TerrainGuard.LimitCut` with `TerrainGuard.Fade` | No wall at a shoreline: the cut ramps from 0 to `MaxCut` over `FadeMargin` (smoothstep) | Lakes (`WaterRules.LakeCandidate`), swamps (`WaterRules.SwampFinal`, `SwampPass`) |
| Fills are at most `MaxFill` | `TerrainGuard.LimitFill` | Ground is never raised by more than `MaxFill` | Swamps (`WaterRules.SwampFinal`); lakes never fill |
| A zone whose rim varies by more than `MaxRimVariation` is rejected | `WaterRules.RimSpan` against `MaxRimVariation` (not in `TerrainGuard`) | Applied to the 5x5 heights actually available around the column, not to a whole basin | Lakes (`WaterRules.LakeCandidate`) and swamps (`SwampPass`) |

**Exclusions** (`ColumnExclusions`, computed per column in world coordinates): a column is excluded if **any** flag is set. When zones overlap the precedence decides which
reason is reported and which rule applies: **Structure (ruin plus `RuinMargin`) > River water > Ocean**. A ruin wins over everything (nothing near it is touched, not even to join a river); a river
wins over the ocean (a river-mouth column keeps its river level for lake matching, when the bed is above sea level; a mouth bed at or below sea level is excluded as Ocean, same effect).
Swamps also stay `LakeKeepOut` (6 blocks) away from every active lake disc and 10 blocks (`RiverRadius`, was 6) from river water, which encloses
the river-side lake zone and its banks. Water this mod places (lakes, swamp pockets) is recorded per chunk column (map-chunk mod data
`riverfeatures:water`, plus a session cache); the river scan of later neighbours ignores it, so it is never mistaken for a river.
The ocean buffer is horizontal (`OceanBuffer`) and a lake column must lie above sea level.
**Border order dependences:** at `TerrainFeatures` every neighbour chunk already exists at the same or a later pass (vsapi `EnumWorldGenPass`), so its terrain
and its river water are there to read. What can still differ by generation order at a chunk border: (1) **structures** a neighbour places later in its own
`TerrainFeatures` pass are not seen by the chunk generated first; (2) neighbour blocks **this mod already changed** (its water is recorded and ignored by the river scan; only chunks generated
before this change, or a neighbour whose record could not be read, still show it as river water); (3) neighbour **heights no source could supply** (stored originals, the neighbour's map
chunk, the request's `NeighbourTerrainHeight`) read as unknown and are treated as *not ocean, not river*. Excluding border strips "to be safe" would leave a
straight unmodified strip, which is worse. Story structures are placed later, in the `Vegetation` pass, and are **not** excluded at all.

## Flight test round 1 changes

Flight test of 2026-11-22 (seed offset 5, `LakeChance` 0.1): 6093 chunk columns, 119500 swamp columns, only 12411 lake columns, errors 0.
Root causes found and what changed:

| Seen in game | Root cause | Change |
|---|---|---|
| Swamps sunk with sheer, straight walls, often crossing at right angles (-1360,96; -3056,-292) | A chunk generated after its neighbour read the neighbour's **swamp pockets** (and lake water) as river water, locked 6 blocks around them and stayed uncut, while the neighbour had cut its side: a wall along the chunk border. Also: swamps cut the full `MaxCut` (5) with a smoothstep over 8 blocks (up to ~0.94 extra blocks per block). | Placed-water record per chunk column, ignored by the river scan. Swamp cut ceil(`MaxCut`/2) = 3, fade max(`FadeMargin`, 3 x cut) = 9. |
| Large brown terraced areas, scattered brown pits | Every swamp column got peat; at the faded edge single columns got peat by a per-column hash. | Peat only around pockets and in noise clumps of the swamp core; elsewhere the grassy top stays, moved down where cut. |
| Lakes with sheer underwater walls 3 to 6 blocks high (-4796,2237; -2194,-238) | Every flooded column was carved to `level - MaxCut x fade`, the fade measured from the disc edge, not from the actual shoreline. Right beside dry ground the bed was already 3 to 5 deep. | Shore ramp: water depth = distance to the nearest dry column (1, 2, 3 ...) up to the carve depth. Which columns flood is unchanged. |
| No flow-through lakes | (1) Slope and rim were measured against the **river bed**, so every bank beside a Rivers channel looked steeper than `MaxSlope`. (2) The lake level next to a river was the **highest** water within 6 blocks, which on a stepped river matches none of the touching river columns. (3) Lakes only formed where a disc happened to cross a river. | Slope and rim see a water column at its surface. Level = nearest river column. New river-side lake zone (`RiverLakeChance`) along every river. |
| Lakes look like circles | Discs. | Ellipse + noise outline per feature, inside the disc; cell 3 x radius; the `LakeChance` ceiling is measured from the shapes at startup. |
| Gullies read as dug ditches and trenches across slopes | Ridged noise to the 4th power (one-block lines), in any direction. | Squared ridge (wider V), and an alignment factor so gullies run downhill. |

Ruling changes (everything else is unchanged): lake water is no longer at least `LakeMinDepth` in **every** column (a column still floods only
if it can reach that depth; the shore columns are shallower by design); swamps use half of `MaxCut` and a wider fade; `RiverRadius` is 10 (was 6) and
`LakeKeepOut` is a fixed 6; the feature cell is 3 x `LakeMaxRadius` (was 4 x); one new setting, `RiverLakeChance`.
New lake shapes and the new rules make chunks generated after the update disagree with older ones at their border: **start a new world**.

## Flight test 8b: lake water was in the solid layer (found with /twprobe)

`/twprobe 3` at 42,142,208 (fresh world, seed 12345) showed every TerraWaters water column with `water-still-7` as its SOLID
block and no fluid, next to Rivers' water in the fluid layer: `IBlockAccessor.SetBlock(id, pos)` on the worldgen accessor put
water into the solid layer. The game draws faces between water in the two layers (the zigzag "water walls"), and Realistic
Water treats it oddly. Commit now writes liquids to the fluid layer (solid cleared), air to both layers, and solid blocks to
the solid layer (fluid cleared). Chunks generated before this fix keep their solid-layer water: check it in a new world.
`/twprobe [radius]` (controlserver) stays as a read-only diagnostic: ground block, water (fluid layer), empty cells below the
surface, TerraWaters flags, original height, TerraWaters' planned top/surface (`riverfeatures:plan` mod data), Rivers flow.

## Flight test 8 changes (Realistic Water: sealed lake water)

Seed 12345, height 320, at 40,141,209: dry pits below the water surface and stepped water. Realistic Water (installed in the
game folder's Mods) replaces the game's water behaviour with finite water that flows toward any lower open path within
`MaxPathDistance` (15) blocks and lowers its level as it goes. TerraWaters places lakes and pockets in the TerrainFeatures pass,
after the game's caves, so a lake can sit on a cave ceiling or touch a cave or overhang from the side; vanilla water just stands
there, Realistic Water drains through the gap. After every chunk's Commit, `WaterSeal` fills EMPTY blocks (no solid, no fluid)
directly under each column this mod flooded and beside its water (bed + 1 .. water surface) with the column's rock (or bed
block). Water and solid ground are never replaced; unloaded neighbour chunks are skipped. Rivers' own water is not touched (the
game's caves avoid it). Shutdown line: `blocks sealed beside or under placed water N`. UNVERIFIED: whether the pits at
40,141,209 came from a TerraWaters lake (gravel bed, lake shapes on the map) or from Rivers' channel; flying the same seed
without Realistic Water tells which.

## Flight test 6 changes (swamp walls, grass over peat, more lakes, world-creation settings)

- **Swamp wall at 1506,-3214.** The wall was straight and one chunk long, at the full swamp cut (3): one chunk cut its side of
  the border and the neighbour chunk did not. Each chunk plans the shared border from world data, so any input one of them
  could not read the same way made them disagree. Now:
  - every finished chunk column is marked (`riverfeatures:done` in the map chunk's mod data);
  - a swamp column within `cut + 1` blocks of a finished neighbour chunk is fitted to that neighbour's ACTUAL ground
    (`WaterRules.SeamFit`): never more than one block per block below it, and next to ground this mod lowered the own ground
    ramps down (grass moved down), by at most the swamp cut; natural steps are left alone;
  - the swamp cut is ceil(MaxCut / 3) = 2 (was ceil(MaxCut / 2) = 3), so the worst case is smaller too.
  The shutdown line counts `... fitted to finished neighbour chunks`.
- **Grass over peat.** Peat on land now lies under the column's own grassy top (or `soil-low-normal`); beside a water pocket
  the top is `peat-verysparse` (peat with a thin grass cover). Pocket beds stay plain peat.
- **More lakes.** `LakeChance` default 0.12 (was 0.05). The 3 x LakeMaxRadius grid covers only about 8% of the map with every
  lake active, so a higher chance now packs the lake features closer (2.75 / 2.5 / 2.25 x LakeMaxRadius, chosen at world start;
  about 15% at the densest). Swamp patch sizes keep the 3 x grid. Existing `terrainwaters.json` files keep their value: the
  installed one was changed from 0.1 to 0.12 by hand.
- **World-creation settings** (`worldconfig.json`, Customize world, own tab *Rivers & Lakes* since flight test 7): *Lakes*,
  *Swamps* and *River lakes*, each Default (the mod config) or Off / Rare ... Most; River Hierarchy adds *River length*, *River
  deltas* and *Oxbow lakes* to the same tab. A chosen value overrides the mod config for that world only (logged at start).
  Flight test 7: the labels showed raw keys because the game reads mod world-setting translations ONLY from
  `<mod>/assets/game/lang/worldconfig-<lang>.json` (TranslationService.PreLoadModWorldConfig, read from the 1.22.7 IL); dropdown
  entries are looked up as `worldconfig-<code>-<name>`. Both mods now ship that file, and a unit test checks every key.

## Flight test 4 changes (natural hollows, Rivers flow on still water)

Flight test 4 (42199 chunk columns): standalone lake columns had fallen to about 0.8 per chunk column (2.0 in flight test 1).

| Seen in game | Root cause | Change |
|---|---|---|
| Standalone lakes mostly gone | A lake feature had ONE random level (sea + 2 .. sea + MaxRise); a natural hollow flooded only when that level happened to lie between its floor and its rim. | Natural hollows now fill to their own **spill level**: the highest level of the lake band at which the closed region below it is accepted (inside an active lake outline, above sea level, at most 2 x `MaxCut` deep, no structure or ocean nearby, bounding box at most `BasinSpan`, at least `LakeMinDepth` deep somewhere). The natural floor is kept; slope and rim rules do not apply to a hollow's floor (nothing is cut). Flat ground still floods at the feature level as before. Chunk-consistent: every column of a hollow finds the same level. |
| Isolated pools of "fast-flowing" water (89,147,209; 37,152,204) | Rivers stores a flow-vector map per chunk (`flowVectors`, 32x32 X and Z) and its client draws ANY water in a column with a non-zero vector as flowing (and pushes boats and swimmers). Stretches of a Rivers channel that ended up as separate pools (their muddy-gravel bed shows they are Rivers water) kept the channel's flow; still water placed in the flow zone inherited it. | The flow is cleared (still water) on every column where TerraWaters places water and on every small enclosed pool of existing water (at most 400 columns and 28 blocks across, not reaching the edge of the scanned area; a river channel never qualifies). New shutdown counter: "Rivers flow cleared". |

## Flight test 3 changes (Rivers lakes, chasms, Continental Worlds)

Flight test 3 (25109 chunk columns, errors 0): 102230 lake columns, of which 76300 river-side. Findings and changes:

| Seen in game | Root cause | Change |
|---|---|---|
| Submerged mounds, flat plateaus with a deep moat, walls (-95,242; -189,280; 535,1971) | The flow-through rule joined **Rivers lakes** (Rivers has its own `lakeChance`), not only river channels: islands and shores of a Rivers lake were flooded at its level to TerraWaters' shallow depth, ending as a wall above the lake's deep basin. | A column joins river water only where at most 40% of the columns within 8 blocks already hold water (channels yes; Rivers lakes, their islands and very wide rivers no). Where a lake does join a channel, its bed slopes down into the channel bed (one block per block) instead of stopping as a wall. |
| Stepped funnel pond (1455,2798) | The shore ramp deepened one block per block, and a 5-block noise octave raised single columns. | Shore slope 0.45..0.6 blocks per block (small ponds stay shallow); the 5-block octave removed (depth noise at about 28 and 14 blocks). |
| Frozen rivers and lakes not seen (log: "Liquid found below the terrain height map") | Rivers caps cold water with lake ice; the height map points at the ice and the river scan only looked up from it. | Water one or two blocks under the surface block counts as river water. |
| Water-filled chasms 20..40 blocks below sea level (-360,99,301; 967,118,1874) | TerraWaters' own landforms: `tw-steepridges` and `tw-cliffmassif` started their height keys at 0.25 / 0.30 of world height, so their valleys reached ~58 blocks below sea level and filled with water. | Both now start at 0.435 (just above sea level 0.43), like vanilla landforms. Lakes, swamps and erosion never went below sea level; their limits are unchanged. |

**Continental Worlds 1.1.2** (code mod, checked by reflection): it Harmony-patches only the ocean-map generator (`GetOceanMapGen`) and is
configured in `continental-world.json`. TerraWaters never reads the ocean map: the sea is taken from terrain heights and the world's sea level,
so continents and islands from Continental Worlds are seen as they are generated. No pass-order or height conflict. Rivers reads
the ocean map (river starts) and follow Continental Worlds' coasts.

## Structures, traders and ruin mods (flight test 2.5)

Checked against the 1.22.7 game DLLs (VSEssentials / VSSurvivalMod, by reflection) and BetterRuins 0.6.4 (a content mod: `structures.json`,
`villages.json`, `storystructures.json`, no code):

| Generator | Pass / order | Seen by TerraWaters? |
|---|---|---|
| Ruins (`GenStructures`: vanilla and BetterRuins `structures.json`), villages (`villages.json`) | TerrainFeatures 0.3 | yes, same chunk: every schematic is in `GeneratedStructures`; kept `RuinMargin` away |
| Trader camps and other `postPass` structures (`GenStructuresPosPass`) | TerrainFeatures 0.5 | yes, same chunk, as above |
| Story structures (`GenStoryStructures`: vanilla and BetterRuins `storystructures.json`) | **Vegetation** 0.2, after TerraWaters | **now yes**: locations are fixed at world start |

What TerraWaters does about them:

- **Structures placed later by a neighbouring chunk** (the trader camp on a lake bed at -649,144): such a structure takes its height from the terrain
  height map, which over a lake is the lake **bed**, and the game rejects a site only when it overlaps an entry of `GeneratedStructures`. TerraWaters
  now registers its own footprints there: lake water and eased shores (`riverfeatures:lake`) and ground lowered by 2 or more (`riverfeatures:cut`),
  one box per 8x8 tile, in the own region and the regions of the 8 neighbouring chunks. Trader camps, ruins and villages placed afterwards
  refuse to overlap them. TerraWaters ignores its own entries.
- **Story structures:** their footprints (`GenStoryStructures.Structures`) are protected like any structure (footprint + `RuinMargin`), and their
  "pond" keep-out (`skipGenerationCategories`, 150..200 blocks for every BetterRuins story structure) keeps lakes, flow-through lakes and swamps
  out, through the same query vanilla ponds use.
- **Water veto for all structures:** the camp at -649,144 was not on TerraWaters water. Its floor is about 10 blocks below sea level (138), where
  TerraWaters never changes ground, in a basin of the Rivers mod (Rivers has its own `lakeChance`, and makes its river and lake water in the Terrain
  pass). Vanilla surface placement only tests the centre and four corners one block above the ground for liquid. TerraWaters now subscribes to the
  game's own placement hook (`GenStructures.OnPreventSchematicPlaceAt`) and refuses a surface structure when its footprint, or a 2-block ring around
  it, holds liquid 1 to 3 blocks above its floor. `trader-shallow` (meant for shallow water) and underground structures are exempt. Villages use a
  different check and are not covered by this veto.
- The startup log says how many story locations are protected and that the water veto is active; the shutdown summary counts the footprints
  registered and the placements vetoed.

Limits: a structure placed later by a neighbour can still sit next to (not on) a lake or over ground lowered by 1 block; a lake basin that crosses
into a chunk where a structure is placed later is still the known chunk-order case (see Border order dependences); worlds generated before this
change have no footprints. Swamp pockets are not registered, so swamp trader camps (`trader-shallow`) can still spawn in TerraWaters swamps.

## Flight test round 2 changes

Second flight test (320-high world, so `MaxCut` 6): swamps and lakes much better; three spots remained.

| Seen in game | Root cause | Change |
|---|---|---|
| Ring-shaped shelf inside a lake, stepped shore (-475,-858) | The carve was capped at `MaxCut` below each column's **own** ground, so terraced land left terraced shelves under water; natural basin floors next to carved humps gave multi-block rings; the shore ramp drew bands exactly parallel to the shore; banks of natural basins stood 2..3 blocks above the water. | Depth measured from the level (terrace-independent), noisy shore slope (0.8..1) and bottom depth (0.6..1 x `MaxCut`), bed smoothing (one block per block, lowering only), shore easing within 3 blocks of water. |
| Pits and 2..3 block steps inside swamps (938,1191; 1296,1973) | The swamp cut followed each column's own height, keeping terrace risers; pockets were allowed with neighbours any height above the water, giving water at the bottom of holes. | Swamp surface smoothing (one block per block within 3, never past the faded cut); a pocket needs every neighbour within one block above its water. |

Ruling change: `MaxCut` for lakes is now the depth below the water level (bank columns can lose up to `MaxCut` + 2 at defaults). The river scan
reaches 30 blocks beyond the chunk (was 27) for the wider swamp window. New shutdown counter: shore columns eased.

## Known limitations

- **Lakes are rarer than `LakeChance` suggests.** Only natural closed basins and flat ground at the lake level flood; water covers well under the outline area. Tune with
  `MaxRiseAboveSea`, `MaxCut`, `LakeMinDepth`. The achievable share is printed as the ceiling in the startup line.
- **Flow-through lakes need low banks.** A river-side lake floods ground at most two blocks above the river surface (`MaxCut` - `LakeMinDepth`) with slope at most `MaxSlope`;
  rivers in deep valleys get none. A river with a rising or falling surface gets a chain of pools, not one lake.
- **Chunk-order dependence.** Structures placed later by a neighbour, neighbour blocks this mod already changed, and neighbour heights no source could supply
  (see Border order dependences above) can make the two sides of a border differ; a step of up to `MaxCut - 1` blocks is possible at such a border, and a lake
  may touch a coast there.
- **Story structures** are protected by their fixed locations (see [Structures, traders and ruin mods](#structures-traders-and-ruin-mods-flight-test-25)); a story structure added by a mod that does not register with `GenStoryStructures` is not.
- **Rivers' own banks** are Rivers' work; this mod cannot change their smoothing.
- A lake basin that spans a chunk border can end up half dry if the handler throws in one of the two chunks.
- Basin columns skip the cave check; a swamp's pocket water or lake water is seen as river water by later neighbours (kept safe by the keep-out rule).
- Nothing is retroactive; changing any worldgen setting mid-world (see Seam warnings) leaves seams against older chunks.
- **Performance is unmeasured**: roughly 1156 `GetClimateAt` calls per swamp chunk, a lazy water scan of up to about 6000 columns, and up to about 100,000 `GetBlock` calls in the worst swamp or basin chunk. Watch chunk generation time.
- Default values are untested tuning numbers.

## Unverified assumptions

Nothing was run, so every game API use is an assumption until the checklist below passes. Flagged in code comments too.
Symbol **names** were checked against the 1.22.7 API history (vsapi `63d33f7`, vssurvivalmod `849fa8c`), **not** against the newer 1.23 pre-release
sources; signatures and behaviour were not checked by any tool.

**Build and tooling**
- csproj `HintPath`s (`$(VINTAGE_STORY)/VintagestoryAPI.dll`, `Mods/VSSurvivalMod.dll`, `Mods/VSEssentials.dll`, `Lib/Newtonsoft.Json.dll`) are guesses; set the `VINTAGE_STORY` environment variable to the game folder and fix paths if the build cannot find them.
- Test package versions (xunit 2.9.2, xunit.runner.visualstudio 2.8.2) and the `net10.0` target framework are unverified against the game's runtime and your SDK
  (see [Build your own DLL](#build-your-own-dll) for choosing the framework).
- The game's **in-process source compiler**: whether it accepts this C# (records, `init`, `with`, switch expressions) and which language version it uses. If the
  mod does not load from the source zip, use the DLL path.
- A code mod as a zip with the DLL and `modinfo.json` at its root is the usual layout; not tested with this mod.
- `File.Move(string, string, bool)` (atomic config save) and `File.Replace` exist in .NET Core 3.0+; their behaviour in the game's runtime is unverified.

**Landforms and world settings**
- 9 octave entries per landform follow TerraPrety's 1.22 data; the wiki example shows 10. If the game's `landforms.json` uses another length the patch will misbehave. The high-octave rules use indices 7 and 8. The key/threshold meaning below the first Y key is unverified.
- The world-config key `landformScale` is a worldgen setting in vssurvivalmod 1.22.7; it is read with `GetAsString` (stored type unverified) and parsed with a
  dot first, then the server's locale. Anything unparsable gives scale factor 1.
- The landcover and ocean world-creation settings (keys `landcover` and `oceanscale`, vssurvivalmod `Properties/AssemblyInfo.cs`) are **known but not used**: they
  change where land and ocean are, which the ocean rule already reads from the terrain heights.

**Worldgen API**
- Whether `NeighbourTerrainHeight` is populated in normal generation (it is declared next to `RequiresChunkBorderSmoothing` and may be filled only when border
  smoothing is needed), and its layout (assumed `ushort[8][]`, cardinal order N..NW, each 32x32, null if absent). The mod no longer depends on it: neighbour map
  chunks come first (`GetMapChunk` through the worldgen accessor, unverified from the worldgen thread). A one-time warning fires if a chunk has no neighbour heights from any source.
- Tall grass is placed by the block-layers pass (`Terrain`, order 0.4) at terrain height + 1 (vsapi `EnumWorldGenPass` doc); the mod clears it over lowered or
  flooded columns. Other loose blocks there with `Replaceable` >= 5000 are cleared too.
- `WorldGenTerrainHeightMap` semantics (top solid block; whether it points at a river bed or water surface at order 0.55), and whether the arrays can be written in place and are honoured by later passes; `RainHeightMap` semantics for lakes and pockets; whether `GetWorldgenBlockAccessor(true)` keeps the rain height map in step with `SetBlock`.
- Liquid detection (`IsLiquid()`, `LiquidCode` not starting with `lava`) for river water; any non-lava liquid counts as river.
- `GetClimateAt(..., WorldGenValues).Rainfall` is valid during `TerrainFeatures`; on failure columns count as dry and one warning is logged.
- Thread safety: mod data on map chunks (`GetMapChunk` / `GetModdata` from the worldgen thread), `NormalizedSimplexNoise.Noise` under parallel generation, and `BlockCodes.Resolve` called from the command thread on `set`/`reload`. A failed mod-data write is logged once.
- Logger brace handling: in 1.22.7 `Logger.Warning(string)` forwards the text as a format string, so every dynamic message is passed as an argument (`"{0}"`). How the
  closed logger implementation treats braces is unverified; constant messages without braces are passed directly.
- Handlers in one pass run in ascending `ExecuteOrder` (0.55 after structures); **ruin structures are present at order 0.55**. `Cuboidi.MaxX/MaxZ` are exclusive in 1.22.7
  (`Cuboidi.Contains` uses `<`); the mod treats them as inclusive, so the margin is one block wider on the +X/+Z sides (intended, safe).
- Neighbour map regions are read with `IBlockAccessor.GetMapRegion` / `RegionSize` on the worldgen accessor, as vanilla `GenStoryStructures` does.
- `GetChunkAtBlockPos` returns null for ungenerated neighbour chunks; `ICoreServerAPI.ChatCommands` optional word/all arguments arrive as null when omitted and `args[i]` is the string.

**Block codes** (no game asset JSON was available): `peat-none` (swamp surface; falls back to `muddygravel`, then `soil-low-normal`), `sand-<rock>` (lake bed on shallow shore; `gravel-<rock>` is the Rivers mod's pattern and used deeper, with the other as fallback),
`tallplant-coopersreed-land-normal-free` and `tallplant-tule-land-normal-free` (only the `harvested` state was seen in source, so the `normal` state is a guess), `forestfloor-*` soil check in erosion. Confirmed in source: `water-still-7`, `tallgrass-tall-free`, `muddygravel`, `soil-low-normal`. Missing codes are skipped and logged once, never an error.

**Rivers**
- The Rivers modid is assumed to be `rivers` (the `modinfo.json` dependency); if it differs the game refuses to load this mod.
- Beyond the key `riverSpawnChance` (seen as a string in `Rivers.dll`), the shape of `rivers.json` is unknown; the mod edits that one key and adds nothing.
- When Rivers reads `rivers.json` relative to this mod's `StartPre` (order -1.0) is unknown; the Rivers API distance query was not inspected, so rivers are found by an in-chunk water scan (radius 6, up to 24 blocks above the terrain height).

## Rechecking the API

`python3 tools/check_api.py` checks every game symbol the code uses by name. It defaults to 1.22.7 checkouts at `/tmp/vsapi-1.22.7` and
`/tmp/vssurvivalmod-1.22.7` (the script header has the clone and checkout commands: vsapi `63d33f7`, vssurvivalmod `849fa8c`); override with
`--vsapi <dir>` / `--survival <dir>` or `VSAPI_DIR` / `VSSURVIVALMOD_DIR`. It prints each tree's HEAD commit and warns if it is not a 1.22.7
checkout. For another game version, check out the matching "Updated to Version x.y.z" commits and rerun.

## First-run verification checklist (pre-2.0; superseded by the Next flight checklist at the top)

1. **Build and test** (needs the .NET SDK and the game folder in `VINTAGE_STORY`): `dotnet build RiverFeatures.csproj -p:TargetFramework=<from the game>` then
   `dotnet test tests/TerraWaters.Tests -p:TargetFramework=<same>`. Nothing has ever been compiled, so expect to fix compile errors and some test failures first;
   the tests were only hand-traced. Report the errors.
2. Install the zip as above with Rivers, remove TerraPrety, and start a **new world with a fixed seed** (write the seed down). If there are no `[TerraWaters]`
   lines at all or a compile error mentions the mod, switch to the DLL zip. Check the server log for these lines (numbers always use a dot, whatever the server's locale):
   - `[TerraWaters] worldgen ready: height ..., sea ..., lake radius 16..48 (cell 144), lake chance 0.05 (map share; ceiling about 0.07..0.12, about 0.4..0.7 of lake features active), swamp chance 0.15, swamp surface '...', N swamp plant(s).` (at 256 height; the swamp surface should be `peat-none` or a fallback, never `none`).
   - `[TerraWaters] river-side (flow-through) lakes: chance 0.35, reach 9 blocks from river water.`
   - `[TerraWaters] erosion: strength 0.5, max cut 4 (effective 4), min slope 0.5.`
   - (Removed in 2.0.0: the RiverSpawnChance mirror line; River Features no longer writes `rivers.json`.)
   - **No** warnings about: block codes not found, no worldgen block accessor, no neighbour terrain heights, GetClimateAt, height maps, original heights in the
     map chunk's mod data, unknown settings, "Liquid found below the terrain height map", or errors in a chunk column. Any of these, report them.
   - On shutdown: `[TerraWaters] generated this session: chunk columns N, lake columns ... (... river-side/flow-through, ... shore columns eased), swamp columns ... (... plants), loose plants cleared ..., eroded columns ...; ... errors 0`.
3. Fly around (creative or `/tp`) and look for, in order:
   - **Mountains next to rivers and lakes:** no eaten shoreline, no cut into hills or steep slopes, no vertical bank at a shore.
   - **A lake touching a coast:** it should stay `OceanBuffer` away from the sea; a touch at a chunk border is one of the known border order dependences (see Exclusions).
   - **A ruin next to a lake or river:** the ruin and `RuinMargin` around it unchanged.
   - **A swamp near hills:** no swamp on hillsides; plants on the new ground, not floating; water pockets sealed.
   - **Chunk-border seams:** walk along chunk borders and look for straight steps, strips of unchanged terrain, water walls.
   - **Floating trees or grass** over eroded slopes, lake banks or swamp pockets (tall grass should be cleared; trees follow the height map).
   - **Lake frequency:** if you see no lakes in a large area, report that (see limitations).
4. **Commands:** `/riverfeatures show`; `/riverfeatures set LakeChance 0.5` (reply should mention the ceiling and the effective share); `/riverfeatures set LakeChance lots` (must be rejected, nothing changed);
   edit the file into invalid JSON then `/riverfeatures reload` (must report not applied, old settings kept, file untouched); fix it and reload; `/riverfeatures set SeedOffset 5` (must warn about seams);
   add `"lakeChance": 0.2` to the file and reload (the reply's notes name it with "did you mean 'LakeChance'?", and the key is still in the file afterwards).
5. **Repeat testing with `/wgen regen`:** the game source (`vssurvivalmod`) calls it as `/wgen regen <integer>` (a radius in chunks), and `/wgen delr <range>` deletes chunks so they regenerate. I could not read the command's own
   implementation (it is in a closed game library), so the exact semantics in 1.22.7 are unverified: check with `/help wgen`. **Caution:** this mod keeps original heights in an in-memory cache and in the map chunk's mod data; whether a regen
   clears them is unknown, so repeated regen may leave stale data. For clean comparisons restart the world with the same seed, or delete the world folder and regenerate.
6. **Report back:** the seed; the full TerraWaters log lines (startup, shutdown summary, any warning or error); the first compile or test failures from step 1; the game version and the exact Rivers and Rivers Extended versions;
   screenshots with coordinates of anything from step 3 that looks wrong; the `landformScale` and world height you created the world with; and whether chunk generation felt slow.


---

# Archive: river network notes before 2.0.0 (superseded)

> **Historical, kept for reference only. Where anything below disagrees with the top of this README, the top is right.**
> This part was written while the river network was a separate mod (versions 0.1.x, before the merge). Its status, test
> counts, config file name, log lines and world settings are those of that time. For 2.0.0 use the top of this README
> and *Configuration reference (2.0.0)* (the current `riverfeatures-rivers.json` keys).

The river network part of River Features, for **Rivers 5.0.2** (Vintage Story 1.22.7). Changes only Rivers regions generated after it is installed:
**start a new world** (or explore far enough for new 32768-block Rivers regions).

**STATUS: compiled; 33 unit tests pass, including binding to the installed Rivers.dll and building nodes with Rivers' own code.
Not yet flown in game.**

## What it does

| Feature | How |
|---|---|
| Rivers may grow up to **10,000 blocks** from the mouth (a cap; most end sooner) | Sets Rivers' `maxNodes` (the stage limit of `GenerateRiver`) to `MaxRiverLengthBlocks` / mean node length (minLength + lengthVariation/2 = 250 at defaults: 40 nodes instead of 20). Rivers still stops a branch where it would run back toward the sea, cross itself, leave its region or meet another river. |
| **Main river at every fork**, cascading hierarchy | Rivers forks into two equal branches with the same remaining budget. Here the secondary one (picked per fork by a position hash) keeps `TributaryLengthShare` (0.6) of the remaining node budget. After Rivers sizes the river, the branch with the **longest upstream path** is the main stem at every confluence (ties: the straighter continuation); the rest are secondary, one level up per secondary confluence. |
| **Longer tributaries** | With the longer stem budget, a tributary keeps 0.6 of the remaining budget: up to ~24 nodes (~6000 blocks) instead of at most 20 nodes for the whole river. |
| **Tributary width cap** | A secondary branch is at most `TributaryWidthShare` (0.6) of the width of the river it joins and at most `TributaryMaxSize` (28; Rivers' widths are 14..50), never below `TributaryMinSize` (8); the cap cascades up every branch. The main stem keeps Rivers' widths. |
| **Deltas** | At a river mouth, with probability `DeltaChance` x (0.5 + mouth width / Rivers' maxSize), so wide rivers get deltas more often (none below `DeltaMinRiverSize` 30): 2..4 distributary channels fan out seaward over +-45 degrees, 120..260 blocks long, half the mouth's width. Rivers carves them like its own channels. Rivers Extended is not needed or used. |
| **Oxbow lakes** | At bends of the main stem and tributaries up to level 2 (turn >= 12 degrees, width >= 10) with probability `OxbowChance` (0.65; 0.1.0 had 0.2 and 20 degrees, which gave 21 oxbows for 144 rivers because most of Rivers' bends are about 18 degrees): a horseshoe of still water (speed 0, like Rivers' own lakes), radius 30..60, on the OUTER side of the bend, clear of every river channel. **0.1.2:** only where the estimated ground across the horseshoe and `OxbowBankMargin` (12) blocks of bank, the water level included, varies by at most `OxbowMaxRelief` (7) blocks. |
| **Calm, low headwaters** (0.1.2) | Rivers gives every node flow speed 1, right up to the source tip, and its water is always at sea level. A source tip whose upstream half and banks stand more than `HeadwaterMaxRise` (8) blocks above the water is removed, up to `HeadwaterMaxTrimNodes` (3) per source; the new tip becomes the source (Rivers' own source-lake roll applies). Flow speed then ramps from `HeadwaterMinSpeed` (0.25) at a source to full speed `HeadwaterCalmBlocks` (800) blocks downstream. |

All decisions are deterministic per world seed and position.

## Terrain estimate (0.1.2)

The network is built before any chunk exists, so the checks use an estimate made from the same inputs as Rivers' terrain:
the game's landform map (`GenMaps.landformsGen`, 3x3 map cells) with each landform's terrainYKey thresholds, and Rivers'
valley rule (`ModifyLandformWeights`: landform weights x `lerp(strength, 1, distance / maxValleyWidth)^2`, the rest goes to
Rivers' flat riverlandform; strength from Rivers' valley noise). Ground is counted up to where the solid probability falls to
0.2 (`TerrainNoiseShare` 1), because the landform noise turns a partly solid height band into plateaus and terraces. Not
counted: 3D noise detail and geologic upheaval. If anything it needs is missing, one warning is logged and the checks are off.


## How it attaches to Rivers (checked against the installed Rivers.dll by reflection, no decompiled code)

- `RiverRegion.GenerateZones` prefix: `maxNodes` on the region's config.
- `RiverRegion.GenerateRiver` prefix/postfix: detects a fork (two requests enqueued) and advances the secondary request's stage;
  stops a river at `MaxNodesPerRiver`.
- `River.AssignRiverSizes` postfix (Rivers calls it once per river, before adding source lakes and indexing segments for terrain):
  steep headwater tips removed (segments unlinked, the parent becomes the source), hierarchy and width caps, flow speeds,
  then delta and oxbow nodes appended to the river (`end = false`, so Rivers adds no source lake to them).
- `RiverRegion.GenerateZones` also tells the postfix which region (world origin) it is working in.

Rivers is not referenced at compile time. If any member is missing (another Rivers version), the mod logs one line and stays off;
any error while running turns it off for the session (one log line) and Rivers continues unchanged.

## Log lines

- Start: `[RiverHierarchy] active: max river length 10000 blocks, tributary length share 0.6, ...`
- First river: `[RiverHierarchy] terrain estimate bound (landform map + Rivers valley): ...` (or a warning that the checks are off).
- Shutdown: `[RiverHierarchy] this session: rivers N, forks made secondary N, tributary nodes narrowed N, deltas N (N channels), oxbow lakes N (skipped on steep ground N), headwater nodes trimmed on steep ground N, headwater nodes slowed N, ...`

## Unverified

How Rivers carves channels that have no parent (delta distributaries), whether still-water oxbow chains fill evenly: check them in game. Rivers regions are 32768 blocks; a 10k
river fits, but the uphill rule may stop many rivers earlier.

Use with **TerraWaters**: independent. TerraWaters reads the finished terrain and water; oxbows and deltas are Rivers water to it
(river exclusion, flow-through lakes join only channels, see its README).

## World-creation setting (0.1.3)

`worldconfig.json` adds *Oxbow lakes (River Hierarchy)* to Customize world, World generation: "From mod config" (default) or
Off / Rare / Few / Some / Many / Every suitable bend (OxbowChance 0, 0.2, 0.4, 0.65, 0.85, 1) for that world only. The mod is
now `"side": "Universal"` (`requiredOnClient: false`) so the client sees the setting and its label; the code still runs on the
server only.

## 0.1.4 (flight test 7)

- **Deltas on land.** Rivers starts each river in an ocean zone, so its mouth node begins out at sea; the 0.1.x deltas fanned
  out from that point and lay entirely in the ocean (invisible, or trenches). Now the coast is found along the main stem with
  the game's ocean map (`GenMaps.oceanGen`, which Continental Worlds supplies), the apex sits 120..260 blocks upstream of it on
  land, and 2..4 distributaries run from the apex to just past the coastline (24 blocks), on both sides of the main stem.
  A channel whose banks are estimated more than `DeltaMaxRise` (6) blocks above the sea is dropped.
- **Shallow river mouths.** Rivers carves every channel column down to about sea - 7 (sea - 15 for wide rivers), also on the
  ocean shelf, which left a trench across the shelf and a cliff where it met shallower floor (-593,-288). A postfix on
  `RiversApi.SampleRiver` fades the channel's bank factor where the ocean map says sea, over `MouthShallowRange` (60) of
  oceanicity above Rivers' `oceanThreshold`, to a depth whose cut ends at sea level: nothing under the sea is dug. Land
  columns are unchanged.
- **World-creation settings** (tab *Rivers & Lakes*): River length (Rivers default, up to 5,000 .. 20,000 blocks, a cap), River deltas
  (Off .. All wide rivers), Oxbow lakes. Labels come from `assets/game/lang/worldconfig-en.json`, the only place the game
  reads mod world-setting translations from (TranslationService.PreLoadModWorldConfig).
- Shutdown line adds `deltas skipped (no ocean map) N, river-mouth columns made shallow N`.

## 0.1.5 (flight test 8)

- **Varied oxbows.** Each oxbow picks a shape: horseshoe (20%), crescent (30%), long flattened arc (25%) or irregular meander
  scar (25%), turned up to +-25 degrees, radius wobbling point by point, 6..8 points.
- **Dry oxbows** (`DryOxbowShare` 0.3 of oxbows): an abandoned channel whose floor stays `DryFloorAboveSea` (3) blocks above
  the sea, so it never holds water.
- **Dry rivers (wadis).** Going down each river from the mouth, the river itself or a tributary branch becomes dry with
  probability `DryRiverChance` (0.6) x (`DryRainLimit` (0.4) - rainfall) / `DryRainLimit` (rainfall from the game's climate map at
  the node), and everything upstream of a dry node is dry: likeliest in deserts, never where rainfall is 0.4 or more. A dry
  node is at most `DryRiverMaxSize` (12, about 24 blocks wide), has no flow and no source lake, and its channel floor stays
  3 blocks above the sea. A dry river's mouth node fades out over its lower 60%, so the wadi runs out on land instead of
  reaching the sea; a dry tributary simply ends at the wet river it joins. Rivers' valley still forms around it, so it reads
  as a dry gravel-floored valley.
  How: postfixes on `RiverGenerator.SampleSelf` / `SampleConnector` cap the bank factor a dry node adds at
  (heightBoost - DryFloorAboveSea) / (map height - sea); Rivers carves from sea + heightBoost - depth up and fills water only
  below sea level, so the floor stays dry.
- World setting *Dry rivers* (Off / Rare / Some / Many) on the *Rivers & Lakes* tab.
- Shutdown line adds `dry river nodes N, dry oxbows N`.
- UNVERIFIED in game: how the dry floor looks where Rivers' valley already lowers the ground to about sea + 4 (then the valley
  floor itself is the wadi), and whether TerraWaters' standalone lakes fill some dry hollows.

