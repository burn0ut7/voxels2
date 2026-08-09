# Task: Eliminate Phase 4 LOD cracks and make the GPU clipbox watertight

Repository:
https://github.com/burn0ut7/voxels2

Branch:
phase4-rebuild

Inspect the actual branch before editing.

Do not begin Phase 5, implement terrain edits, replace Transvoxel, add skirts,
or perform unrelated performance refactors. Preserve the current GPU regular
mesher, persistent pools, indirect renderer, clipbox planner, and transition
GPU pipeline.

## Current assessment

The logical clipbox planner is mostly correct:

- viewer-centered 3D levels;
- non-overlapping coarse shells;
- power-of-two sample spacing;
- fixed toroidal slots;
- current/desired revisions;
- unique transition ownership;
- fine/coarse generation dependencies.

The production geometry is not yet proven watertight.

The leading source-level problem is inconsistent world-origin conversion.

The clipbox coordinate model expects:

    sampleOrigin = coordinate * ChunkSize * (1 << Lod)

but the runtime regular and transition paths use:

    sampleOrigin.z = (coordinate.z - 1) * ChunkSize * (1 << Lod)

This creates a different absolute vertical offset at every LOD:

    LOD0: -32 samples
    LOD1: -64 samples
    LOD2: -128 samples
    LOD3: -256 samples

This breaks physical nesting even if logical shell tests pass.

The second architectural problem is that transition meshes are emitted as
separate companions while the coarse regular mesh remains completely
unmodified at transition faces. A complete Transvoxel integration requires
the low-resolution regular boundary and transition cells to share one
geometric contract.

## Slice 1 — Unify canonical coordinates

Create one authoritative coordinate utility used by every GPU terrain path.

Required formulas:

    int SampleStep(int lod) => 1 << lod;

    Vector3Int CanonicalBlockOriginSamples(
        Vector3Int coordinate,
        int lod)
    {
        return coordinate * ChunkSize * SampleStep(lod)
             + GlobalTerrainOriginSamples;
    }

    Vector3 WorldFromCanonicalSamples(Vector3 sample)
    {
        return sample * VoxelSize;
    }

GlobalTerrainOriginSamples must be independent of LOD.

Remove every LOD-scaled `(coordinate.z - 1)` expression from:

- regular count requests;
- regular emission allocation descriptors;
- regular resident descriptors and bounds;
- transition fine origins;
- transition coarse origins;
- transition allocation descriptors;
- transition resident bounds;
- debug visualization;
- CPU/GPU seam fixtures.

Use SimplexBaseHeight for terrain elevation where possible.

Add hard assertions and tests:

    Origin(coarseCoordinate, lod + 1)
        == Origin(coarseCoordinate * 2, lod)

    Origin(c + axis, lod) - Origin(c, lod)
        == axis * ChunkSize * SampleStep(lod)

Test every axis, LOD0–LOD6, and negative coordinates.

Fail immediately on any origin mismatch.

## Slice 2 — Add an end-to-end production seam test

Do not change transition topology until this test exists.

For each face orientation, generate through the production GPU paths:

- one coarse regular block;
- the adjacent 2×2 fine regular blocks;
- all required transition meshes.

Use test-only geometry readback.

Merge the output in canonical sample space and analyze it as one mesh.

Required fixtures:

- plane;
- diagonal plane;
- sphere;
- cave mouth;
- tangent surface;
- current simplex terrain.

Required configurations:

- LOD0→LOD1;
- LOD1→LOD2;
- LOD2→LOD3;
- every face orientation;
- positive and negative block coordinates;
- VoxelSize 1;
- production VoxelSize;
- transition-face edges and corners;
- a full six-sided fine/coarse shell.

Report:

- maximum matching-vertex distance;
- unmatched internal boundary edges;
- T-junctions;
- nonmanifold edges;
- duplicate coplanar triangles;
- zero-area triangles;
- invalid indices;
- fine-boundary mismatches;
- coarse-boundary mismatches;
- transition-to-transition corner mismatches.

Pass requirements:

    unmatched internal boundary edges = 0
    T-junctions = 0
    nonmanifold edges = 0
    duplicate coplanar triangles = 0
    invalid indices = 0

Use a local-coordinate tolerance of:

    max(1e-4, VoxelSize * FineSampleStep * 1e-4)

This test must initially reproduce the current gap before the implementation
is considered trustworthy.

## Slice 3 — Make coarse regular meshes transition-aware

Add a six-bit TransitionFaceMask to every regular block request/resident.

The mask is set on a coarse block face when that face borders active blocks at
the next finer LOD.

Requirements:

- fine regular blocks remain ordinary regular meshes;
- the coarse regular mesh knows which faces are transition faces;
- coarse boundary vertices/cells are modified using a validated Transvoxel
  boundary rule;
- transition cells occupy the reserved transition layer;
- the coarse regular mesh must not occupy the same boundary volume as the
  transition cells;
- a transition-mask change invalidates the coarse regular mesh;
- no skirts, depth bias, overlapping duplicate surface, or epsilon-expanded
  geometry.

Port the boundary positioning rule from a validated Transvoxel implementation
rather than inventing an ad hoc inset.

A separate transition allocation/draw is allowed, but it must be published as
a coherent companion of:

    coarse regular generation
    fine regular generations
    transition generation
    transition face mask

The transition key must contain:

    coarse stable slot and generation
    face direction
    all relevant fine stable slots/generations
    rule version
    edit revision

