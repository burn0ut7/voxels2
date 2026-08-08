# GPU-Resident Smooth Voxel Terrain Experiment

## Document status

- Status: implementation specification
- Scope: experimental GPU visual-terrain branch
- Decision under test: full GPU-resident visual terrain, targeted GPU use, or no GPU terrain adoption
- CPU baseline: frozen separately and compared by revision-tagged benchmark runs
- Runtime authority on the GPU branch: one GPU visual backend; CPU remains authoritative for world state, gameplay queries, edits, and local collision

## Executive decision

The experiment is technically feasible in the installed s&box build. The engine exposes compute shaders, structured GPU buffers, indirect draw arguments, command lists, and indexed indirect drawing. The archived prototype also proves that the basic API path works.

The experiment should proceed before a complete LOD system is implemented, but it must begin with an LOD-independent request contract. The first producer will request only fixed LOD0 blocks. After density generation, pooled mesh allocation, direct GPU rendering, batching, residency, and bounded scheduling pass, the same contract will gain real clipbox/LOD selection and Transvoxel transitions.

This order answers the central risk directly: first prove that work remains on the GPU and scales better than the CPU pipeline; then add the complexity needed for production terrain. Building the full LOD system first would not prove the GPU hypothesis and would make failures harder to isolate.

The target is not high GPU utilization for its own sake. The target is better player-visible scaling, lower CPU terrain pressure, bounded latency, and bounded VRAM. A busier GPU is acceptable only when those outcomes improve.

## Final changes to the proposed direction

The supplied direction is accepted with these corrections:

1. **Separate branches are the rollback mechanism.** Preserve the CPU visual implementation on the frozen CPU branch. On the GPU experiment branch, compile and run one authoritative visual path. Do not add an automatic CPU visual fallback, dual visual generation, or a cascading backend selector.
2. **Keep CPU Transvoxel for collision only on the GPU branch.** It may also produce frozen reference fixtures outside normal runtime. It must not silently become a second visual backend.
3. **There is no existing clipbox/LOD implementation to connect.** The current manager streams a square, single-LOD set by `ChunkRadius`. A terrain-request contract and LOD planner are new work.
4. **Use Marching Cubes only as a plumbing proof.** Marching Cubes cannot be treated as topology parity with the current CPU Transvoxel regular-cell mesher. It proves density-to-pool-to-indirect-render flow; GPU Transvoxel has a later, separate correctness gate.
5. **Replace the Godot-specific `ArrayMesh` prohibition.** For s&box, the requirement is: normal GPU regeneration must not read generated vertices or indices back to C# and must not rebuild CPU-authored `Mesh`/`Model` objects per block.
6. **Define one procedural specification.** CPU authority and GPU visual evaluation must implement the same versioned SDF/material rules. Cross-implementation fixtures and tolerances prevent the two implementations from drifting.
7. **Make lifetime safety part of the design.** Every request, edit state, pool allocation, draw descriptor, and deferred release carries a generation/version. Stale jobs must never publish or overwrite live geometry.
8. **Bound memory and work explicitly.** Shared pools need a hard budget, allocation-failure behavior, deferred reclamation, fragmentation metrics, and queue backpressure. “Global pool” alone is not a memory policy.
9. **Permit diagnostic readback only.** Small, asynchronous, opt-in validation readbacks are allowed for tests and aggregate counters. Geometry readback remains forbidden during normal play and performance measurement.
10. **LOD is required for the final architecture decision.** It is not required for the first GPU proof, but full adoption cannot pass without regular and transition-cell seam validation under LOD churn.

## Experiment question and hypothesis

### Question

Can a GPU-resident visual pipeline perform procedural density evaluation, surface extraction, persistent mesh storage, culling, indirect command generation, and rendering with better scaling than the complete CPU visual pipeline while preserving terrain behavior and keeping VRAM bounded?

### Hypothesis

At equivalent visible coverage and terrain detail, the GPU branch will:

