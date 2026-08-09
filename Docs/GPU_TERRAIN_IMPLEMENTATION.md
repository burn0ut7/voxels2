# GPU-Resident Smooth Voxel Terrain — Codex Implementation Specification

## Purpose

This document is the standalone implementation handoff for the GPU visual-terrain architecture in `burn0ut7/voxels2`.

A new Codex session should be able to use this file without any prior conversation. It defines:

- the target architecture;
- the expected repository starting point;
- the next required implementation phase;
- concrete C# and shader responsibilities;
- data contracts and lifetime rules;
- implementation order;
- tests, diagnostics, and stop/go gates;
- the roadmap for later LOD, editing, and collision work.

This is an implementation specification, not a request to redesign the terrain system.

## Codex execution contract

When implementing from this document:

1. Inspect the actual repository before editing. File names below describe the intended responsibilities; adapt to harmless naming differences, but do not change the architecture without a measured blocker.
2. Build the project and run the existing GPU Transvoxel proof before modifying it.
3. Implement only the next incomplete phase. Do not skip ahead to clipbox LOD, transition cells, GPU culling, or sparse edit baking.
4. Preserve the existing proof as a diagnostic/conformance harness. Production code must be separate.
5. Compile after each implementation slice and fix all new warnings, shader errors, and console errors before continuing.
6. Do not introduce a CPU visual fallback on the GPU branch.
7. Do not use synchronous GPU readback, `Graphics.FlushGPU()`, or a fence wait during normal play.
8. Do not read generated visual vertices or indices back to C# during normal play or performance measurement.
9. Keep all queues, buffers, pools, and work per frame explicitly bounded.
10. Update this document's status table and the benchmark schema when a phase is completed.
11. If an s&box API does not behave as expected, reduce it to a minimal capability test, record the result, and implement the simplest supported architecture that preserves the invariants below.
12. Avoid unrelated refactors while a phase gate is open.

Recommended instruction for a new Codex chat:

> Read `Docs/GPU_TERRAIN_IMPLEMENTATION.md`, inspect the repository, and implement the next incomplete phase exactly as specified. Start by reporting the current code-to-spec gaps. Do not implement later phases or change the terrain algorithm. Compile and validate each slice before continuing.

---

# 1. Product Goal

Build a large, smooth, editable voxel terrain system for s&box where the expensive visual work scales on the GPU without moving gameplay authority to the GPU.

The two primary visual costs to move off the CPU are:

1. dense procedural SDF/material evaluation for visible terrain;
2. visual Transvoxel classification and mesh generation.

The system must support:

- caves, overhangs, and non-heightmap terrain;
- large streaming worlds;
- real-time topology-changing edits;
- Transvoxel LOD transitions;
- bounded CPU time, GPU time, and VRAM;
- headless authoritative servers;
- CPU collision and gameplay queries near relevant players.

The target is not maximum GPU utilization. The target is lower CPU terrain pressure, bounded request-to-visible latency, stable frame pacing, and bounded memory at equivalent visual coverage.

---

# 2. Non-Negotiable Architecture

```text
CPU/server canonical terrain
    world seed
    procedural-rule version
    macro-world descriptors
    ordered sparse edits
    baked sparse edit bricks
    gameplay queries and validation
    nearby collision
                |
                +---- GPU client visual terrain
                        requested visual blocks
                        procedural SDF/material evaluation
                        Transvoxel classification and emission
                        persistent GPU geometry
                        terrain rendering
```

## 2.1 CPU authority

The CPU/server owns:

- world seed and procedural-rule version;
- biome regions, POIs, roads, and authored macro descriptors;
- authoritative edit IDs, ordering, validation, replication, and persistence;
- gameplay SDF/material queries;
- visual request and LOD policy;
- request priorities and frame budgets;
- the initial persistent vertex/index range allocator;
- resident lifetime and eviction policy;
- nearby CPU collision;
- benchmark orchestration and report persistence.

## 2.2 GPU visual ownership

The client GPU owns:

- visual SDF/material evaluation at requested world coordinates;
- block empty/solid/surface reduction;
- regular and transition-cell classification;
- local prefix scans and geometry counts;
- visual vertex/index emission;
- persistent visual geometry storage;
- terrain rendering;
- optional later GPU culling and command generation.

## 2.3 Single visual system invariant

During measured GPU runtime:

- the GPU backend is the only visual terrain backend;
- CPU Transvoxel may generate collision and frozen reference fixtures only;
- CPU Transvoxel may not publish visual meshes;
- failed GPU work may delay or reduce visual coverage, but may not activate CPU visual generation.

## 2.4 Algorithm decision

Keep Transvoxel.

Do not replace it with:

- Marching Cubes;
- MC33;
- Dual Contouring;
- FlexiCubes;
- a sparse voxel octree renderer;
- ray-marched primary terrain;
- mesh shaders or meshlets as a prerequisite.

Reuse the current project's:

- regular-cell lookup tables;
- corner numbering;
- edge ownership;
- interpolation convention;
- winding behavior;
- gradient normals;
- transition-cell tables when Phase 4 begins.

---

# 3. Expected Repository Starting Point

Codex must verify this against the current checkout rather than blindly assume it.

Expected relevant files include:

```text
Code/Voxels/VoxelManager.cs
Code/Voxels/VoxelGpuTransvoxelProof.cs
Code/Voxels/VoxelGpuMeshPoolLifecycleProof.cs
Code/Voxels/VoxelTerrainBenchmark.cs
Code/Voxels/VoxelTransvoxelMesher.cs
Code/Voxels/VoxelTransvoxelTables.cs

Assets/shaders/voxel_gpu_transvoxel_clear_cs.shader
Assets/shaders/voxel_gpu_transvoxel_density_cs.shader
Assets/shaders/voxel_gpu_transvoxel_classify_cs.shader
Assets/shaders/voxel_gpu_transvoxel_scan_cs.shader
Assets/shaders/voxel_gpu_transvoxel_vertices_cs.shader
Assets/shaders/voxel_gpu_transvoxel_indices_cs.shader
Assets/shaders/voxel_gpu_transvoxel.shader
```

Expected functional state:

- `VoxelManager` still runs the CPU visual world during ordinary gameplay.
- `VoxelGpuTransvoxelProof` is an opt-in diagnostic object.
- The proof generates a flat SDF on the GPU.
- The proof validates regular-cell Transvoxel output against a CPU reference.
- The proof supports batches of `1`, `8`, `32`, and `128` blocks.
- The proof has batched density, classification, local scans, counts, and shared batch output.
- The proof performs diagnostic readback and is not a production no-readback backend.
- `VoxelGpuMeshPoolLifecycleProof` models allocation on the CPU but is not the production GPU geometry pool.
- Production LOD and transition cells are not implemented.

