# GPU-Resident Volumetric Clipbox LOD — Phase 4 Rebuild Specification

**Repository:** `burn0ut7/voxels2`  
**Target branch:** a new Phase 4 rebuild branch created from the last verified Phase 3 implementation  
**Document purpose:** standalone implementation contract for Codex  
**Scope:** rebuild clipbox LOD and Transvoxel transitions without changing the successful Phase 2/3 GPU terrain architecture

---

## Codex start instruction

Use this at the beginning of a new Codex chat:

> Read `Docs/GPU_CLIPBOX_PHASE4_REBUILD.md` completely, inspect the repository, identify the exact last-known-good Phase 3 code state, and implement only the next incomplete Phase 4 rebuild slice. Preserve the successful GPU regular-meshing pipeline. Do not copy the failed Phase 4 runtime design wholesale. Compile and run the required gate after every slice. Stop immediately when a gate fails.

Codex must report before editing:

1. Current branch and commit.
2. Last verified Phase 3 commit or equivalent code state.
3. Whether the editor Git-polling stutter fix is present.
4. Which Phase 4 rebuild slice is next.
5. Exact files expected to change.
6. Baseline benchmark results being protected.

---

# 1. Decision and status

The GPU terrain architecture remains valid. The previous Phase 3 implementation proved:

- GPU procedural visual SDF evaluation;
- GPU regular-cell Transvoxel meshing;
- persistent GPU vertex/index pools;
- compact asynchronous count readback;
- bounded resident state;
- indirect rendering;
- no production geometry readback;
- fixed-LOD player streaming at roughly 220–240 FPS on the reference RTX 5090 test machine.

The first Phase 4 attempt must be archived, not incrementally patched into production. It combined too many new systems at once and violated core clipmap invariants.

## Required repository action

Before implementation:

```text
current Phase 4 head
    -> preserve as archive/phase4-attempt-1 or equivalent tag/branch

new Phase 4 rebuild branch
    -> start from last verified Phase 3 implementation
    -> retain the asynchronous Git identity/stutter fix
    -> retain current benchmark/reporting improvements that do not alter terrain behavior
```

Do not delete the first attempt. It remains useful for tests, lookup tables, and failure analysis.

## Implementation status table

| Slice | Status at document creation |
|---|---|
| Phase 3 fixed-LOD GPU terrain | Verified baseline |
| 4R-0 baseline restoration and archive | Next |
| 4R-1 mathematical clipbox planner | Verified; standalone proof, live editor integration, and clean suite v20 runtime gate passed |
| 4R-2 renderer-capacity proof | Verified; standalone proof, live editor integration, and clean suite v20 runtime gate passed |
| 4R-3 regular two-level clipbox | Verified; static integration, live compiler, and clean suite v20 runtime gates passed |
| 4R-4 four-level toroidal streaming | Verified; planner/runtime integration, bounded revision diagnostics, editor visualization, and clean suite v20 runtime gate passed |
| 4R-5 transition ownership and residency | Verified; stable seam slots, fine-side ownership, generation dependency metadata, diagnostics, proof, editor visualization, and clean suite v20 runtime gate passed |
| 4R-6 Transvoxel golden reference and GPU proof | Verified; pinned official transition tables, CPU reference, six orientations, all 512 cases, winding, scale, fixtures, gradient normals, boundary checks, live GPU case proof, and production transition runtime passed |
| 4R-7 production GPU transition pipeline | Verified on the RTX 5090 reference machine in clean suite v20 run `20260809-101717`; bounded GPU count/emission, exact persistent allocation, shared indirect rendering, zero production geometry readback, and zero CPU transition SDF evaluations |
| 4R-8 coherent publication and stability | Verified in clean suite v20; exact desired-resident settlement, stale publication rejection, bounded queues, regular LOD gates, and actual-player clipbox oscillation passed |
| 4R-9 performance/adoption campaign | Not started; requires the complete suite on the RTX 5090 and a representative mid-range GPU |

Do not begin Phase 5 editing until every Phase 4 gate passes.

---

# 2. Research basis and how it applies

This specification is based on the sources below, but it does not copy a heightmap geometry-clipmap implementation directly. Classic geometry clipmaps are two-dimensional heightfield systems. This project needs a three-dimensional volumetric adaptation for SDF terrain with caves and overhangs.

## 2.1 Geometry Clipmaps — Losasso and Hoppe

Reference:

- [Geometry Clipmaps: Terrain Rendering Using Nested Regular Grids](https://hhoppe.com/proj/geomclipmap/)
- [GPU Gems 2, Chapter 2: Terrain Rendering Using GPU-Based Geometry Clipmaps](https://developer.nvidia.com/gpugems/gpugems2/part-i-geometric-complexity/chapter-2-terrain-rendering-using-gpu-based-geometry)

Source-derived principles to retain:

- viewer-centered nested regular grids;
- only the finest level is complete; coarser levels are hollow rings;
- fixed-size level storage;
- grid origins snap to level spacing;
- toroidal addressing reuses storage;
- movement updates only newly exposed regions;
- coarse levels update less often;
- render cost is bounded independently of world size;
- block-level frustum culling;
- graceful degradation under fast motion;
- transitions must be spatially and temporally continuous.

Volumetric adaptation used by this project:

```text
2D square clipmap ring
    -> 3D cubical clipbox shell

height texture update strip
    -> regular voxel-block slot reassignment

vertex geomorph between height levels
    -> Transvoxel transition cells between 3D SDF resolutions

fixed height-grid footprint
    -> persistent generated regular and transition meshes
```

What must not be copied:

- heightmap-only vertex displacement;
- 2D trim/filler mesh topology;
- skirts or degenerate strips as the volumetric seam solution;
- vertex geomorphing as a replacement for Transvoxel.

## 2.2 GPU procedural volumetric terrain — GPU Gems 3

Reference:

- [GPU Gems 3, Chapter 1: Generating Complex Procedural Terrains Using the GPU](https://developer.nvidia.com/gpugems/gpugems3/part-i-geometry/chapter-1-generating-complex-procedural-terrains-using-gpu)

Source-derived principles to retain:

- evaluate a density field in three dimensions;
- process fixed-size terrain blocks;
- use 32³ cells per block;
- generate density and polygons on the GPU;
- keep generated geometry resident until eviction;
- prioritize nearby/visible blocks;
- recognize that many blocks are entirely empty or solid;
- use density gradients for normals;
- include halo samples for correct boundaries.

Project decision:

- retain the successful Phase 3 two-pass count/allocation/emission pipeline;
- evaluate every LOD directly from the canonical terrain field;
- do not generate LOD0 first and downsample it;
- add block empty/solid rejection before expensive classification where practical.

## 2.3 Voxel Tools / godot_voxel

Reference:

- [Voxel Tools smooth terrain documentation](https://voxel-tools.readthedocs.io/en/latest/smooth_terrain/)
- [Zylann/godot_voxel](https://github.com/Zylann/godot_voxel)

Source-derived principles to retain:

- block resolution stays constant while block and voxel scale double per LOD;
- adjacent LOD levels differ by 2:1;
- Transvoxel is used for volumetric seams;
- clipbox streaming loads concentric boxes and updates only differences;
- LOD count and block size must be balanced against view distance;
- SDF range/precision matters more at coarse LODs;
- the same canonical position must return the same field value at every LOD;
- optional LOD fading and secondary transition positions can reduce visible popping after correctness is established.

Project decision:

- one primary local visual observer controls client clipbox residency;
- auxiliary cameras reuse the same resident terrain;
- multiplayer/gameplay interest remains separate;
- no LOD-dependent terrain rule changes;
- no fixed narrow SDF clamp that shifts the zero crossing at coarse LODs.

## 2.4 Transvoxel

Reference:

- [The Transvoxel Algorithm](https://transvoxel.org/)
- [Official Transvoxel data tables](https://github.com/EricLengyel/Transvoxel)

Source-derived requirements:

- transitions connect exactly full-resolution and half-resolution voxel data;
- only local voxel data is required;
- there are 512 transition cases;
- transition cells are inserted only on genuine 2:1 boundaries;
- arbitrary volumetric cracks cannot be solved reliably by heightfield stitching;
- the official tables and orientation semantics are authoritative.

Project decision:

- preserve the project’s current Transvoxel tables only after validating them against the official tables;
- build a CPU golden transition fixture for tests only;
- production transition SDF evaluation, classification, counting, and emission run on the GPU;
- a transition key includes the actual fine and coarse resident generations.

## 2.5 Mike J. Savage geometry-clipmap implementation

Reference:

- [Geometry clipmaps: simple terrain rendering with level of detail](https://mikejsavage.co.uk/geometry-clipmaps/)

Applicable implementation lessons:

- square tiles alone are insufficient; bad layouts produce overlap on one side and gaps on another;
- clipmap snapping must be applied consistently to geometry and sample coordinates;
- incorrect snapping creates trembling, swimming, or large-scale mesh movement;
- high-frequency detail can make LOD normal changes more visible;
- the layout must be proven before adding visual polish.

Project adaptation:

- do not use 2D filler/trim geometry;
- prove cubical shell ownership and block alignment mathematically;
- prove sample origins and rendered origins use the same snapped coordinate system;
- add LOD-color, shell-boundary, toroidal-slot, and seam-direction debug modes before optimizing.

## 2.6 s&box rendering constraints

Reference:

- [s&box `CommandList`](https://sbox.game/api/Sandbox.Rendering.CommandList)
- [s&box indirect indexed draw API](https://sbox.game/api/Sandbox.Rendering.CommandList/DrawIndexedInstancedIndirect)
- [s&box command-list documentation](https://sbox.game/dev/doc/rendering/shaders/command-lists)

Project requirements:

- capability-test indirect offsets and multi-draw counts;
- never assume the current 16-command grouping is an engine requirement;
- never allocate or attach command lists for a huge theoretical maximum when only a small active range is used;
- transition and regular geometry must both be frustum culled;
- inactive command lists must be disabled or detached;
- rendering must not depend on synchronous readback.

---

# 3. Failed Phase 4 patterns that must not return

The first attempt contained or risked the following patterns. Codex must explicitly verify they are absent.

## 3.1 Overlapping LOD volumes

A coarse level must not render regular blocks inside the complete volume covered by the next-finer level.

Forbidden:

```text
LOD0 full cube
LOD1 full cube overlapping LOD0
LOD2 full cube overlapping both
```

Required:

```text
LOD0 full cube
LOD1 outer cube minus LOD0 coverage
LOD2 outer cube minus LOD1 coverage
LOD3 outer cube minus LOD2 coverage
```

## 3.2 Arbitrary staging resident capacity

Do not add a large pool of extra resident slots to survive movement.

Required:

- each clipbox level owns a fixed toroidal slot array;
- each slot stores current and pending assignment;
- entering world blocks reuse the toroidal slot of the exiting block;
- pending geometry uses mesh-pool memory, not a new resident slot.

## 3.3 Full transition-set CPU rebuild

Forbidden production work:

- CPU evaluation of all transition SDF samples;
- one `List<TransitionCell>` per face;
- `float[13]` per cell or face;
- LINQ `Sum`, `ToArray`, or full-set hashing in the hot path;
- rebuilding every transition because one slab changed.

## 3.4 Magic transition limits

Forbidden:

```text
MaximumTransitionFaces = 2048
if limit reached:
    break
```

A capacity limit must defer, retry, degrade explicitly, or fail the gate. It may never silently omit seams.

## 3.5 Maximum-capacity rendering

Forbidden:

- thousands of permanently active command lists;
- an arbitrary `+8192` indirect-command capacity;
- transition commands appended without culling;
- comparing/uploading the entire unused command tail;
- disappearing terrain at draw 49 or another command-group boundary.

## 3.6 Per-frame stationary transition work

A stationary, settled observer must produce:

```text
0 clipbox revisions
0 regular requests
0 transition requests
0 transition set hashing
0 resident publications
0 indirect uploads unless the camera actually changes culling
approximately 0 managed terrain allocation
```

## 3.7 Fixed LOD-independent SDF clamp

A fixed clamp can change interpolation at coarse spacing and move the reconstructed surface.

Required:

- preserve the unclamped field through classification/interpolation when scratch uses floats; or
- use an explicitly proven LOD-scaled range;
- the same world coordinate must return the same field value at every LOD.

---

# 4. Non-negotiable architecture

```text
CPU/server canonical state
    seed
    terrain-rule version
    macro descriptors
    ordered edits later
    gameplay queries
    nearby collision
    clipbox policy and scheduling
               |
               v
Client GPU visual cache
    regular density/count/emit
    transition density/count/emit
    persistent mesh pool
    resident descriptors
    indirect rendering
```

## Invariants

1. CPU/server remains authoritative.
2. GPU remains the only visual terrain backend in measured GPU scenarios.
3. No CPU visual fallback.
4. No production vertex/index readback.
5. No synchronous GPU wait.
6. No skirts or overlapping crack-hiding geometry.
7. Only adjacent LODs with a 2:1 sampling ratio may touch.
8. World coverage is complete and non-overlapping.
9. All storage and queues are bounded.
10. Allocation pressure never silently drops terrain.
11. Old coherent terrain remains visible until replacement is complete.
12. Ordinary movement changes assignments only for affected toroidal slots.
13. Stationary observers cause no residency work.
14. Phase 3 fixed-LOD behavior remains available as a control until Phase 4 adoption.

---

# 5. Coordinate model

Use integer coordinates for all planning.

## 5.1 Constants

```csharp
const int CellsPerBlock = 32;
```

Initial supported clipbox dimensions:

```text
BlocksPerAxis = 4 or 8
```

Do not support arbitrary values initially.

Requirements:

```text
BlocksPerAxis is a power of two
BlocksPerAxis >= 4
BlocksPerAxis % 4 == 0
LevelCount is 1..4
```

Default production-development configuration:

```text
BlocksPerAxis = 8
LevelCount = 4
```

Use `4 × 4 × 4` for fast correctness tests.

## 5.2 Coordinate spaces

```text
CanonicalSample
    integer LOD0 sample coordinate

BaseBlock
    CanonicalSample / 32

LodBlock(L)
    one block covers 32 * 2^L canonical cells per axis

World
    CanonicalSample * VoxelSize + terrain transform
```

A regular block key is:

```csharp
readonly record struct VoxelVisualBlockKey(
    Int3 LodBlockCoordinate,
    byte Lod,
    ushort RuleVersion,
    uint EditRevision
);
```

`EditRevision` may remain zero until Phase 5, but the field must exist in the lifetime model.

## 5.3 Sampling

For LOD `L`:

```text
sampleSpacing = 2^L
blockCanonicalExtent = 32 * sampleSpacing
```

Each block still contains:

```text
32³ logical cells
33³ corner samples
plus the existing normal halo
```

Every sample position is:

```text
blockOriginCanonical
+ localSampleCoordinate * sampleSpacing
```

The regular and transition pipelines must call the same canonical field implementation.

---

# 6. Clipbox geometry

## 6.1 Snapped origin

Let:

```text
B = BlocksPerAxis
L = LOD index
S = 2^L
observerBaseBlock = floor(observerCanonicalSample / 32)
observerLodBlock = floorDiv(observerBaseBlock, S)
snappedCenterL = floorDiv(observerLodBlock, 2) * 2
originL = snappedCenterL - B / 2
```

The center is even in LOD-block coordinates. Because `B / 2` is also even, every level boundary aligns to its parent grid.

Do not use floats or center-distance tests to determine shell ownership.

## 6.2 Outer and inner boxes

Level 0:

```text
active = outer box [origin0, origin0 + B)
```

Level `L > 0`:

```text
outer = [originL, originL + B)

innerMin = origin(L-1) / 2
innerMax = (origin(L-1) + B) / 2

active = outer minus [innerMin, innerMax)
```

All divisions above must be exact due to snapping. Assert this in debug builds.

## 6.3 Expected active regular counts

For `B` blocks per axis and `N` levels:

```text
regularActive =
    B³
    + (N - 1) * (B³ - (B / 2)³)
```

Expected values:

| Blocks/axis | Levels | Active regular blocks | Stable regular slots |
|---:|---:|---:|---:|
| 4 | 2 | 120 | 128 |
| 4 | 4 | 232 | 256 |
| 8 | 2 | 960 | 1,024 |
| 8 | 4 | 1,856 | 2,048 |

`Stable regular slots = levels * B³`.

If runtime counts differ without cropping or inactive levels, fail the planner gate.

## 6.4 Coverage invariant

For the committed revision:

- every point inside the outermost clipbox belongs to exactly one regular LOD;
- regular volumes from different LODs do not overlap;
- boundaries may touch but never overlap;
- there are no holes in the union of regular block volumes;
- every neighbor pair is same LOD or differs by exactly one.

Build a slow reference checker for tests. It may enumerate all block AABBs because it is not a runtime path.

---

# 7. Toroidal slot model

## 7.1 Stable slots

Each level owns exactly `B³` slots.

```csharp
slotLocal = PositiveModulo(worldLodBlockCoordinate, B);

slotIndex =
    levelBase
    + slotLocal.X
    + B * (slotLocal.Y + B * slotLocal.Z);
```

Within one level window, no two desired world coordinates may map to the same slot. Assert it.

## 7.2 Slot state

```csharp
enum ClipboxSlotState
{
    Inactive,
    Current,
    PendingCount,
    PendingAllocation,
    PendingEmit,
    PendingCommit,
    FailedDeferred
}

struct ClipboxRegularSlot
{
    int StableSlotId;
    int Lod;
    Int3 CurrentCoordinate;
    uint CurrentGeneration;
    GpuAllocation CurrentAllocation;

    Int3 PendingCoordinate;
    uint PendingGeneration;
    GpuAllocation PendingAllocation;

    ClipboxSlotState State;
    ulong PendingRevision;
}
```

The resident-table capacity is the exact stable-slot count. Do not add staging resident slots.

## 7.3 Assignment update

On a snapped origin change:

1. Compute the desired world key for every stable slot.
2. Compare desired assignment with current/pending assignment.
3. Leave identical assignments untouched.
4. Create pending work only for changed active assignments.
5. Mark newly inactive slots for retirement at commit.
6. Keep current geometry selected until commit.

A bounded scan of the stable slot arrays is acceptable. “Slab-only update” means that only changed slots generate or publish geometry; it does not prohibit scanning a few thousand fixed metadata entries.

## 7.4 Revision model

Only one uncommitted clipbox revision is required initially.

```csharp
struct ClipboxRevision
{
    ulong Id;
    Int3 ObserverBaseBlock;
    int ChangedRegularSlots;
    int ChangedSeamSlots;
    int PendingRegular;
    int PendingSeams;
    RevisionState State;
}
```

When a newer observer position arrives:

- coalesce directly to the newest snapped origin;
- cancel stale pending work;
- reuse completed work whose desired key still matches;
- never queue every intermediate camera position;
- keep the last committed revision rendered.

---

# 8. Regular LOD generation

Reuse the known-good Phase 3 regular GPU pipeline.

## 8.1 Request

```csharp
struct RegularBlockRequest
{
    Int3 LodBlockCoordinate;
    uint Lod;
    uint SampleSpacing;
    uint RuleVersion;
    uint EditRevision;
    uint SlotId;
    uint Generation;
    ulong ClipboxRevision;
}
```

## 8.2 Pipeline

```text
GPU density evaluation
    -> block min/max reduction
    -> skip empty/solid blocks
    -> regular-cell classification
    -> scans and counts
    -> asynchronous compact count metadata
    -> CPU exact persistent allocation
    -> GPU emission
    -> pending slot ready
```

## 8.3 Canonical field

Create one shader include used by regular and transition generation:

```text
Assets/shaders/voxel_terrain_field.hlsl
```

Required API:

```hlsl
float EvaluateTerrainSdf(int3 canonicalSample, uint ruleVersion);
MaterialSample EvaluateTerrainMaterial(int3 canonicalSample, uint ruleVersion);
float3 EvaluateTerrainGradient(int3 canonicalSample, uint ruleVersion, int step);
```

Create a matching CPU evaluator for golden tests only.

Rules:

- no LOD-dependent noise or terrain-rule branches;
- LOD changes only sample spacing;
- no fixed narrow clamp before interpolation;
- use integer/camera-relative coordinates before converting to float;
- shared block-boundary samples must be bitwise equal where practical.

## 8.4 Empty and solid residents

A zero-geometry block is still a valid published regular slot:

```text
PublishedEmpty
PublishedSolid
PublishedSurface
```

It consumes metadata but no mesh-pool allocation.

Do not repeatedly regenerate zero-geometry blocks while they remain assigned.

---

# 9. Transition ownership

## 9.1 When a transition exists

A transition face exists only when:

1. a fine regular block is active;
2. the same-LOD neighbor across that face is not active;
3. an active coarse block exists across the face;
4. `coarse.Lod == fine.Lod + 1`;
5. the boundary plane aligns to the coarse block grid.

Same-LOD faces produce no transition.

The outer boundary of the coarsest level produces no transition.

## 9.2 Ownership

The fine block owns the transition face.

```csharp
readonly record struct TransitionKey(
    int FineSlotId,
    FaceDirection Face,
    uint FineGeneration,
    int CoarseSlotId,
    uint CoarseGeneration,
    ushort RuleVersion,
    uint EditRevision
);
```

The generations are mandatory. Coordinate/LOD alone is insufficient after replacement or editing.

## 9.3 Stable transition slots

For each adjacent LOD pair:

```text
6 faces * B² possible fine boundary faces
```

Maximum transition slots:

```text
transitionSlotCapacity =
    (LevelCount - 1) * 6 * B²
```

Expected capacities:

| Blocks/axis | Levels | Maximum transition face slots |
|---:|---:|---:|
| 4 | 2 | 96 |
| 4 | 4 | 288 |
| 8 | 2 | 384 |
| 8 | 4 | 1,152 |

Do not use an arbitrary 2,048 or 8,192 limit.

A slot may be inactive, current, or current-plus-pending exactly like a regular slot.

## 9.4 Transition counts

For a 32-cell fine block face:

```text
transition grid = 16 × 16 transition cells
```

The runtime must produce this from the official Transvoxel coordinate and table semantics. Do not preserve the first attempt’s ad hoc 13-density layout unless the golden reference proves it correct.

---

# 10. GPU transition pipeline

Production transition work must remain GPU-first.

## 10.1 Required passes

```text
TransitionDensityCS
    evaluate canonical local samples

TransitionClassifyCountCS
    build 9-bit case codes
    lookup official transition class/topology
    calculate per-face vertex/index counts

TransitionScanTotalsCS
    compact offsets and face totals

Asynchronous count metadata
    face key
    generations
    vertex count
    index count
    overflow/status

CPU allocation
    exact persistent ranges

TransitionEmitCS
    table-driven interpolation
    gradient normals
    materials
    indices
```

The CPU may build request descriptors and allocate persistent ranges. It must not evaluate every transition cell’s SDF or topology in production.

## 10.2 Scratch design

Use a small bounded transition scratch ring.

Initial benchmark candidates:

```text
32 faces × 2 arenas
64 faces × 2 arenas
128 faces × 1 arena
```

Select after measurement.

Requirements:

- no full `MaximumFaces * 256` managed array;
- no large per-rebuild GPU cell buffer sized for every possible seam;
- permanent CPU memory added by Phase 4 should remain under 16 MiB at `B=8, L=4`;
- transition GPU scratch should target 32 MiB or less;
- no per-face managed allocations after warm-up.

## 10.3 Geometry space

Every transition vertex must use the same units and origin model as regular vertices.

Required tests must catch:

- missing `VoxelSize`;
- missing per-cell U/V offset;
- wrong face basis;
- wrong positive/negative face winding;
- duplicated cells at one origin;
- full-world precision drift.

## 10.4 Normals

Transition normals must come from the canonical SDF gradient.

Forbidden production normal:

```text
normal = -faceDirection
```

Regular and transition gradient step must be consistent with the fine sampling scale.

## 10.5 Boundary treatment

The CPU golden reference must establish the exact regular-boundary behavior required by the project’s Transvoxel implementation.

Do not merely append transition triangles to a full regular mesh and assume correctness.

The proof must verify:

- the transition layer occupies the intended boundary region;
- regular and transition geometry do not overlap incorrectly;
- no duplicate boundary faces;
- no crack or T-junction;
- optional secondary positions/transition masks are applied only after watertight topology is proven.

---

# 11. Coherent publication

## 11.1 First production model: revision-atomic commit

For the first correct implementation, publish one clipbox movement revision atomically.

A pending revision contains only changed slots; unchanged current slots are referenced directly.

Commit when:

```text
all changed regular slots are ready
and
all changed transition slots are ready
and
every transition dependency generation matches
```

At commit:

1. swap changed regular current/pending state;
2. swap changed transition current/pending state;
3. retire leaving regular and transition allocations;
4. rebuild/select render descriptors;
5. increment committed clipbox revision.

Before commit, render the previous committed revision.

This is broader than the final ideal local commit model, but it is deterministic and safe.

## 11.2 Later refinement

Only after revision-atomic publication passes all gates may Codex split commits into independent level-pair or local seam groups.

A local group must still include every affected fine/coarse/seam dependency.

## 11.3 Allocation failure

On mesh-pool or scratch pressure:

- do not remove the current resident;
- mark the pending request `FailedDeferred`;
- retry under scheduler budget;
- preserve the old committed revision;
- report explicit backpressure;
- never silently settle with missing terrain.

Resident-slot exhaustion is an assertion failure because slot capacity is mathematically fixed.

---

# 12. Rendering contract

## 12.1 Command population

Build one compact visible command sequence containing:

```text
visible current regular residents
visible current transition residents
```

Pending residents are never drawn.

Both types require conservative AABBs and frustum culling.

## 12.2 Capacity

```text
regular draw capacity <= stable regular slots
transition draw capacity <= stable transition slots
```

No arbitrary `+8192` capacity.

## 12.3 Command lists

Before production integration, add an explicit s&box capability test.

Test resident/draw counts:

```text
1
15, 16, 17
31, 32, 33
47, 48, 49
63, 64, 65
127, 128, 129
256
512
1024
```

For each count:

- all synthetic blocks are inside the frustum;
- all blocks have unique positions/colors;
- every command is rendered;
- no left/right replacement or disappearing chunks;
- verify indirect offset units;
- verify the highest supported multi-draw count;
- verify depth and opaque passes.

Production rules:

- only the command lists needed for the current active command range are enabled;
- unused lists are disabled or detached;
- do not execute a theoretical maximum number of zero commands;
- upload only the active range plus required clearing when the count shrinks;
- compare only the active range;
- regular and transition commands follow the same batching contract.

## 12.4 Culling

Initial implementation:

- CPU frustum culling;
- regular and seam AABBs;
- per-view command compaction;
- no HZB requirement.

HZB occlusion is a later optimization.

## 12.5 Render callback boundaries

Scheduling, result consumption, revision commit, and reclamation must run exactly once per game frame.

Only actual render submission belongs in per-view render callbacks.

Record callback count per frame during diagnostics.

---

# 13. Scheduling and movement policy

## 13.1 Priority

Initial regular priority:

```text
1. LOD0 blocks nearest the player
2. changed regular blocks required by an incomplete seam
3. LOD1 near shell
4. remaining regular work by projected/distance priority
5. outer coarse levels
```

Transition priority:

```text
1. seams blocking current revision commit
2. seams nearest the camera
3. remaining seams
```

Prevent seam work from permanently starving regular coverage.

## 13.2 Movement thresholds

Each level moves only when its snapped origin changes.

Because level centers are snapped to even LOD-block coordinates:

```text
LOD0 updates every 2 base blocks
LOD1 updates every 4 base blocks
LOD2 updates every 8 base blocks
LOD3 updates every 16 base blocks
```

This is intentional and keeps parent/child boundaries aligned.

The finest clipbox must include enough margin that this snapping does not expose missing terrain near the player.

## 13.3 Hysteresis and residency time

Add only after core correctness:

- minimum committed-revision lifetime;
- observer boundary hysteresis;
- age promotion for deferred requests;
- optional short dithered LOD fade.

Hysteresis must never hide a planner error.

## 13.4 Fast movement

When the observer moves faster than generation:

- keep the last committed revision;
- cancel/coalesce obsolete pending revisions;
- target the newest snapped position;
- keep queues bounded;
- never publish a partial seam state;
- report lag in snapped blocks and world units.

## 13.5 Teleport

Teleport is a separate path.

Requirements:

- bounded full reassignment;
- no request drop;
- old revision remains selected until the declared teleport readiness gate;
- record time to first safe near-field coverage and full commit;
- no synchronous generation.

---

# 14. Implementation slices and gates

Codex must implement one slice at a time.

## 4R-0 — Restore and lock the Phase 3 baseline

Deliverables:

- archive first Phase 4 attempt;
- restore known-good Phase 3 runtime;
- retain editor Git-polling fix;
- run complete GPU-only Phase 3 suite;
- create a clean baseline report and commit.

Pass:

- production render remains near the verified baseline;
- fixed-LOD streaming works;
- no periodic editor stutter;
- working tree clean;
- report records exact revision.

Fail:

- any missing Phase 3 feature;
- >5% frame-p95 regression;
- editor periodic hitch returns.

Stop after failure.

## 4R-1 — Mathematical planner only

Implement:

```text
VoxelClipboxConfig
VoxelClipboxLevelState
VoxelClipboxReferencePlanner
VoxelClipboxRuntimePlanner
stable regular slot mapping
active masks
```

No LOD rendering changes yet.

Tests:

- counts for every supported `B/L`;
- no overlap;
- complete coverage;
- aligned inner/outer bounds;
- no LOD difference greater than one;
- stationary delta zero;
- cardinal, diagonal, vertical movement;
- negative coordinates;
- origin crossing;
- teleport;
- runtime planner equals fresh reference plan for 10,000 deterministic observer positions.

Pass:

- zero mismatch;
- no allocation after warm-up in repeated planner movement;
- no changed slot outside the expected old/new active masks.

## 4R-2 — Renderer capacity proof

Implement synthetic indirect-render test before LOD integration.

Pass:

- all tested counts through at least 1,024 render correctly;
- draw 49 is visible;
- no command-group boundary changes coverage;
- depth and opaque outputs agree;
- unused command lists do not execute.

Fail:

- any count-dependent disappearance;
- unclear offset semantics;
- fixed 16-command assumption without a capability result.

## 4R-3 — Two-level regular clipbox, transitions disabled

Use the existing regular GPU pipeline.

Configuration:

```text
B=4
L=2
expected active regular=120
stable slots=128
```

Render LOD colors.

Pass:

- exact counts;
- no overlap or missing volume;
- correct sample scale;
- camera motion produces slot reassignment, not table growth;
- no transition geometry exists;
- expected cracks at the one LOD boundary are allowed in this slice;
- no arbitrary disappearing regular chunks.

Performance:

- one-LOD control remains within 5% of Phase 3;
- two-level regular clipbox settled p95 no worse than 15% over control.

## 4R-4 — Four-level regular toroidal streaming

Configurations:

```text
B=4, L=4
B=8, L=4
```

Transitions still disabled.

Pass:

- exact active/stable counts;
- movement changes assignments only;
- one pending revision maximum;
- no resident slot exhaustion;
- no dropped work;
- return-to-origin memory stable;
- vertical movement supported;
- 60-second stationary soak produces zero residency work.

## 4R-5 — Transition ownership and residency metadata

No transition geometry yet.

Implement stable seam slots and keys.

The rebuild uses fixed `(LevelCount - 1) * 6 * B²` seam slots. The proof covers 96, 288, 384, and 1152 slots for B4/L2, B4/L4, B8/L2, and B8/L4. The fine regular block is the sole owner of a face, and a slot is active only when its same-LOD neighbor is inactive and the adjacent coarse block is active. Each active key carries both regular stable slot IDs, both scheduler generations, rule version, and edit revision. Current/desired metadata arrays are reused without truncation; stationary observers produce no transition work, and Full clipbox gizmos draw and label each owned face dependency.

Pass:

- expected transition capacity;
- same-LOD faces produce none;
- outermost faces produce none;
- every fine/coarse boundary face has exactly one owner;
- every transition has valid current/pending generation dependencies;
- stationary observer creates zero transition work;
- movement updates only changed seam slots;
- no arbitrary cap/truncation.

## 4R-6 — Golden Transvoxel reference and GPU case proof

Build diagnostic CPU reference from official tables.

Tests:

- all 512 cases;
- all six orientations;
- positive/negative winding;
- unit scale and production `VoxelSize`;
- plane, sphere, cave mouth, and tangent surface fixtures;
- regular/transition boundary edge matching;
- gradient normals.

Diagnostic readback is permitted only in this proof.

Pass:

- topology and positions match the golden reference within declared tolerance;
- no invalid indices;
- no collapsed or world-spanning triangles;
- no duplicate transition cells.

## 4R-7 — Production GPU transition pipeline

Replace CPU transition SDF/classification with GPU passes.

Pass:

- zero production transition geometry readback;
- zero CPU per-cell SDF evaluation;
- exact persistent allocations;
- transition scratch bounded;
- no per-face managed allocations;
- all transition residents have bounds;
- full renderer culls them.

## 4R-8 — Coherent revision publication

Integrate regular and seam pending state.

Pass:

- no crack or partial seam during repeated boundary crossing;
- old revision remains coherent until commit;
- stale work rejected;
- oscillation does not grow queues;
- allocation failure defers instead of dropping;
- high-speed movement coalesces;
- teleport remains bounded.

## 4R-9 — Performance and adoption

Run the complete Phase 4 suite on:

- RTX 5090 reference machine;
- at least one representative mid-range GPU.

Pass every absolute and relative gate in Section 17.

Only then mark Phase 4 complete.

---

# 15. Debug visualization

Add runtime toggles:

```text
LOD color
regular slot ID
toroidal local coordinate
current vs pending
clipbox revision
outer boxes
inner exclusion boxes
active/inactive slot mask
transition owner
transition face direction
transition generation dependencies
frustum culled / visible
resident allocation ID
```

Required visual colors:

```text
LOD0 = distinct color
LOD1 = distinct color
LOD2 = distinct color
LOD3 = distinct color
pending = flashing or striped
stale/deferred = red
transition = separate high-contrast palette
```

Do not use debug visualization to alter terrain state.

---

# 16. Logging and reporting

## 16.1 Logging rules

Default runtime logs must be bounded.

Log only:

- initialization summary;
- committed revision summary;
- explicit backpressure/failure;
- benchmark start/end;
- capped slow-event traces.

Do not log per block, cell, face, or frame in normal play.

## 16.2 Revision event record

Write one structured event per planned/committed revision:

```json
{
  "frame": 0,
  "revision": 0,
  "observer_base_block": [0, 0, 0],
  "level_origins": [[0,0,0]],
  "regular_active": 0,
  "regular_changed": 0,
  "regular_pending": 0,
  "seam_active": 0,
  "seam_changed": 0,
  "seam_pending": 0,
  "cancelled_stale": 0,
  "deferred_budget": 0,
  "commit_latency_ms": 0.0
}
```

## 16.3 Per-level fields

Report for each LOD:

```text
origin
sample spacing
stable slots
active slots
current surface/empty/solid
pending
entering
leaving
count batches
emit batches
mesh bytes
last update frame
updates per second
```

## 16.4 Renderer fields

```text
visible regular commands
visible transition commands
culled regular
culled transition
active command lists per pass
commands per submission
argument upload bytes
resident upload bytes
culling CPU ms
argument build CPU ms
terrain depth GPU ms
terrain opaque GPU ms
```

## 16.5 Transition fields

```text
desired seam slots
published seam slots
pending seam slots
transition cells classified
active transition cells
vertices
indices
count GPU ms
emit GPU ms
count readback latency
generation mismatch rejects
coherence wait ms
```

## 16.6 Memory fields

```text
regular scratch
transition scratch
vertex pool used/peak/capacity
index pool used/peak/capacity
current regular allocations
pending regular allocations
current seam allocations
pending seam allocations
retired allocations
free range count
largest free range
managed allocation bytes/sec
```

## 16.7 Hitch trace

For any frame over 16.67, 33, or 50 ms, preserve a bounded history containing:

- frame time;
- known CPU/GPU categories;
- revision planned/committed;
- regular/seam submissions;
- readback completions;
- pool allocations/reclaims;
- command uploads;
- GC;
- unaccounted frame time.

Do not synchronously print a large hitch trace.

---

# 17. Success, failure, and expected performance

## 17.1 Protected Phase 3 reference

Reference run:

```text
Run: 20260809-025534
Resolution: 1524 × 911
CPU: Ryzen 7 9800X3D
GPU: RTX 5090
```

Important Phase 3 values:

```text
settled production render:
    average FPS       241.1
    frame p95         4.72 ms
    frame max         5.65 ms
    GPU p95           3.52 ms

fixed-LOD streaming:
    average FPS       220–233
    frame p95         5.4–6.8 ms
```

Use the clean rerun from 4R-0 as the official comparison baseline.

## 17.2 Relative performance gates

### One-LOD control

Phase 4 code configured as one LOD must remain within:

```text
frame p95 regression <= 5%
GPU p95 regression <= 5%
managed allocation regression <= 10%
```

### Regular clipbox, no transitions

At equivalent render settings:

```text
settled frame p95 <= Phase 3 baseline * 1.15
settled GPU p95   <= Phase 3 baseline * 1.20
```

### Full Phase 4 transitions

Relative to the same regular-only clipbox:

```text
settled frame p95 overhead <= 15%
settled GPU p95 overhead   <= 20%
```

A much larger sustained regression is an implementation failure, not an expected cost of LOD.

## 17.3 RTX 5090 development targets

For `B=8, L=4`, 1524×911, current simple terrain fixture:

```text
settled average FPS         >= 200
settled frame p95           <= 6.0 ms
settled frame max           <= 16.67 ms after warm-up
settled terrain GPU p95     <= 4.5 ms

movement frame p95          <= 8.0 ms
movement frame max          < 33 ms
frames over 50 ms           = 0
frames over 100 ms          = 0
movement 1% low             >= 120 FPS
movement 0.1% low           >= 60 FPS
```

These are adoption targets, not promises for every future terrain rule.

## 17.4 Latency targets

```text
near regular request-to-ready p95 <= 100 ms
full revision commit p95          <= 150 ms
stationary pending work           = 0
```

Old coherent terrain remaining visible means generation latency must not create holes.

## 17.5 Memory targets

At `B=8, L=4`:

```text
new permanent managed Phase 4 memory <= 16 MiB
transition GPU scratch               <= 32 MiB target
stationary managed allocation         approximately 0 B/s
streaming managed allocation          <= 1 MiB/s target
```

No 70+ MiB transition CPU scratch array.

After ten identical movement loops:

```text
pool retained-byte delta <= 2%
resident count delta     = 0
pending count            = 0
```

## 17.6 Functional success

Phase 4 succeeds only when all are true:

- exact regular and transition counts;
- no arbitrary 49-chunk or command-boundary limit;
- no missing regular chunks;
- no overlapping regular LOD volumes;
- no cracks or T-junction leaks;
- no duplicate transition owners;
- no stale seams;
- no silent capacity truncation;
- no CPU transition SDF/classification in production;
- no production geometry readback;
- bounded queues and memory;
- stationary zero-work invariant;
- negative coordinates, vertical movement, reversal, oscillation, and teleport pass;
- three clean benchmark runs are consistent;
- representative mid-range GPU behaves correctly.

## 17.7 Immediate failure conditions

Stop the current slice when any occur:

- a visible regular block disappears when radius/count increases;
- requested and committed active counts differ after settle;
- a slot is assigned to two world keys;
- regular AABBs overlap across LODs;
- a seam is omitted because a hard limit was reached;
- a request is dequeued and lost after reservation/allocation failure;
- stationary terrain produces clipbox/transition rebuild work;
- normal movement regenerates every block in a level;
- transition geometry uses incorrect units or origin;
- frame p95 exceeds the relative gate;
- repeated >33 ms movement hitches;
- managed allocation or memory grows unbounded;
- CPU visual fallback activates.

---

# 18. Required benchmark scenarios

Add a new suite version and retain the Phase 3 scenarios.

## Planner/correctness

```text
phase4_planner_counts
phase4_planner_reference_equivalence
phase4_negative_coordinates
phase4_vertical_movement
phase4_regular_coverage
phase4_no_lod_overlap
phase4_neighbor_difference
```

## Renderer

```text
phase4_indirect_1_to_1024
phase4_indirect_boundary_49
phase4_depth_opaque_parity
phase4_command_list_active_range
```

## Regular clipbox

```text
phase4_regular_b4_l2_stationary
phase4_regular_b4_l4_stationary
phase4_regular_b8_l4_stationary
phase4_regular_cardinal_streaming
phase4_regular_diagonal_streaming
phase4_regular_vertical_streaming
phase4_regular_reversal
phase4_regular_teleport
phase4_stationary_soak_60s
```

## Transition correctness

```text
phase4_transition_all_512_cases
phase4_transition_six_orientations
phase4_transition_plane
phase4_transition_sphere
phase4_transition_cave
phase4_transition_tangent_surface
phase4_transition_watertight_edges
phase4_transition_no_duplicate_faces
```

## Integrated movement

```text
phase4_integrated_cardinal
phase4_integrated_diagonal
phase4_integrated_vertical
phase4_integrated_boundary_oscillation
phase4_integrated_high_speed
phase4_integrated_teleport
phase4_integrated_return_origin_10_loops
phase4_integrated_memory_pressure
phase4_integrated_resource_recreation
```

## Terrain distributions

Every integrated campaign must test:

```text
flat plane
rolling terrain
steep slope
overhang
cave mouth
dense cave network
mostly empty
mostly solid
fragmented surface
```

---

# 19. Suggested file responsibilities

Adapt names to the repository, but keep responsibilities separate.

```text
Code/Voxels/Gpu/Clipbox/
    VoxelClipboxConfig.cs
    VoxelClipboxCoordinates.cs
    VoxelClipboxLevelState.cs
    VoxelClipboxReferencePlanner.cs
    VoxelClipboxRuntimePlanner.cs
    VoxelClipboxRevision.cs
    VoxelClipboxRegularSlot.cs
    VoxelClipboxTransitionSlot.cs
    VoxelClipboxValidation.cs

Code/Voxels/Gpu/Transitions/
    VoxelTransitionKey.cs
    VoxelTransitionScheduler.cs
    VoxelTransitionScratchArena.cs
    VoxelTransitionGoldenMesher.cs       // tests only
    VoxelTransitionValidation.cs

Code/Voxels/Gpu/Rendering/
    VoxelGpuTerrainRenderer.cs
    VoxelGpuIndirectCapabilityProof.cs

Assets/shaders/
    voxel_terrain_field.hlsl
    voxel_transition_density_cs.shader
    voxel_transition_count_cs.shader
    voxel_transition_scan_cs.shader
    voxel_transition_emit_vertices_cs.shader
    voxel_transition_emit_indices_cs.shader
```

The existing Phase 3 regular backend should be extended through narrow interfaces, not rewritten.

---

# 20. Codex implementation rules

1. Preserve a clean baseline commit.
2. One slice per implementation run.
3. Update this document’s status table after each passing slice.
4. Add tests before integrating production behavior.
5. Keep the failed first attempt disabled and separate.
6. Do not introduce Phase 5 edits.
7. Do not replace Transvoxel.
8. Do not add an octree.
9. Do not add skirts.
10. Do not use CPU visual meshing.
11. Do not use synchronous GPU readback.
12. Do not create per-block scene objects or meshes.
13. Do not hide failures by reducing coverage.
14. Do not increase magic capacities without a mathematical formula.
15. Do not continue when a pass gate fails.
16. Every benchmark report must include the clean revision and complete configuration.
17. Every optimization must have a before/after scenario.
18. Prefer fixed arrays, bitsets, spans, and stable slot tables over runtime HashSet/LINQ work.
19. Keep the Phase 3 fixed-LOD control path until final adoption.
20. Final Phase 4 adoption requires three clean reproducible runs.

---

# 21. Final completion definition

Phase 4 is complete when:

```text
true 3D non-overlapping clipbox shells
+ fixed toroidal regular slots
+ fixed generation-aware transition slots
+ direct canonical SDF sampling at every LOD
+ GPU regular and transition count/emission
+ exact persistent allocation
+ coherent replacement
+ bounded scheduling and memory
+ correct regular/seam frustum culling
+ no arbitrary draw-count limit
+ no cracks, holes, overlap, or stale seams
+ performance within the declared gates
```

Until then, report:

```text
Phase 4 rebuild is awaiting the 4R-9 mid-range GPU campaign.
```

Do not report Phase 4 complete merely because lookup-table proofs pass or because a small static scene looks correct.