- remove CPU SDF halo-copy and visual-meshing work from the normal visual path;
- remove per-block CPU visual mesh uploads after generation;
- reduce CPU terrain submission and request-to-render latency under streaming and edit churn;
- trade that CPU work for bounded GPU compute and pool traffic;
- preserve authoritative CPU edits, gameplay queries, multiplayer intent, and near-player collision;
- scale more gracefully as resident and requested block counts increase.

### Possible conclusions

- **Adopt full GPU visual terrain:** the complete pipeline passes feature, stability, memory, and performance gates.
- **Adopt a narrower GPU responsibility:** a self-contained GPU stage provides a repeatable win without geometry readback or a duplicate runtime path. The minimum coherent terrain candidate is GPU density plus GPU surface extraction; GPU density followed by CPU readback is not an acceptable result.
- **Reject or park GPU terrain:** the complete path does not improve player outcomes, cannot remain bounded, or is too fragile for the benefit measured.

## Current baseline and motivation

The current runtime is a CPU Transvoxel regular-cell visual mesher with CPU SDF storage, worker-pool meshing, main-thread `Mesh` upload, and CPU collision built from the same local topology. Streaming is one LOD and uses a square radius around observers.

The latest available stress report is useful diagnostic evidence, but it is **not the frozen comparison baseline** because it identifies revision `dfc6358+working-tree` while the repository has since advanced. Before implementation begins, create a clean CPU baseline commit and run the complete suite again.

Diagnostic snapshot from complete suite v7, run `20260808-050802`, radius 32, 4,096 blocks, 32³ cells, Ryzen 7 9800X3D, RTX 5090, engine 26.07.22:

| Observation | Current result |
|---|---:|
| Suite completeness | 10/10, PASS |
| Cold visual batch completion | 21.79 s |
| Cold request-to-render p95 | 20.93 s |
| Cold aggregate worker meshing | 13.71 s |
| Cold aggregate snapshot wait/copy | 5.74 / 3.76 s |
| Cold aggregate main upload | 0.74 s |
| Infinity traversal batch completion | 53.02 s |
| Infinity traversal request-to-render p95 | 6.68 s |
| Worst scenario frame p95 | 32.24 ms |
| Worst observed frame | 849.21 ms |
| Cold peak process memory | 12.43 GiB |
| Cold authoritative SDF storage | 421.14 MiB |

The suite passes its correctness/completeness contract, but the stress experience does not settle quickly. The numbers indicate that CPU worker meshing is a major cost, while snapshot creation, queueing, publication, and repeated block orchestration are collectively just as important. This is why the experiment must replace the complete visual path rather than move only the triangle loop to compute.

The archived GPU prototype is negative evidence worth preserving. On a 324-block radius-9 fixture it took about 7.7–8.1 seconds versus about 1.4 seconds for the then-current CPU path. Its per-block dispatching, synchronization, buffer management, and readback overwhelmed the useful compute work. The new architecture must prove that those costs are absent or amortized.

## Product and technical scope

### In scope

- versioned visual block requests;
- direct LOD-specific procedural SDF/material evaluation on the GPU;
- initial one-block Marching Cubes proof;
- batched block classification, allocation/scan, and mesh emission;
- shared vertex/index storage with hard VRAM limits;
- block residency, replacement, eviction, and deferred reclamation;
- GPU culling and indexed indirect drawing;
- regular-cell and transition-cell Transvoxel after the resident path passes;
- sparse authoritative edits, compact GPU edit data, dirty propagation, and coalescing;
- CPU collision within a configurable local interest region;
- one- and multi-observer request planning;
- complete benchmark, correctness, and stress coverage.

### Out of scope for the first proof

- multiplayer replication implementation;
- persistence/file format implementation;
- GPU collision or GPU-to-CPU geometry readback;
- a persistent dense full-resolution SDF for untouched terrain;
- production Transvoxel before Marching Cubes proves the pipeline;
- final terrain art, biome, or material complexity;
- supporting both CPU and GPU visual backends in one runtime build.