If the current code already contains part of a later phase, preserve working code, verify it against the contracts below, and continue from the first unmet gate.

---

# 4. Phase Status

| Phase | Required state |
|---|---|
| Phase 0 — Baseline and contracts | Complete. CPU baseline, versioned request/report contracts, and benchmark corpus are maintained. |
| Phase 1 — Single-block regular-cell proof | Complete. The proof remains an opt-in conformance harness. |
| Phase 2A — Batched scratch proof | Complete. The proof remains a batch/conformance harness. |
| Phase 2B — Persistent fixed-LOD GPU backend | Complete. Persistent pools, asynchronous count readback, transactional allocation, generation-safe publication, and bounded multi-draw are live. |
| **Phase 3A — Production render integration** | **Complete for the current s&box renderer.** Standard lit Forward/Depth terrain rendering is live; the engine's generic indirect path reports `IndirectFirstInstance=false`, so the bounded world-space vertex fallback is retained and explicitly reported. |
| **Phase 3B — Fixed-LOD movement streaming** | **Complete.** Actual player observers drive same-frame desired-set deltas, bounded queues, persistent residents, CPU frustum culling, and the required GPU traversal scenarios. |
| Phase 4 — 3D clipbox LOD and transitions | After fixed-LOD streaming passes. |
| Phase 5 — Sparse edits and CPU collision integration | After LOD correctness. |
| Phase 6 — Final adoption campaign | After all mandatory behavior exists. |

Do not start Phase 4 merely because the current batch proof renders multiple blocks.

---

# 5. Target Production Data Flow

```text
CPU request planner
    BlockKey + request generation + priority + rule/edit versions
                         |
                         v
GPU PASS A — bounded scratch batch
    evaluate density/material
    reduce block min/max
    classify regular cells
    scan edge flags and cell index counts
    produce per-block vertex/index counts
                         |
                         v
bounded asynchronous count-result readback
    request ID + generation + counts + surface state + flags
                         |
                         v
CPU persistent mesh allocator
    reject stale results
    allocate vertex and index ranges transactionally
    preserve old resident if replacement allocation fails
                         |
                         v
upload allocation descriptors
                         |
                         v
GPU PASS B
    emit vertices into assigned ranges
    emit indices into assigned ranges
    apply resource barriers
                         |
                         v
resident publication
    publish matching generation only
    switch draw commands to new resident
    retire previous allocation
                         |
                         v
CPU frustum culling initially
    compact indirect command array
                         |
                         v
one indexed multi-draw per applicable view/pass
```

Normal visual geometry never crosses from GPU to CPU.

---

# 6. Shared Data Contracts

Use explicit, versioned data contracts. CPU and HLSL layouts must match exactly.

Requirements for GPU-facing structs:

- use sequential fields with 4-byte packing;
- prefer sizes that are multiples of 16 bytes;
- avoid C# `bool` in GPU structs;
- use `uint` flags;
- add debug assertions for `Marshal.SizeOf<T>()`;
- centralize matching C#/HLSL definitions or document every offset;
- include a layout self-test in diagnostic builds.

The exact names may change, but the information and invariants may not.

## 6.1 Block key

```csharp
public readonly record struct VoxelVisualBlockKey(
    Vector3Int Coordinate,
    int Lod,
    int RuleVersion
);
```

A world block is uniquely defined by:

- integer block coordinate;
- LOD level;
- procedural-rule version.

Edit/world generation is tracked separately so the same spatial key can have multiple requested generations over time.

Neighboring blocks must derive shared boundary sample coordinates from integer world coordinates. Never accumulate floating-point origins from neighboring blocks.

## 6.2 GPU block request

Conceptual 64-byte record:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuVoxelBlockRequest
{
    public int BlockX;
    public int BlockY;
    public int BlockZ;
    public uint RequestId;

    public uint Lod;
    public uint RequestGeneration;
    public uint RuleVersion;
    public uint EditRevision;

    public uint EditOffset;
    public uint EditCount;
    public uint Flags;
    public uint Reserved0;

    public float VoxelSize;
    public float SdfClampDistance;
    public uint PriorityClass;
    public uint Reserved1;
}
```

`RequestId` maps the compact GPU result back to a CPU job. Do not copy a large key or managed object through readback when an ID is sufficient.

## 6.3 Count result

Conceptual 32-byte record:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuVoxelBlockCountResult
{
    public uint RequestId;
    public uint RequestGeneration;
    public uint VertexCount;
    public uint IndexCount;

    public uint SurfaceKind;      // Empty, Solid, Surface, Invalid
    public uint ActiveCellCount;
    public uint Flags;            // Overflow, invalid table access, etc.
    public uint Reserved;
}
```

Rules:

- Pass A writes one result per request slot.
- Empty and solid blocks report zero geometry.
- A result never authorizes emission by itself.
- CPU generation validation happens before allocation.
- Production count readback is asynchronous and bounded.

## 6.4 Allocation descriptor

Conceptual 64-byte record:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuVoxelAllocationDescriptor
{
    public uint RequestId;
    public uint RequestGeneration;
    public uint ResidentSlot;
    public uint AllocationGeneration;

    public uint VertexOffset;
    public uint VertexCapacity;
    public uint IndexOffset;
    public uint IndexCapacity;

    public uint ExpectedVertexCount;
    public uint ExpectedIndexCount;
    public uint Flags;
    public uint Reserved0;

    public int BlockX;
    public int BlockY;
    public int BlockZ;
    public uint Lod;
}
```

No Pass B emission is submitted unless:

```text
VertexCapacity >= ExpectedVertexCount
IndexCapacity  >= ExpectedIndexCount
```

## 6.5 Resident descriptor

Conceptual record:

```csharp
public struct VoxelGpuResidentDescriptor
{
    public VoxelVisualBlockKey Key;
    public uint PublishedGeneration;
    public uint AllocationGeneration;

    public uint VertexOffset;
    public uint VertexCount;
    public uint IndexOffset;
    public uint IndexCount;

