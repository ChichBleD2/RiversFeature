> **Historical research notes (2026-11-22, before the merge into River Features 1.0.0).** Kept for the Rivers internals
> they record. The "wanted" items and numbers below are the starting point, not the current behaviour; README.md describes
> what the mod does now (for example river length is now `MaxRiverLengthBlocks`, a growth cap; see README.md).
> Not shipped in the mod zip.

# River network (Rivers 5.0.2): findings for deltas, length, oxbows, tributaries, hierarchy

Read on 2026-11-22 from the installed `Rivers.dll` (Rivers 5.0.2) by reflection (types, fields, method signatures and the
calls each method makes; no decompiled source) and from `ModConfig/rivers.json` / `riversextended.json`.

## How Rivers builds rivers

- **Network first, terrain second.** `RiverRegion` (a Rivers region is `RegionSize` = 32768 blocks, split into zones of
  `zoneSize` 256) generates whole river trees in `GenerateZones` → `GenerateRiver(angle, startPos, stage, parentNode, river,
  errorLevel, queue)`, starting from coastal zones (`RiverZone.coastalZone`, `oceanDistance`; `SetZoneOceanicity` reads the
  ocean map, which Continental Worlds replaces). Lakes are nodes too: `AddLake(...)`, `RiverNode.isLake`.
- **Data model (all public):**
  - `River`: `List<RiverNode> nodes`, `Radius`, `StartPos`; `AssignRiverSizes()` → recursive `AssignRiverSize(RiverNode, float)`.
  - `RiverNode`: `startPos`, `endPos`, `parentNode`, `river`, `startSize`, `endSize`, `end`, `segments`, `speed`, `isLake`.
  - `RiverSegment`: `startPos`, `endPos`, `midPoint`, `riverNode`, `parent`, `children`, `parentInvalid`.
  - `RiverRegion`: `rivers`, `riverStarts`, `zones`, `segmentsForSampling` and `paddedNodeBounds` (RBush spatial indexes used when
    terrain is sampled), `config` (`RiverConfig`).
- **Terrain:** `NewGenTerra` (Terrain pass, order 0) replaces vanilla terrain generation, samples the segments near each chunk
  (`RiverGenerator.SampleRiver` → `riverDistance`, `bankFactor`, flow vector) and writes river and lake water. `GravelGen`
  (Terrain, 0.45) gravels the beds, `GravityBlocksPass` (Terrain, 0.5), `GeneratePartialFeatures` (Vegetation, 0.15) only
  places boulders. `RiversApi` (public) exposes `GetSegmentsInRangeOfChunk`, `SampleRiver`, `GetRiverRegion`.
- **Rivers Extended 0.1.0** already widens river mouths into estuaries (`EstuaryExtension`), smooths shores, and scales spawn
  by rainfall. It does not make branching deltas, oxbows or a main/secondary hierarchy.

## What config already controls (`rivers.json`)

| Want | Knobs | Note |
|---|---|---|
| Longer rivers (~4-5k now, 10k wanted) | `minNodes` 8 / `maxNodes` 20, `minLength` 150 + `lengthVariation` 200 per node | The longest stem is bounded by about maxNodes x (minLength + lengthVariation) = 7000 now. E.g. `maxNodes` 28, `minLength` 200, `lengthVariation` 200 gives up to ~11k. UNVERIFIED how nodes split between stem and branches: measure with Rivers' debug command (`RiverDebugCommand`, maps rivers as waypoints). |
| Tributary count | `riverSplitChance` 0.35, `minForkAngle`, `forkVariation` | |
| Widths | `minSize` 14, `maxSize` 50, `riverGrowth` 3 | Global, not per hierarchy level. |
| River lakes | `lakeChance` 0.1 | These are the big round lakes at junctions. |

## What needs code (not in any config)

| Feature | Where it has to act | Feasibility |
|---|---|---|
| Main river at a confluence, cascading hierarchy | Harmony postfix on `River.AssignRiverSizes`: build children from `parentNode`, give each confluence's longest upstream branch the main stem, mark the rest secondary (recursively), then rewrite `startSize`/`endSize`. | Good: public types, one method, runs before terrain. |
| Width cap on tributaries | Same postfix: clamp secondary widths (for example to a configurable max and a share of the main stem). | Good. |
| Longer tributaries | `GenerateRiver` decides branch lengths; either config (above) or a prefix that raises node counts for branches. | Medium (needs the generation order understood). |
| Deltas with configurable chance | Postfix on `RiverRegion.GenerateZones`: at mouth nodes in coastal zones, add 2..4 distributary nodes/segments fanning to the sea, and insert them into `segmentsForSampling` / `paddedNodeBounds`. | Medium-hard: new segments must be valid for `SampleRiver` and the RBush indexes. |
| Oxbow lakes | Either Rivers-side (add `isLake` nodes beside strong meander bends via `AddLake`), or TerraWaters-side: crescent lake features placed from `RiversApi.GetSegmentsInRangeOfChunk` geometry (bends) and carved like TerraWaters lakes. | Medium. TerraWaters-side keeps Rivers untouched and reuses the lake rules. |

## Recommendation

A **separate companion mod** (for example `riverhierarchy`, depends on `rivers` 5.0.2), not TerraWaters:

- Every one of these changes rewrites Rivers' own network before Rivers carves terrain, through Harmony patches on Rivers' classes.
  That ties the code to Rivers 5.0.2 internals; isolating it means a Rivers update can only break the companion, and TerraWaters (which
  only reads finished terrain and water) keeps working.
- TerraWaters runs per chunk in the TerrainFeatures pass with only local data; it cannot change a river's path, width or branching.
- Oxbows are the one item that could live in TerraWaters (as lake features from the public segment API), if preferred.