Multiplayer and persistence must still influence data contracts: seed/rule versions, edit ordering, edit IDs, and world revisions cannot be local-only assumptions.

## Authority and ownership

### CPU authority

The CPU owns:

- world seed and procedural-rule version;
- sparse persistent edit state and deterministic edit order;
- multiplayer/world authority;
- gameplay terrain queries;
- visual block request and LOD policy;
- request priority and per-frame submission budget;
- local CPU collision;
- compact GPU request/edit submission;
- diagnostics orchestration and report persistence.

### GPU visual system

The GPU owns:

- visual density/material evaluation at the requested spacing;
- regular and transition-cell classification;
- prefix sums/offset allocation or an equivalent bounded allocation pass;
- visual vertex/index generation;
- global mesh-pool storage;
- resident block descriptors and bounds;
- visibility culling;
- indirect draw argument generation;
- terrain rendering.

### Single-system invariant

On the experiment branch, the GPU system is the only visual terrain implementation used during play and benchmark runs. The CPU Transvoxel mesher remains legitimate for local collision and fixture generation because those are separate responsibilities. It may not publish visual geometry, rescue failed GPU blocks, or participate in measured GPU runs.

## Target data flow

```text
CPU world authority
  seed + procedural-rule version + sparse edits
                    |
CPU request planner | block key + LOD + generation + priority
                    v
================ GPU boundary ================
GPU request/edit buffers
                    |
LOD-specific density/material scratch
                    |
regular-cell and transition-cell classification
                    |
bounded count/scan/allocation
                    |
mesh emission into shared vertex/index pools
                    |
resident block descriptor table
                    |
GPU culling + indirect argument generation
                    |
indexed indirect terrain render
```

Collision is intentionally parallel to the visual path:

```text
CPU world authority -> local collision interest -> CPU Transvoxel -> collider
```

Visual completion never waits for collision outside the local safety contract, and collision never consumes GPU visual mesh readback.

## Required contracts

### Block key

A visual block is identified by:

- integer block coordinate;
- LOD level;
- procedural-rule version;
- world/edit revision or generation;
- optional observer partition only if multi-observer ownership requires it.

The coordinate and LOD define an unambiguous world-space origin and sample spacing. Neighboring blocks must derive shared boundary samples from identical world coordinates, never from accumulated local floating-point steps.

### Visual request

Each request contains or references:

- block key;
- world-space integer sample origin;
- cell count, initially 32³;
- sample spacing derived directly from LOD;
- seed/rule constants;
- edit-data range or edit revision;
- priority class and distance key;
- request generation;
- cancellation/staleness token.

No request carries a full CPU-generated dense SDF for procedural terrain.

### Resident descriptor

Each resident block descriptor records:

- block key and published generation;
- vertex offset/count;
- index offset/count;
- bounds;
- material range or material classification data;
- allocation handle generation;
- residency/visibility flags;
- last-used frame or eviction score;
- transition-neighbor state where applicable.

### Lifecycle

The observable state machine is:

```text
Missing -> Requested -> Density -> Classified -> Allocated -> Emitted
        -> Resident -> Visible
        -> Dirty/Replaced -> Retired -> Reclaimed
```

Cancellation can occur before publication. A result whose generation no longer matches the requested generation is retired without becoming visible. Releases are deferred until the GPU can no longer reference the allocation.

## Procedural SDF and material parity

The current procedural fixture is a flat signed-distance field. Future caves/noise must be introduced as a versioned procedural module with a CPU reference implementation and a GPU implementation.

Parity requirements:

- both implementations consume the same integer world sample coordinate, seed, and rule version;
- sign and material classification are authoritative outputs;
- arithmetic order and clamping are specified rather than incidental;
- SDF comparison tolerance is
  `max(2 * SdfClampDistance / (32767 * 2), 0.0001 voxel)` unless a future rule version declares and justifies a different tolerance;
- samples farther than the tolerance from zero must have identical solid/air classification;
- every rule-version change updates conformance fixtures before performance comparisons;
- performance runs disable diagnostic sample readback.