    public BBox Bounds;
    public uint ResidentSlot;
    public uint Flags;
    public long LastVisibleFrame;
    public float EvictionScore;
}
```

The GPU-facing resident descriptor may use a packed 16-byte-aligned representation. The CPU representation may contain additional bookkeeping.

## 6.6 Indirect draw command

For each resident draw:

```text
IndexCount     = resident.IndexCount
InstanceCount  = 1
FirstIndex     = resident.IndexOffset
VertexOffset   = resident.VertexOffset when indices are block-local
FirstInstance  = resident.ResidentSlot
```

Preferred production representation:

- indices stored local to each block allocation;
- `VertexOffset` supplies the vertex range base;
- `FirstInstance` identifies the resident descriptor.

The multi-draw capability test must validate `VertexOffset` and `FirstInstance`. If either is not correctly exposed by the installed s&box path:

- use global vertex indices with `VertexOffset = 0`; and/or
- add a packed resident slot to the vertex as a temporary fallback.

Do not fall back to one C# draw call per block.

---

# 7. Runtime State Machine

Each visual request follows this state machine:

```text
Missing
  -> Requested
  -> Counting
  -> CountReadbackPending
  -> CountReady
  -> AwaitingAllocation
  -> Allocated
  -> EmissionSubmitted
  -> ReadyToPublish
  -> Resident
  -> Visible
  -> Dirty/Replacing
  -> Retired
  -> Reclaimed