## Slice 4 — Unify transition sample orientation

Replace duplicated branch-heavy face-coordinate calculations with one
authoritative transition orientation definition.

The CPU reference, transition count shader, transition emit shader, and tests
must derive sample positions from the same generated data or equivalent shared
definitions.

For every face and transition cell, verify that all 13 sample positions from
the GPU path exactly match the CPU reference.

Specifically validate:

- nine high-resolution face samples;
- four low-resolution interior samples;
- face winding;
- coarse-side direction;
- cell U/V orientation;
- adjacent transition-cell boundaries;
- four fine faces tiling one coarse face.

The GPU case-table test is not sufficient unless world-space sample positions
also match.

## Slice 5 — Share one canonical terrain field

Create:

    Assets/shaders/voxel_terrain_field.fxc

Move the procedural density/noise logic into this include.

Both regular and transition count/emission shaders must call:

    EvaluateTerrainDensity(canonicalSample)
    EvaluateTerrainGradient(canonicalSample, sampleStep)

Do not maintain separate copies of simplex/hash/terrain logic.

Add CPU/GPU sample-parity tests at deterministic random world coordinates,
negative coordinates, and all LOD sample spacings.

Evaluate whether visual density should remain unclamped.

If clamping remains, it must scale conservatively with sample spacing so a
coarse edge does not interpolate from artificially saturated ±8 values.

## Slice 6 — Preserve coherent publication

Keep the current old-revision fallback until all of these are complete:

- desired regular residents;
- desired transition-aware coarse residents;
- transition residents;
- generation dependencies.

Do not retire the previous coherent revision early.

Add explicit states:

    Planned
    RegularPending
    CoarseBoundaryPending
    TransitionPending
    ReadyToCommit
    Committed
    Failed

Normal Phase 4 settlement must require:

    blocked regular requests = 0
    blocked transition requests = 0
    dependency mismatches = 0
    desired regular = published regular
    desired transitions = published transitions
    clipbox revision pending = false

A capacity-limited or missing-transition world must not be reported as a
normal PASS.

## Slice 7 — Diagnostics and visualization

Add debug modes:

1. LOD colors.
2. Regular shell bounds.
3. Inner/outer clipbox bounds.
4. Transition face masks on coarse blocks.
5. Transition geometry only.
6. Missing transitions in red.
7. Dependency-mismatched transitions in orange.
8. Current versus desired revision.
9. Wireframe regular plus transition geometry.
10. Seam-error markers from test readback.

For every clipbox revision report:

- level origins;
- sample spacing;
- active regular count per level;
- changed slots per level;
- transition-aware coarse block count;
- desired/published/pending/blocked transitions;
- zero-geometry transitions;
- dependency mismatches;
- maximum origin alignment error;
- maximum seam position error;
- unmatched boundary-edge count;
- T-junction count;
- duplicate-triangle count;
- revision plan-to-commit latency;
- regular and transition mesh bytes;
- transition count/emit batches;
- regular and transition visible draw commands.

Add a bounded JSON hitch/seam report. Do not synchronously log every cell.

## Chunk-size contract

The current transition implementation assumes:

    ChunkSize = 32
    TransitionCellsPerAxis = 16

Either explicitly reject every other ChunkSize in the GPU clipbox backend or
derive:

    TransitionCellsPerAxis = ChunkSize / 2
    TransitionCellCount = TransitionCellsPerAxis²

Remove `% 16` and `/ 16` from production shaders if dynamic chunk size is
supported.

## Correctness acceptance

Phase 4 seam correctness passes only when:

- no visible crack remains after settlement;
- no logical LOD coverage gap exists;
- no LOD overlap exists outside intentional current/desired staging;
- adjacent active regular blocks differ by at most one LOD;
- every fine/coarse boundary has exactly one owner;
- every required transition is published;
- all six orientations pass;
- negative coordinates pass;
- shell corners and edges pass;
- end-to-end composite meshes are manifold;
- no geometry is hidden by skirts or overlap;
- production geometry readback remains zero.

If the scene still shows a crack, Phase 4 is not complete even if table,
planner, or isolated transition tests pass.

## Performance acceptance

Compare complete transitions against the same regular-only clipbox workload:

- settled average FPS >= 85% of regular-only;
- frame p95 regression <= 15%;
- GPU p95 regression <= 20%;
- no recurring frame over 16 ms after warmup;
- target maximum movement frame < 33 ms;
- no frame over 50 ms;
- stationary terrain-owned managed allocation approximately zero;
- no complete clipbox rebuild during ordinary one-block movement;
- no CPU visual meshing;
- no production geometry readback.

Correctness has priority over these optimizations.

## Stop conditions

Stop and report rather than continuing when:

- canonical parent/child origins do not align;
- the composite seam test has unmatched internal edges;
- coarse regular geometry overlaps the transition layer;
- a transition face is missing or duplicated;
- a normal scenario settles with blocked transition work;
- a proposed fix relies on skirts, depth bias, or duplicate overlap.

Deliver:

1. Root-cause report.
2. Before/after seam screenshots in LOD-color and wireframe modes.
3. Composite GPU seam validation report.
4. Coordinate-alignment test report.
5. Updated benchmark JSON/Markdown.
6. Clean Git revision.
7. A concise statement of whether Phase 4 is geometrically complete.

Do not proceed to terrain editing until these gates pass.