GPU output is a visual cache. Persistent edits and gameplay truth remain on the CPU.

## LOD and seam model

Each requested block keeps a constant logical cell count while changing world-space sample spacing:

| LOD | Cells | Sample spacing | Relative world coverage |
|---|---:|---:|---:|
| 0 | 32³ | 1x | 1x |
| 1 | 32³ | 2x | 2x per axis |
| 2 | 32³ | 4x | 4x per axis |
| 3 | 32³ | 8x | 8x per axis |

Lower-detail blocks evaluate the procedural field directly at their spacing. They are never generated from LOD0 data.

Seam requirements:

- same-LOD neighbors share exact world-coordinate boundary samples;
- regular cells consume a defined one-sample halo/ownership rule;
- LOD differences are restricted to supported Transvoxel neighbor relationships;
- transition ownership is unique so two blocks cannot emit competing transition geometry;
- an edit touching a border invalidates all regular and transition blocks that consume affected samples;
- replacement is coherent: all blocks in a seam-affecting publication set become visible together or the old set remains visible;
- no crack, T-junction leak, duplicate face, invalid index, degenerate triangle, or non-manifold transition is accepted.

## GPU memory model

### Persistent storage

Use shared vertex, index, resident-descriptor, and indirect-command pools. Do not allocate worst-case vertex/index capacity per block.

### Scratch storage

Density, material, classification, count, and scan buffers are reusable scratch arenas sized for a bounded batch. Scratch capacity is independent of total resident-block count.

### Allocation policy

The implementation must define one allocator and one owner. A page allocator, buddy allocator, or size-class free-list is acceptable after measurement; a silent mixture is not.

The allocator must provide:

- hard vertex/index byte budgets;
- allocation handles with generations;
- bounds checks on every produced range;
- deferred release after draw/compute use;
- fragmentation and largest-free-range metrics;
- explicit behavior when allocation fails;
- replacement without exposing partially written geometry;
- an opt-in compaction path only if fragmentation data proves it necessary.

### Budget profiles

- Adoption profile: `TerrainVramBudgetMiB = 2048` for mesh pools, scratch, descriptors, and edit data combined.
- High-end stress profile: `TerrainVramBudgetMiB = 4096`.
- The system may expose smaller profiles later, but passing only on the RTX 5090's available VRAM is not sufficient.

When the budget cannot satisfy a request, the scheduler must apply backpressure and evict lower-priority residents. It must never overrun a buffer, grow without a cap, block the frame waiting for GPU geometry, or switch to CPU visual generation.

## Scheduling

Priority, highest to lowest:

1. dirty blocks caused by player edits/explosions;
2. missing LOD0 blocks in the local safety region;
3. missing LOD0 blocks in movement direction;
4. nearby transition blocks required to close visible seams;
5. remaining LOD1;
6. LOD2 and above;
7. distant refinement and speculative cache fill.

Scheduling requirements:

- a configurable CPU submission budget;
- a configurable GPU terrain-compute budget measured asynchronously;
- bounded request, edit, and retirement queues;
- batch dispatches rather than per-block command-list churn;
- deduplication by block key and generation;
- coalescing of repeated dirty notifications;
- age promotion so low-priority work cannot starve forever;
- cancellation of obsolete movement/LOD requests;
- no synchronous waits for ordinary publication.

The compute budget is a pacing control, not a claim that GPU work completes synchronously in the submission frame.

## Editing and collision

The conceptual terrain state is:

```text
procedural base SDF + ordered sparse persistent edits = authoritative terrain
```

Each edit has a stable ID, world revision, operation/type, bounds, parameters, material effect, and deterministic ordering key. The CPU applies and persists it first. The GPU receives a compact command or updated edit range plus affected-block generations.

Editing requirements:

- repeated brush samples in one frame are coalesced by affected block;
- boundary edits invalidate neighboring halos and transitions;
- obsolete in-flight generations cannot publish over a newer edit;
- edits receive the highest visual priority;
- GPU edit storage is compact and budgeted;
- normal edits do not rebuild unaffected residents;
- visual and local collision convergence are measured separately;
- authoritative gameplay queries observe CPU state immediately even if visuals are still converging.

Collision requirements:

- CPU Transvoxel remains the sole collision mesher;
- collision uses authoritative CPU state, never GPU mesh readback;
- the local interest radius and build budget are configurable;
- local holes/placements become physically correct within the acceptance latency;
- visual progress outside the collision region is not blocked by collision generation.

## Instrumentation contract

Instrumentation is always on during an active authoritative benchmark run and inert otherwise. Aggregate counters update once per operation or batch, not per sample.

### CPU metrics

- request-planner/clipbox update time;
- request deduplication and priority time;
- GPU submission time and submitted batches;
- edit-authority time and affected-block calculation;
- collision queue, mesh, publication, and convergence time;
- managed allocations/GC and process memory;
- queue depths, cancellations, stale results, and backpressure events.

### GPU metrics

- density/material evaluation;
- regular-cell classification;
- transition-cell classification;
- count/scan/allocation;
- regular mesh emission;
- transition mesh emission;
- culling;
- indirect argument generation;
- terrain render;
- total terrain compute and end-to-end request-to-visible latency;
- GPU frame association/latency for asynchronous measurements.

### Work and memory metrics

- requested/generated/remeshed/retired/evicted blocks;
- resident and visible blocks by LOD;
- vertices, indices, triangles, and transition triangles;
- empty/solid/surface blocks;
- mesh-pool used/free/high-water bytes;
- scratch/edit/descriptor/indirect bytes;
- internal and external fragmentation;
- largest free range;
- allocation failures and retries;
- stale generations rejected;
- normal-operation geometry readback count, which must remain zero.

## Branch and comparison protocol

1. Commit or otherwise resolve unrelated working-tree changes before branch creation.
2. Create a clean CPU baseline commit from `main` and tag or record it immutably.
3. Run and validate the complete authoritative suite on that exact CPU commit.
4. Create the GPU experiment branch from that commit.
5. Record engine, CPU, GPU, display, VSync/frame cap, graphics settings, seed, scene, route, warm-up, and benchmark configuration.
6. If a correctness fix is required in both branches, commit it once and cherry-pick the exact change to the other branch; then regenerate both baselines. Do not compare diverged behavior.
7. Run CPU and GPU measurements in separate editor sessions to avoid stale hotload and retained-resource contamination.
8. Use at least three complete valid runs per branch for the final decision. Compare medians, p95 distributions, and worst-case evidence; do not claim a win from one run.
9. Preserve all JSON, JSONL, CSV, Markdown, and HTML output with revision and backend identifiers.

The benchmark configuration identity must add at least: visual architecture, procedural-rule version, LOD policy version, pool budget, scratch batch capacity, edit-data version, and terrain compute budget.

## Implementation phases and gates

Every phase is a stop/go gate. A failed gate is fixed or documented before later phases begin; later features must not conceal an earlier architectural failure.

### Phase 0 — Freeze baseline and define contracts

Deliverables:

- clean CPU baseline revision and complete validated report;
- current CPU pipeline audit;
- versioned block request, resident descriptor, edit, and diagnostics contracts;
- fixed LOD0 request producer replacing direct visual-backend assumptions;
- benchmark schema additions for backend identity and unavailable GPU stages;
- API ledger for installed s&box compute, buffers, barriers, command lists, indirect draw, and profiling surfaces.

Pass:

- CPU behavior and complete suite remain unchanged except additive report fields;
- all ten required scenarios execute in order and validation passes;
- fixed LOD0 requests reproduce the same desired world coverage;
- runtime still has exactly one CPU visual path at this phase;
- baseline revision is clean and reproducible.

Fail:

- an incomplete/stale report is used as baseline;
- the request abstraction changes player-visible coverage or edit behavior;
- a second visual path is introduced;
- backend-specific assumptions leak into world authority contracts.