```

Required rules:

1. Every transition is keyed by block key and request generation.
2. A newer request makes older unfinished generations stale.
3. Stale count results are discarded before allocation.
4. Stale emission work may complete, but it may never publish.
5. Replacements use a different safe allocation from the currently visible resident.
6. The old resident remains visible while counting, allocation, or emission is pending.
7. Failed replacement allocation leaves the old resident untouched.
8. The old allocation is retired only after the replacement becomes the selected resident.
9. A retired range is not reused until the GPU can no longer reference it.
10. Empty/solid replacement may publish a zero-geometry resident, then retire old geometry.
11. No normal state transition waits synchronously for the GPU.

---

# 8. Phase 2B — Persistent Fixed-LOD GPU Backend

## Objective

Convert the existing batched Transvoxel proof into a production-capable fixed-LOD visual backend with:

- bounded scratch buffers;
- count-before-allocation;
- asynchronous count metadata;
- persistent shared vertex/index pools;
- safe emission;
- generation-safe publication;
- replacement and reclamation;
- a static fixed-LOD resident renderer;
- no normal visual geometry readback.

Do not implement movement streaming, production LOD transitions, GPU culling, or terrain edits until the static persistent backend passes.

## 8.1 Required production files

Create or equivalent:

```text
Code/Voxels/Gpu/VoxelGpuTerrainBackend.cs
Code/Voxels/Gpu/VoxelGpuBatchScheduler.cs
Code/Voxels/Gpu/VoxelGpuScratchArena.cs
Code/Voxels/Gpu/VoxelGpuMeshPool.cs
Code/Voxels/Gpu/VoxelGpuRangeAllocator.cs
Code/Voxels/Gpu/VoxelGpuResidentTable.cs
Code/Voxels/Gpu/VoxelGpuTerrainRenderer.cs
Code/Voxels/Gpu/VoxelGpuTerrainDiagnostics.cs
Code/Voxels/Gpu/VoxelGpuContracts.cs
Code/Voxels/Gpu/VoxelGpuCapabilities.cs
```

Shader source files should be production-specific rather than silently mutating proof behavior:

```text
Assets/shaders/voxel_gpu_count_clear_cs.shader
Assets/shaders/voxel_gpu_density_material_cs.shader
Assets/shaders/voxel_gpu_block_reduce_cs.shader
Assets/shaders/voxel_gpu_classify_regular_cs.shader
Assets/shaders/voxel_gpu_scan_regular_cs.shader
Assets/shaders/voxel_gpu_block_totals_cs.shader
Assets/shaders/voxel_gpu_emit_vertices_cs.shader
Assets/shaders/voxel_gpu_emit_indices_cs.shader
Assets/shaders/voxel_gpu_terrain.shader
```

Reuse common tables and helper code where the shader system permits it. Do not manually edit generated `.shader_c` artifacts as the source of truth.

## 8.2 Keep the proof separate

`VoxelGpuTransvoxelProof` remains responsible for:

- diagnostic readback;
- CPU/GPU topology comparison;
- all 256 regular cases;
- selected batch offset validation;
- proof-only experiments.

Rename proof-facing terminology from `shared mesh pool` to `shared batch output` unless it is actually using the production persistent pool.

Production runtime must not instantiate the proof object.

## 8.3 Capability spikes before backend integration

Implement small, isolated tests before depending on engine behavior.

### A. Asynchronous readback test

Validate:

- `GpuBuffer<T>.GetDataAsync` or the current installed equivalent;
- multiple readbacks in flight;
- completion without blocking the main thread;
- safe buffer-slot reuse only after completion;
- cancellation/stale-result handling;
- behavior during component disable, scene reload, and device/resource recreation.

### B. Sixteen-command multi-draw test

Create one vertex buffer, one index buffer, and 16 indirect records with:

- distinct index ranges;
- distinct vertex offsets;
- distinct `FirstInstance` values;
- multiple nonzero draws;
- zero-count records;
- explicit count and stride;
- forward rendering;
- depth rendering;
- resource transitions after buffer updates.

Pass only if the correct geometry and instance IDs render in one bounded multi-draw submission.

### C. Safe retirement test

Determine the current engine-supported way to know when a submitted range may be reused.

Preferred order:

1. explicit engine submission/fence completion primitive that does not block;
2. engine-provided deferred destruction/release mechanism;
3. conservative frame-epoch ring based on the maximum frames in flight plus a safety margin.

Do not call a synchronous fence wait or `Graphics.FlushGPU()` during normal operation.

Record the selected mechanism in `VoxelGpuCapabilities` and in benchmark identity.

## 8.4 Scratch arena

`VoxelGpuScratchArena` owns fixed-capacity reusable buffers for a configured maximum batch size.

Minimum scratch resources:

```text
BlockRequests
DensitySamples
MaterialSamples or packed density/material samples
BlockSurfaceKinds
Cells
EdgeFlags
EdgeLocalOffsets
CellLocalOffsets
EdgeGroupSums
CellGroupSums
BlockCounts
CountResults
AllocationDescriptors
DebugFlags
```

Rules:

- one arena is sized for a bounded batch, not resident world size;
- no per-block scratch buffer creation;
- no capacity derived from a flat reference mesh;
- every shader checks request/batch bounds;
- batch capacities to test: `16`, `32`, `64`, `128`;
- choose the production default from measured latency, GPU time, scratch VRAM, and frame pacing;
- likely initial default: `32` or `64`;
- `128` remains a stress option unless measurements justify it.

## 8.5 GPU Pass A — count only

Pass A must not write final vertices or indices.

### Pass A.1 — clear request ranges

Clear only the active request range, not the entire maximum arena when avoidable.

Reset:

- cell records;
- edge flags;
- group sums;
- per-block counts;
- result flags.

### Pass A.2 — density and material evaluation

For every requested sample:

```text
Base procedural field
+ macro descriptors
+ baked edit brick, when Phase 5 exists
+ recent edit operations, when Phase 5 exists
```

Phase 2B initially supports the current flat rule plus `TerrainRuleStressV1`.

Use integer world sample coordinates derived from:

```text
block coordinate
LOD sample spacing
local sample coordinate
```

Do not derive shared samples from accumulated floating-point block origins.

### Pass A.3 — block reduction

Compute conservative per-block min/max SDF:

```text
minSdf > 0 -> Empty
maxSdf < 0 -> Solid
otherwise  -> Surface
```

`ClassifyRegularCS` must early-out for empty and solid blocks.

The first version may dispatch across all request cells and early-out by block kind. Add surface-block compaction/indirect dispatch only after profiling proves it useful.

### Pass A.4 — regular-cell classification

For surface blocks:

- compute regular case code;
- determine cell index count;
- set owned edge flags;
- increment diagnostic active-cell counts;
- never emit geometry.

### Pass A.5 — local scans

Keep the existing workgroup-local 256-thread scan approach unless profiling proves a better group size.

Perform:

- exclusive scan of edge flags within each group;
- exclusive scan of per-cell index counts within each group;
- group totals per block.

### Pass A.6 — block totals

Produce per-block vertex and index totals without the current `O(batch²)` loop over all previous blocks.

Pass A does not need global batch output offsets. It only needs independent block totals for CPU allocation.

Therefore:

- remove the cross-block prefix-allocation pass from the production count path;
- write one `GpuVoxelBlockCountResult` per request slot;
- preserve local edge/cell offsets needed by Pass B or recompute them in Pass B if that is measurably cheaper than retaining scratch across frames.

### Scratch-retention decision

Because count readback introduces a frame delay, choose one explicit strategy:

**Strategy A — retain classified scratch until allocation returns**

- lower recomputation;
- higher scratch residency;
- limits count batches in flight.

**Strategy B — rerun deterministic Pass A before Pass B**

- simpler scratch reuse;
- doubles density/classification work for accepted blocks.

Default requirement: implement Strategy A with a bounded ring of scratch slots unless measured scratch memory makes it impractical. Each in-flight scratch slot is owned by one batch until Pass B submission or cancellation.

Do not silently overwrite a scratch slot whose count result or emission still depends on it.

## 8.6 Bounded asynchronous count readback

Implement a ring of `CountReadbackSlot` objects.

Each slot records:

```text
SlotState
BatchId
ScratchSlotId
RequestCount
SubmitFrame
Request IDs and generations
Readback task/callback
```

Requirements:

- configurable maximum readbacks in flight;
- no `GetData` in normal runtime;
- no polling that blocks;
- completion processed on the main thread or a safe game task continuation;
- component disable/disposal invalidates callbacks by owner generation;
- stale results are discarded before allocation;
- count readback bytes are reported;
- readback latency and queue age are reported;
- scheduler stops submitting Pass A when no readback/scratch slot is available;
- count records are batched in one transfer per scratch batch where possible.

Count readback is the only production GPU-to-CPU terrain transfer permitted in Phase 2B.

## 8.7 Persistent mesh pool

### Range allocators

`VoxelGpuMeshPool` owns two independent allocators:

```text
VertexRangeAllocator
IndexRangeAllocator
```

Allocate in element units internally and expose byte metrics.

First implementation:

- sorted free-range list;
- first-fit or best-fit chosen explicitly;
- alignment sufficient for the installed buffer API, with 256-byte accounting alignment for metrics unless the API requires another value;
- adjacent free-range coalescing;
- no compaction initially.

### Transactional allocation

Allocating a mesh is one transaction:

1. allocate vertex range;
2. allocate index range;
3. if either fails, roll back both;
4. create an allocation handle with a monotonically increasing generation;
5. do not modify the currently visible resident.

### Allocation handle

```csharp
public readonly record struct VoxelGpuAllocationHandle(
    int Id,
    uint Generation,
    int VertexOffset,
    int VertexCapacity,
    int IndexOffset,
    int IndexCapacity
);
```

### Replacement

For replacement generation `N`:

- current resident remains active;
- allocate a separate pending handle;
- emit into the pending handle;
- publish only if request generation still equals `N`;
- atomically select the new resident in CPU bookkeeping/command construction;
- enqueue old handle for deferred release.

### Eviction

Eviction priority should initially consider:

```text
not visible
outside desired set
distance from primary observer
age since visible
LOD priority later
recent edit protection later
```

Never evict the old resident merely to allocate its replacement unless the scheduler explicitly accepts a visible hole. The player safety region must not accept that behavior.

### Deferred release

A deferred release record contains:

```text
AllocationHandle
RetireSubmissionSerial or RetireFrame
SafeReuseSerial or SafeReuseFrame
Reason
```

Ranges become free only after the selected safe-retirement mechanism says they cannot be referenced by compute or draw commands.

### Required metrics

- vertex pool capacity/used/free/high-water;
- index pool capacity/used/free/high-water;
- live allocations;
- pending replacements;
- retired allocations;
- internal and external fragmentation;
- largest free range;
- allocation failures;
- eviction retries;
- deferred-release age;
- stale generation rejections.

Refactor `VoxelGpuMeshPoolLifecycleProof` to test the actual production allocator classes. Do not keep a second allocator implementation solely for the proof.

## 8.8 GPU Pass B — safe emission

Pass B consumes:

- the original request batch/scratch slot;
- accepted allocation descriptors;
- local scan results from Pass A;
- regular lookup tables.

### Vertex emission

For each owned edge:

- compute interpolation from SDF values;
- compute gradient normal using the halo;
- write only inside the descriptor's vertex range;
- write block-local positions, not large absolute world positions;
- validate output index against `VertexCapacity` in diagnostic builds;
- set a debug flag instead of writing when invalid.

Initially reuse the current vertex format to minimize risk. Packing normals/materials is a later measured optimization.

### Index emission

Preferred behavior:

- write indices local to the block allocation;
- use indirect `VertexOffset` during draw;
- write only inside the descriptor's index range;
- validate all local indices `< ExpectedVertexCount` in diagnostic builds.

If the capability test proves `VertexOffset` unusable, write global indices and document that fallback.

### Bounds

Use conservative CPU-known block bounds in Phase 2B. Do not add a GPU bounds readback.

A later phase may calculate tighter GPU bounds if profiling proves conservative bounds harm culling.

### Submission ordering

Use the graphics queue or an explicitly synchronized queue for the first implementation.

Required ordering:

```text
Pass B UAV writes
-> UAV/resource barriers
-> terrain vertex/index consumption
```

Do not depend on accidental frame timing.

## 8.9 Publication without normal readback

The CPU already knows:

- accepted counts;
- allocation ranges;
- request generation;
- block bounds.

Therefore production publication does not need a geometry or completion-status readback.

First implementation:

1. submit Pass B in frame `F`;
2. keep the old resident selected for frame `F`;
3. after the Pass B command stream is enqueued, mark the replacement `EmissionSubmitted`;
4. select the replacement for draw-command construction no earlier than frame `F + 1`, relying on guaranteed queue ordering from the validated capability path;
5. retire the old allocation using the safe-retirement mechanism;
6. diagnostic builds may optionally read a small status buffer asynchronously, but rendering may not depend on it.

If the engine uses a separate asynchronous compute queue, explicit cross-queue synchronization is mandatory. Do not assume next-frame ordering across queues.

## 8.10 Resident table

`VoxelGpuResidentTable` owns:

- fixed-capacity resident slots;
- map from block key to resident slot;
- current resident generation;
- pending replacement generation;
- current allocation;
- pending allocation;
- bounds and visibility timestamps;
- zero-geometry empty/solid residents;
- eviction state.

Resident slot IDs must remain stable while a resident is selected because they are used by indirect commands and shader descriptor lookup.

When a slot is reused, increment a slot generation to prevent stale references.

## 8.11 Static fixed-LOD renderer

Before movement streaming, render a deterministic static set of persistent residents.

`VoxelGpuTerrainRenderer` should be one scene render object/component, not one object per block.

Initial rendering steps:

1. gather selected resident descriptors;
2. CPU frustum-cull conservative block bounds;
3. create a compact indirect command array;
4. upload command metadata only;
5. submit one indexed multi-draw for the view/pass;
6. render directly from the persistent vertex/index buffers.

The renderer must not:

- create `Mesh` or `Model` objects per block;
- upload geometry after Pass B;
- call one draw per block;
- read visibility or geometry back from the GPU.

## 8.12 Phase 2B integration with `VoxelManager`

Add an explicit visual-backend boundary instead of embedding all GPU code in `VoxelManager`.

Suggested interface:

```csharp
internal interface IVoxelVisualBackend : IDisposable
{
    void Reset(in VoxelVisualWorldConfiguration configuration);
    void SubmitDesiredBlocks(ReadOnlySpan<VoxelVisualBlockRequest> requests);
    void MarkDirty(ReadOnlySpan<VoxelVisualBlockKey> blocks, uint editRevision);
    void Update(in VoxelVisualFrameContext context);
    bool IsSettled { get; }
    VoxelVisualBackendDiagnostics CaptureDiagnostics();
}
```

Branch policy:

- CPU branch uses the CPU visual backend;
- GPU branch uses the GPU visual backend;
- do not create an automatic fallback chain;
- dedicated server constructs no visual backend or a no-op headless implementation with no GPU resources.

Collision remains separate from `IVoxelVisualBackend`.

## 8.13 Phase 2B test matrix

### Correctness

- flat plane;
- empty block;
- solid block;
- single corner;
- sphere;
- border-crossing surface;
- deterministic cave;
- fragmented surface;
- selected regular cases, then all 256 cases through the proof;
- first, middle, and last surface block in a batch;
- all block offsets in a small batch;
- every emitted index in range;
- no non-finite vertex values;
- no degenerate or reversed triangles beyond declared Transvoxel behavior.

### Allocation/lifetime

- allocate, publish, replace, retire, reclaim;
- replacement while old resident is visible;
- replacement allocation failure;
- stale count result;
- stale Pass B submission;
- stale publication attempt;
- eviction while count is pending;
- eviction while emission is pending;
- component disable during readback;
- scene reload/resource recreation;
- deliberate vertex exhaustion;
- deliberate index exhaustion;
- fragmented free space;
- repeated out-and-back resident sets;
- return-to-origin memory stability.

### Batch capacities

```text
16
32
64
128
```

### Server

- dedicated server starts without `ComputeShader`, `GpuBuffer`, `CommandList`, or scene-render object creation;
- gameplay queries and collision continue to work.

## 8.14 Phase 2B pass gate

Phase 2B passes only when all are true:

- production code is separate from the proof harness;
- Pass A emits no geometry;
- every Pass B job has a valid allocation with sufficient capacity;
- out-of-range geometry writes are structurally prevented;
- normal vertex/index readbacks are zero;
- synchronous count/statistics readbacks are zero;
- asynchronous count queues and scratch slots remain bounded;
- no per-block GPU buffers are created;
- no per-block visual objects are created;
- stale generations never replace newer generations;
- replacement failure preserves the old resident;
- retired ranges are not reused early;
- pool usage never exceeds configured budgets;
- deliberate exhaustion applies bounded backpressure/eviction rather than corruption or CPU fallback;
- repeated equivalent resident routes settle to stable retained memory;
- a static persistent fixed-LOD resident set renders through one bounded multi-draw path;
- dedicated server contains no GPU visual resources;
- the project compiles with a clean console.

Phase 2B does not require movement streaming, LOD transitions, GPU culling, or edits.

---

# 9. Phase 3A — Production Render Integration

## Objective

Replace the proof material and proof draw path with terrain rendering that behaves like normal world geometry.

## 9.1 Production shader

Create `voxel_gpu_terrain.shader` with at least:

```hlsl
MODES
{
    Forward();
    Depth();
}
```

Requirements:

- Source 2/s&box standard lighting path;
- depth prepass and G-buffer data;
- dynamic shadows;
- fog and atmosphere;
- base material ID;
- secondary material ID;
- blend weight;
- world-space normal;
- roughness/metalness policy;
- TAA and configured upscalers;
- debug modes.

Remove hard-coded sun direction and fake proof lighting.

## 9.2 Camera-relative rendering

Store vertices block-local.

Store integer block coordinate in the resident descriptor.

Reconstruct position:

```text
(blockCoordinate - cameraBlockCoordinate) * blockWorldSize
+ localVertexPosition
+ cameraLocalOffset
```

Preferred descriptor selection:

```text
FirstInstance = ResidentSlot
SV_InstanceID -> resident descriptor
```

Validate this in the multi-draw capability test. The current s&box generic
`DrawIndexedInstancedIndirect` path does not expose `FirstInstance` reliably to
the vertex-input path (`IndirectFirstInstance=false`), so Phase 3 keeps the
capacity-bounded world-space vertex fallback. The capability and benchmark
reports expose this explicitly; block-local/camera-relative addressing remains
a later engine-capability follow-up rather than silently claiming support.

## 9.3 Render views

Use shared residents and geometry for:

- main/depth view;
- shadow cascades/views;
- reflection/refraction views when required.

Each view may have a separate command buffer, but auxiliary views do not create independent terrain residency or LOD trees.

## 9.4 Phase 3A tests

- opaque main view;
- depth prepass;
- G-buffer normal/roughness inspection;
- all enabled shadow cascades;
- fog/atmosphere;
- decals where supported;
- TAA/upscaler motion and stability;
- large camera coordinates;
- camera-origin rebasing;
- zero-count indirect commands;
- buffer and renderer recreation.

## 9.5 Phase 3A pass gate

- standard terrain lighting is correct;
- depth/G-buffer output is correct;
- shadows are correct;
- fog/post-processing works;
- no hard-coded proof lighting remains;
- no per-block draw call exists;
- no per-block visual object exists;
- camera-relative rendering remains stable at large coordinates.

---

# 10. Phase 3B — Fixed-LOD Movement Streaming

## Objective

Connect the current observer-driven fixed-LOD desired set to the production GPU backend without changing world coverage or adding LOD.

## 10.1 Request planner

Preserve current fixed-LOD coverage for the first comparison.

The request planner emits:

- desired block keys;
- generation;
- distance/priority;
- whether the request is new, dirty, replacing, or already resident.

Deduplicate by block key and generation.

Cancel requests that leave the desired set before publication.

## 10.2 Scheduler priority

1. missing blocks in the player safety region;
2. visible missing blocks nearest the observer;
3. blocks in movement direction;
4. dirty/replacing blocks when edits are later integrated;
5. return-to-origin cache reuse;
6. prefetch/background work.

Apply age promotion to prevent starvation.

## 10.3 Bounded queues

Configure caps for:

- desired/request queue;
- Pass A batches;
- scratch slots;
- count readbacks;
- allocation-ready jobs;
- Pass B batches;
- pending publications;
- retired allocations.

When saturated, stop accepting lower-priority work. Do not grow queues without a cap.

## 10.4 Culling and command submission

First version:

- CPU frustum-cull resident block bounds;
- compact commands by view/pass;
- upload command metadata;
- one multi-draw or the smallest supported bounded submission count.

GPU culling is not required for this phase.

## 10.5 Streaming tests

- stationary cold settle;
- straight-line traversal;
- diagonal traversal;
- infinity traversal;
- high-speed reversal;
- teleport/outlier movement;
- camera frustum sweep;
- return to origin;
- repeated route loops;
- minimum memory budget;
- allocation pressure during movement;
- main and shadow views sharing residents.

## 10.6 Phase 3B pass gate

- fixed-LOD visible coverage has no persistent holes after the declared settle window;
- CPU visual mesh generation is disabled during measured GPU runs;
- no per-block draw submission exists;
- all queues remain bounded;
- VRAM remains bounded;
- stale jobs and publications remain zero;
- return-to-origin retained memory stabilizes;
- the complete fixed-LOD GPU backend can be compared with the CPU backend at equivalent coverage and quality.

---

# 11. Phase 4 — 3D Clipbox LOD and Transvoxel Transitions

Begin only after Phases 2B, 3A, and 3B pass.

## 11.1 LOD model

Each block keeps `32³` logical cells.

| LOD | Sample spacing | World coverage per axis |
|---|---:|---:|
| 0 | 1x | 1x |
| 1 | 2x | 2x |
| 2 | 4x | 4x |
| 3 | 8x | 8x |

Each LOD evaluates the canonical procedural field directly at aligned world coordinates.

Never generate LOD0 visual data first and downsample it for distant visual terrain.

## 11.2 Toroidal 3D clipbox

Each level owns a fixed-size 3D toroidal grid.

Required behavior:

- camera movement across a block boundary reassigns newly entered slabs only;
- ordinary movement never rebuilds an entire level;
- coarse levels render as shells around finer levels;
- the primary player camera controls residency;
- auxiliary views reuse residency;
- neighboring rendered blocks differ by at most one supported LOD;
- enter/exit hysteresis suppresses oscillation;
- minimum residency time suppresses rapid churn.

Suggested mapping:

```text
worldBlock -> level-local toroidal slot
slot = positiveModulo(worldBlock - levelOrigin, levelDimensions)
```

Every slot stores its currently assigned world key and generation; slot reuse invalidates stale work.

## 11.3 Transition ownership

Define one owner for every fine/coarse boundary face.

A transition mesh key includes:

```text
fine block key
face direction
fine generation
coarse neighbor generation
```

Rules:

- only the owner emits the transition geometry;
- same-LOD faces emit no transition geometry;
- unsupported LOD differences are prevented by the planner;
- transition geometry uses the project's existing Transvoxel tables and orientation rules.

## 11.4 Coherent publication

For a seam-affecting replacement, publish as one coherent set:

```text
regular block replacement
required transition faces
neighbor generation dependencies
```

Keep the previous coherent set visible until the replacement set is complete.

Do not hide cracks with skirts or overlapping duplicate geometry.

## 11.5 Phase 4 tests

- all 256 regular cases;
- all supported transition cases;
- every face orientation;
- same-LOD borders on X/Y/Z;
- every supported fine/coarse orientation;
- repeated LOD boundary crossing;
- rapid camera oscillation;
- high-speed slab entry;
- teleport;
- caves and mostly-solid regions;
- edited seams after Phase 5 integration.

## 11.6 Phase 4 pass gate

- no cracks, T-junction leaks, duplicate transition faces, invalid indices, or stale seams;
- LOD1+ samples the canonical field directly;
- ordinary movement updates slabs rather than complete levels;
- transition ownership is unique;
- coherent publication prevents partial seam replacement;
- LOD churn and memory remain bounded.

---

# 12. Phase 5 — Sparse Editable Terrain and CPU Collision

## 12.1 Canonical terrain state

```text
procedural base
+ macro-world descriptors
+ baked sparse edit bricks
+ recent ordered edit operations
```

The GPU is a visual cache of this state, not the authority.

## 12.2 Edit operation contract

```csharp
public struct VoxelEditOp
{
    public ulong EditId;
    public uint WorldRevision;
    public VoxelEditShape Shape;
    public VoxelCsgOperation Operation;
    public Vector3 Position;
    public Rotation Rotation;
    public Vector3 Size;
    public float Smoothness;
    public ushort MaterialId;
}
```

Supported initial shapes:

- sphere;
- capsule;
- oriented box.

Supported operations:

- add;
- subtract;
- smooth add;
- smooth subtract;
- material paint.

## 12.3 Edit flow

1. client requests edit;
2. server validates range, permissions, ownership, and cost;
3. server assigns monotonic edit ID/world revision;
4. CPU applies and persists the edit;
5. CPU bins the edit into affected visual/collision blocks;
6. repeated brush samples are coalesced by affected block;
7. GPU receives compact edit data;
8. affected blocks, halos, and transition faces receive newer generations;
9. GPU remeshes affected blocks;
10. CPU collision remeshes the local gameplay region independently.

Do not patch individual mesh vertices. Edits can change topology.

## 12.4 Baked edit bricks

When a region exceeds a configurable edit-count or evaluation-cost threshold:

- bake older edits into a sparse base-resolution SDF/material brick;
- record rule version and world revision;
- remove baked operations from the active journal;
- retain recent operations above the baked result;
- upload only nearby baked bricks and recent operations;
- map world edit-brick coordinate to GPU atlas slot through a flat hash/table.

Untouched terrain requires no dense persistent storage.

## 12.5 Invalidation

An edit invalidates:

- all regular blocks whose owned samples can change;
- neighboring halo consumers;
- all transition faces that consume affected fine/coarse samples;
- local CPU collision chunks.

The invalidation function must be deterministic and shared by runtime and tests.

## 12.6 CPU collision

Keep CPU Transvoxel collision.

Policy:

- high resolution immediately around players and important edits;
- reduced resolution in a wider safety region;
- no collision outside gameplay relevance;
- direct CPU SDF queries for point tests when practical;
- collision never consumes GPU visual geometry.

## 12.7 Phase 5 tests

- varied dig/place operations;
- four-block intersection edit;
- all block-border directions;
- LOD boundary edits;
- continuous 20 Hz digging;
- continuous 20 Hz placement;
- large explosion;
- repeated edits while older generations are in flight;
- edit then eviction/re-entry;
- edit-journal bake threshold;
- rule-version mismatch for baked data;
- local walking/falling/contact after edits.

## 12.8 Phase 5 pass gate

- authoritative edits immediately affect CPU queries;
- every edit eventually converges visually and in local collision;
- stale generations never overwrite newer edits;
- unaffected residents are not remeshed;
- repeated brush samples are coalesced;
- edit evaluation cost remains bounded as the world ages;
- collision never waits for GPU visual completion;
- no GPU visual geometry readback is introduced.

---

# 13. Procedural Terrain Rules and Parity

The flat plane remains a topology fixture only.

Create a versioned `TerrainRuleStressV1` implemented on CPU and GPU with:

- rolling terrain;
- steep slopes;
- overhangs;
- caves;
- mostly empty blocks;
- mostly solid blocks;
- sparse surface blocks;
- highly fragmented surface blocks;
- deterministic material boundaries;
- controlled high-frequency detail.

Parity requirements:

- same integer world sample coordinate;
- same seed;
- same rule version;
- specified arithmetic order and clamping;
- identical solid/air classification outside declared tolerance;
- identical authoritative material classification;
- fixtures updated whenever rule version changes.

Initial scratch representation may remain `float` until the complete path is correct.

Quantized SDF/material packing is a later optimization and requires measured error and bandwidth evidence.

---

# 14. Memory and Scheduling Policies

## 14.1 Initial terrain memory profiles

| Profile | Combined terrain GPU budget |
|---|---:|
| Minimum | 512 MiB |
| Default | 1,024 MiB |
| High | 2,048 MiB |
| Diagnostic stress | 4,096 MiB |

Combined terrain budget includes:

- vertex pool;
- index pool;
- scratch rings;
- request/count/allocation buffers;
- resident descriptors;
- command buffers;
- edit data when implemented.

Clamp configured budget against current reported GPU memory budget where the engine exposes it.

## 14.2 Pressure degradation order

1. stop speculative requests;
2. cancel obsolete low-priority jobs;
3. evict invisible distant residents;
4. reduce distant coverage/refinement after LOD exists;
5. preserve nearby LOD0 and recently edited terrain;
6. never overrun, synchronously wait, corrupt geometry, or activate CPU visual generation.

## 14.3 Frame budgets

Expose configurable budgets for:

- CPU request planning;
- count-result processing;
- allocation processing;
- GPU Pass A requests submitted;
- GPU Pass B requests submitted;
- command-buffer construction;
- collision jobs;
- maximum queue age.

GPU time is asynchronous. A frame budget controls submissions, not synchronous completion.

---

# 15. Diagnostics and Benchmark Contract

## 15.1 CPU metrics

- request planning time;
- request deduplication/priority time;
- Pass A submission time;
- count-result processing time;
- allocator time;
- Pass B submission time;
- publication time;
- command-compaction time;
- collision queue/mesh/publication time;
- managed allocations and GC;
- queue depths, cancellations, and backpressure events.

## 15.2 GPU metrics

- density/material evaluation;
- block reduction;
- regular classification;
- local scans/totals;
- vertex emission;
- index emission;
- transition classification/emission later;
- terrain render by pass;
- total terrain compute;
- request-to-visible latency association.

Use engine GPU profiler scopes/timestamps. Do not call a synchronous readback to measure GPU time.

## 15.3 Work metrics

- requested/counting/allocated/emitting/resident/visible blocks;
- stale results rejected;
- empty/solid/surface blocks;
- active cells;
- vertices/indices/triangles;
- residents by LOD later;
- transition triangles later;
- count readback bytes and latency;
- Pass A/Pass B batch sizes;
- multi-draw calls per view/pass;
- zero-count commands;
- normal geometry readback count, which must remain zero.

## 15.4 Memory metrics

- scratch bytes by ring slot;
- vertex/index pool capacity, used, free, and high-water;
- descriptor/command/edit bytes;
- live/pending/retired allocations;
- fragmentation;
- largest free range;
- allocation failures/retries;
- reported GPU memory budget and use.

## 15.5 Benchmark identity

Every report records:

- actual Git revision automatically;
- dirty working-tree state;
- s&box engine version/revision;
- CPU and GPU;
- resolution, VSync, frame cap, and graphics settings;
- visual backend identity;
- procedural-rule version;
- LOD policy version;
- chunk size and voxel size;
- batch capacity;
- scratch-ring count;
- terrain memory profile;
- edit-data version;
- collision policy;
- material/shadow settings.

Remove dependence on manually maintained scene revision strings.

## 15.6 Scenario states

```text
PASS
FAIL
NOT_APPLICABLE_IN_THIS_PHASE
BLOCKED_BY_LATER_PHASE
```

A fixed-LOD phase is not failed merely because transition edits belong to a later phase. All scenarios still appear in reports.

---

# 16. Benchmark Scenarios by Phase

## Phase 2B

- regular-cell conformance proof;
- static persistent resident set;
- allocator churn;
- replacement failure;
- deliberate exhaustion;
- return-to-origin resident-set stability;
- asynchronous readback saturation;
- resource recreation;
- dedicated server startup.

## Phase 3B

- cold fixed-LOD generation;
- stationary settle;
- line traversal;
- diagonal traversal;
- infinity traversal;
- reversal;
- teleport;
- frustum sweep;
- minimum-budget degradation;
- repeated route loops.

## Phase 4

- LOD slab movement;
- LOD oscillation;
- all transition orientations;
- cave seams;
- fast clipbox movement;
- coherent replacement.

## Phase 5

- varied edits;
- border edits;
- 20 Hz dig/place;
- explosion;
- stale edit generations;
- edit bake threshold;
- visual/collision convergence.

---

# 17. Final CPU/GPU Comparison Protocol

1. Preserve an immutable clean CPU baseline revision.
2. Run the complete CPU suite on that exact revision.
3. Run GPU measurements on the same engine revision.
4. Apply shared correctness fixes identically to both branches.
5. Use separate clean editor sessions.
6. Match voxel size, visible coverage, vertical coverage, procedural rules, materials, shadows, collision policy, and graphics settings.
7. Use at least three complete valid runs per branch.
8. Compare medians, p95, 1% lows, request-to-visible latency, and memory stability.
9. Preserve JSON, JSONL, CSV, Markdown, and HTML reports.
10. Validate on the primary development GPU and at least one representative mid-range GPU.
11. Once GPU LOD exists, compare equivalent visible coverage/quality rather than raw block counts.

---

# 18. Final Adoption Gates

## 18.1 Correctness

- every mandatory scenario passes;
- density/material parity passes;
- regular and transition topology passes;
- normal geometry readbacks are zero;
- synchronous normal metadata readbacks are zero;
- invalid writes are zero;
- stale publications are zero;
- visible cracks and persistent holes are zero;
- edits and local collision converge correctly.

## 18.2 Rendering

- opaque/depth/G-buffer/shadow/fog/post-processing paths are correct;
- renderer uses GPU-owned geometry directly;
- no per-block visual objects remain;
- no per-block draw submissions remain;
- camera-relative rendering is stable.

## 18.3 Memory and stability

- player-profile budget is never exceeded;
- minimum profile degrades gracefully;
- queues remain bounded and recover;
- no resource leak or monotonic retained-memory growth;
- repeated equivalent routes settle within 5% retained terrain memory;
- exhaustion never causes corruption, synchronous waiting, or CPU fallback.

## 18.4 Full GPU adoption performance

Against the equivalent CPU baseline:

- median CPU visual-terrain work, excluding collision, improves by at least 50%;
- cold completion improves by at least 25%;
- request-to-render p95 improves by at least 30% in at least two traversal routes and regresses by no more than 10% in the third;
- no scenario frame p95 regresses by more than 5%;
- no scenario 1% low regresses by more than 5%;
- sustained edit settle p95 regresses by no more than 10%;
- player profile meets its frame/GPU target;
- stress profile remains bounded;
- results reproduce in three runs and on representative hardware.

If full adoption fails, targeted GPU adoption is allowed only for a self-contained path with no normal geometry readback, a repeatable end-to-end benefit of at least 25%, and no duplicate runtime visual system.

GPU density followed by CPU geometry readback is not an acceptable targeted architecture.

---

# 19. Explicit Non-Goals Until Profiling Justifies Them

Do not add these as prerequisites:

- GPU-managed persistent free-list allocator;
- GPU frustum culling;
- hierarchical-Z occlusion;
- mesh shaders;
- meshlets;
- compressed/quantized vertex format;
- compressed SDF scratch;
- per-tile mesh allocations;
- SVO/SVDAG storage;
- ray-marched primary terrain;
- individual vertex patching after edits.

Implement the simplest bounded architecture that removes CPU visual generation and meshing first. Optimize only measured bottlenecks.

---

# 20. Immediate Codex Task Checklist

Implement **Phase 2B only**, in this order:

- [ ] Inspect current code and list code-to-spec gaps.
- [ ] Build and run the existing GPU proof.
- [ ] Preserve the proof as diagnostic-only.
- [ ] Add GPU/C# layout assertions.
- [ ] Implement async readback capability test.
- [ ] Implement 16-command multi-draw capability test.
- [ ] Select and document safe deferred-release mechanism.
- [ ] Create production GPU contracts.
- [ ] Create bounded scratch-ring arena.
- [ ] Implement Pass A density/material generation.
- [ ] Implement block min/max reduction.
- [ ] Implement count-only regular classification/scans/totals.
- [ ] Implement bounded asynchronous count-result ring.
- [ ] Refactor the real vertex/index range allocator.
- [ ] Make lifecycle tests use the production allocator.
- [ ] Implement transactional allocation and replacement.
- [ ] Upload allocation descriptors.
- [ ] Implement capacity-safe Pass B vertex emission.
- [ ] Implement capacity-safe Pass B index emission.
- [ ] Implement resident slots and generation-safe publication.
- [ ] Implement deferred retirement/reclamation.
- [ ] Render a static persistent resident set with one bounded multi-draw.
- [ ] Add Phase 2B diagnostics and report fields.
- [ ] Run correctness, exhaustion, stale-generation, and memory-stability tests.
- [ ] Update the phase status only after every Phase 2B gate passes.

Stop after Phase 2B. Do not begin clipbox LOD or transition cells in the same implementation task.

---

# 21. Definition of Phase 2B Done

Phase 2B is done when a clean run proves:

```text
CPU requests fixed-LOD visual blocks
GPU counts their Transvoxel geometry
CPU asynchronously receives only compact counts
CPU allocates bounded persistent GPU ranges
GPU emits directly into those ranges
one renderer draws the resident set through multi-draw
old residents survive failed replacements
stale generations never publish
retired ranges are reclaimed safely
normal visual geometry readback is zero
CPU visual mesh generation is not used by the static GPU backend
```

Only then proceed to production rendering and movement streaming.