### Phase 1 — Single-block GPU-resident proof

Deliverables:

- procedural GPU density/material generation for one 32³ LOD0 block;
- GPU Marching Cubes classification and emission;
- direct indexed drawing from generated GPU buffers;
- opt-in bounded diagnostic readback for conformance only;
- CPU/GPU phase timing and byte counters.

Tests:

- empty, solid, flat plane, single corner, sphere, border-crossing surface, and deterministic edited fixture;
- density sign/material conformance against CPU samples;
- invalid index, NaN/Inf vertex, degenerate triangle, winding, and bounds validation;
- repeated rebuild and resource-retirement loop.

Pass:

- the block renders correctly from GPU-generated buffers;
- normal-operation geometry readback count is zero;
- all non-boundary density signs/materials match and SDF deltas are within tolerance;
- no invalid indices, non-finite vertices, allocator overruns, or leaked resources occur;
- repeated generations publish only the newest generation.

Fail:

- CPU-created visual geometry is required to render the result;
- readback is required for draw counts or publication;
- one block requires permanent worst-case buffers that would be multiplied by resident count;
- the implementation cannot identify GPU stage timing or memory use.

### Phase 2 — Batched shared-pool generation

Deliverables:

- bounded scratch arena;
- shared mesh allocator and resident table;
- batched density, classification, scan, allocation, and emission;
- generation-safe replacement and deferred reclamation;
- backpressure and allocation-failure policy.

Tests:

- batches of 1, 8, 32, 128, and scheduler-selected capacities;
- mixed empty/solid/surface topology;
- random allocate/replace/evict churn;
- deliberate exhaustion at the configured VRAM budget;
- cancellation and stale-generation storms;
- return-to-origin and repeated-route memory stabilization.

Pass:

- no per-block worst-case geometry allocation exists;
- no overlap, out-of-range write, stale publication, or use-after-retire is detected;
- allocation failure causes bounded backpressure/eviction without crash or CPU fallback;
- used bytes never exceed the configured budget;
- after two identical out-and-back loops and settling, retained terrain bytes are within 5% of the first settled loop;
- batching reduces submission/dispatch count relative to block count and is reported explicitly.

Fail:

- memory grows with cumulative requests rather than resident demand;
- retirement needs a synchronous GPU wait in normal operation;
- geometry buffers are allocated/recreated per block;
- fragmentation makes valid steady-state workloads fail while nominal free space remains, without a measured mitigation plan.

### Phase 3 — Streaming, residency, culling, and indirect draw

Deliverables:

- current observer-driven fixed-LOD requests feeding the GPU path;
- block cache, eviction score, and residency limits;
- GPU frustum culling;
- GPU indirect command generation and indexed indirect terrain rendering;
- CPU visual path removed from measured GPU runtime;
- authoritative benchmark scenarios extended with GPU metrics.

Tests:

- stationary settle;
- infinity, line, and diagonal traversal;
- high-speed direction reversal;
- teleport/outlier movement;
- camera frustum sweep and occlusion-unaware frustum correctness;
- radius-32/4,096-block stress profile;
- one complete authoritative suite, including edits and collision even if GPU edit support is still marked unavailable and therefore causes an explicit phase failure.

Pass:

- visible coverage has no persistent holes after the configured settle window;
- CPU never loops over visible blocks to issue one draw per block;
- culled blocks do not contribute indirect draw work;
- request queues and VRAM remain bounded during all routes;
- zero normal geometry readbacks, stale publications, and pool errors;
- the full report persists in every required format, including failed runs.

Fail:

- culling requires per-frame GPU geometry readback;
- the system dispatches or submits one independent full pipeline per block at scale;
- motion causes unbounded queue growth or never-settling holes;
- the benchmark omits required scenarios because a stage is incomplete.

### Phase 4 — GPU Transvoxel and LOD

Deliverables:

- GPU regular-cell Transvoxel using the official tables;
- LOD planner/clipbox request producer;
- direct LOD-specific sampling;
- GPU transition-cell generation and unique transition ownership;
- coherent regular/transition replacement;
- LOD residency and churn metrics.

Tests:

- exhaustive regular-cell case coverage;
- exhaustive supported transition-cell case coverage;
- CPU fixture comparison for counts, indices/topology class, positions within tolerance, normals, and materials;
- same-LOD border fixtures in all axes;
- every supported fine/coarse neighbor orientation;
- camera motion across each LOD boundary;
- edited seams and repeated LOD oscillation;
- caves and mostly-solid regions once those procedural rules exist.

Pass:

- zero crack, invalid topology, ownership, or stale-transition failures;
- LOD1+ density is evaluated directly at its requested spacing;
- mesh-job cell count remains constant across LODs;
- regular and transition publication is coherent;
- CPU/GPU reference differences stay within declared tolerance;
- LOD churn remains bounded under repeated boundary crossing.

Fail:

- skirts or overlapping duplicate geometry hide transition defects;
- LOD0 is generated and downsampled for distant blocks;
- transitions depend on CPU-generated visual meshes;
- visual correctness depends on unsupported neighbor LOD differences.

### Phase 5 — Sparse edits and CPU collision integration

Deliverables:

- compact, versioned GPU edit representation;
- affected regular/transition block invalidation;
- same-frame and in-flight edit coalescing;
- high-priority regeneration;
- local CPU collision using authoritative state;
- visual/collision convergence metrics.

Tests:

- varied dig/place operations;
- four-block and LOD-boundary edits;
- continuous 20 Hz digging and placement;
- large explosion;
- repeated edits to one block while older work is in flight;
- edit followed by immediate eviction/re-entry;
- local collision walk/fall/contact after edits;
- multi-observer affected-region calculation.

Pass:

- every authoritative edit eventually appears visually and in local collision;
- no obsolete generation overwrites a newer edit;
- unaffected blocks are not remeshed;
- same-frame repeated edits are coalesced by block;
- visual seam coherence violations remain zero;
- gameplay queries reflect CPU authority immediately;
- collision is generated without GPU mesh readback.

Fail:

- one brush sample causes one independent remesh;
- visual and collision use different edit ordering;
- distant or unaffected residents are repeatedly dirtied;
- local collision waits on visual GPU completion.

### Phase 6 — Final decision campaign

Required workloads:

- all current authoritative scenarios in their declared order;
- stationary viewing and cold generation;
- walking, fast traversal, extreme traversal, reversals, and backtracking;
- continuous digging and placement;
- large explosions;
- dense caves and mostly-solid terrain when implemented;
- LOD-boundary churn;
- repeated memory-stability loops;
- multiple separated observers when multiplayer observation exists.

Every run must capture player-facing behavior, CPU and GPU distributions, request-to-visible latency, edits/collision convergence, work totals, allocations, process/render memory, and all GPU pool metrics.

## Final pass/fail decision

Use three complete, valid, equivalent runs per branch. Compare the median of each run-level metric unless the metric is explicitly a maximum. Any missing scenario, stale revision, changed workload, validation failure, compiler error, fresh console error, or missing report format invalidates the run.

Lock two decision profiles before the final campaign:

- **Player profile:** intended production coverage, LOD policy, terrain quality, and graphics settings. Its default target is 60 Hz, so frame and total GPU p95 must be at or below 16.67 ms unless the project records a different product target before gathering baselines.
- **Stress profile:** equivalent to the current radius-32/4,096-block pressure test or an explicitly matched LOD coverage/quality replacement. This profile judges relative scaling and bounded behavior; it is not required to meet the player profile's absolute frame target.

Do not change either profile between CPU and GPU decision runs.

### Correctness gate — mandatory

Pass only if:

- every authoritative scenario passes;
- density/material conformance passes;
- regular and transition topology/seam tests pass;
- normal geometry readbacks, invalid writes, stale publications, and coherence violations are all zero;
- edits and local collision converge correctly;
- the integrated player journey has no persistent holes, visible cracks, corrupt geometry, or incorrect physical terrain.

Any failure rejects full adoption regardless of performance.

### Memory/stability gate — mandatory

Pass only if:

- the 2,048 MiB adoption terrain-VRAM budget is never exceeded;
- all queues have configured caps and recover after workload pressure;
- no resource leak or monotonic retained-memory growth appears across repeated routes;
- post-loop settled terrain VRAM remains within 5% of the first equivalent settled loop;
- allocation exhaustion degrades by delayed/dropped low-priority refinement, not crash, corruption, synchronous wait, or CPU fallback.

### Full GPU adoption performance gate

Against the clean CPU baseline at equivalent coverage and quality, pass only if all are true:

- median combined CPU visual-terrain work, excluding collision, is reduced by at least 50%;
- cold-generation batch completion improves by at least 25%;
- request-to-render p95 improves by at least 30% in at least two of the three traversal routes and regresses by no more than 10% in the third;
- no scenario's frame p95 regresses by more than 5%;
- no scenario's 1% low FPS regresses by more than 5%;
- sustained dig/place post-edit settle p95 regresses by no more than 10%;
- the player profile meets its declared frame and total-GPU p95 target, while the stress profile obeys the configured terrain-compute budget without queue divergence;
- the improvement reproduces in all three decision runs without a major unexplained outlier.

Frame maximum is reported and investigated but is not a sole adoption threshold because it is noisy. A reproducible severe hitch still fails the player-experience gate.

### Targeted GPU adoption gate

If the complete visual pipeline misses the full-adoption gate, a narrower GPU use may be proposed only when:

- it is a self-contained responsibility with no normal geometry readback;
- it improves its end-to-end player-relevant metric by at least 25% in three valid runs;
- it passes all applicable correctness and memory gates;
- it does not create dual runtime implementations or a fallback chain;
- the complete authoritative suite confirms no material regression elsewhere.

GPU culling/indirect rendering is independently eligible. GPU density alone followed by CPU readback is not.

### Reject/park gate

Reject or park the terrain experiment when any is true after reasonable profiling-led correction:

- the correctness or memory gate cannot pass;
- the complete path misses both full and targeted performance gates;
- unavoidable synchronization/readback dominates the path;
- CPU submission and resource management remain proportional to resident block count;
- GPU terrain work harms frame pacing more than the CPU pressure it removes;
- required complexity is disproportionate to the measured player benefit.

## Definition of experiment complete

The experiment is complete only when:

- phases 0–6 have explicit PASS/FAIL records;
- the GPU branch has one authoritative visual path;
- all required architecture, edit, LOD, memory, and lifetime contracts are implemented or explicitly failed;
- the project compiles in the settled live s&box editor with a fresh clean console;
- the full player-driven benchmark suite runs with the exact current revision;
- `scripts/validate-voxel-benchmark.ps1` passes;
- JSON, JSONL, CSV, Markdown, and HTML reports are preserved for every decision run, including failures;
- CPU and GPU results are comparable and reproduced;
- the decision is recorded as full adoption, targeted adoption, or rejection with evidence;
- the resulting player experience is evaluated separately from subsystem timings.

“A block rendered from compute” is Phase 1 completion, not experiment completion.

## Immediate implementation target

Start with Phase 0, then Phase 1. The first code milestone should be:

> One fixed-LOD0 32³ block whose procedural density is generated on the GPU, whose Marching Cubes surface is emitted into GPU buffers, and whose indexed geometry is drawn directly without normal vertex/index readback.

Before expanding to many blocks, prove the request generation, resource lifetime, direct draw, diagnostics, and conformance contracts. Before implementing full LOD, prove batched shared-pool generation, residency, eviction, and indirect drawing under the current fixed-LOD radius workload.

That sequence gives the experiment the highest information value: it tests whether the GPU architecture fixes the actual scaling problem before investing in production Transvoxel and LOD complexity.
