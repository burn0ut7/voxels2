# Project Guidance

## Project identity

This is an **s&box** project, not Unity.

- Use the s&box `Sandbox` API and component/scene model. Never add `UnityEngine`, `MonoBehaviour`, Unity packages, Unity lifecycle methods, or Unity assumptions.
- For every s&box task—including API, code, scenes, assets, editor tools, input, rendering, physics, debugging, tests, performance, networking, review, and packaging—invoke and follow the `sbox` skill before acting. This is the required Sandbox skill; performance-only work is not exempt.
- Never guess engine APIs. Verify them against the installed s&box assemblies and generated references, then the compiler; use current official documentation for intent.
- Keep runtime code under `Code/` and editor-only tooling under `Editor/`.
- `Code/Voxels/VoxelManager.cs` is the current voxel-grid entry point.

## Project goal

Build a large-scale, procedurally generated SDF smooth-mesh voxel world with underground terrain, live terrain editing for players, and multiplayer support. Every voxel node has a material type.

The [Zylann Godot Voxel project](https://github.com/Zylann/godot_voxel) is this project's main inspiration and target. Use it as the primary reference for voxel-system capabilities, architecture, and end-user experience while implementing those ideas with native s&box APIs and patterns.

## Single-system rule

Each feature has exactly one authoritative implementation and one ownership boundary. Extend or replace that system directly. Never add fallback, compatibility, alternate, duplicate, or cascading pipelines for the same behavior. Remove superseded implementations before delivery.

## Working rules

- Inspect nearby code, project settings, and `.editorconfig` before editing.
- Keep changes scoped to the requested slice and preserve unrelated work in the working tree.
- Do not hand-edit generated files, `obj/`, compiled scene files (`*_c`, `*_d`), or build output.
- Starting diagnostics or a benchmark run must be opt-in and inert when disabled. Once a benchmark run starts, its required scenarios, instrumentation, validation, and report persistence are mandatory and cannot be selectively disabled.
- Diagnostics may observe authoritative systems but never become alternate gameplay or data pipelines. Aggregate hot-loop counters once per operation instead of adding profiling calls to every voxel sample.
- Use the `sbox` skill's evidence workflow for API discovery, compilation, live verification, and delivery.

## Voxel performance corpus

- `Assets/scenes/basic_example.scene` and `Code/Voxels/VoxelTerrainBenchmark.cs` define the authoritative voxel performance suite. Maintain one world, one suite, and one reporting pipeline; do not create narrower alternate benchmarks for individual optimizations.
- Running the suite is optional. When it runs, every registered scenario must execute in its declared order, including cold generation, varied edits, bulk editing, sustained world-wide digging, and sustained placement. A missing, duplicated, timed-out, faulted, or skipped scenario makes the run incomplete and the report a failure.
- New terrain behavior that affects generation, meshing, collision, editing, streaming, persistence, memory, file/cache I/O, rendering, or networking must extend the authoritative suite and its required scenario manifest in the same change. Mark unavailable systems explicitly; never synthesize passing data for an implementation that does not exist.
- Keep profiling coverage always-on inside an active suite run: frame pacing and FPS distribution, stutters, latency, CPU/GPU timings, allocations and GC, process and render memory, topology/work totals, call/work-unit counts, collision, and applicable file/cache and network metrics. Only the decision to start diagnostics is optional.
- Instrumentation names and report columns are historical APIs. Prefer additive changes, stable units, per-scenario deltas, bounded buffers, and low-overhead aggregate counters. Distinguish function invocation counts from aggregate work units such as samples copied or tested.
- Every suite run must write the complete revision-tagged corpus to JSON, JSONL, CSV, Markdown, and the HTML dashboard, including failed runs. Preserve older JSONL rows so metrics remain graphable across commits; missing historical fields must remain readable.
- Before accepting performance work, build the project, verify the live s&box compiler and fresh console, run the full suite with the current Git revision, and run `scripts/validate-voxel-benchmark.ps1`. Report the player-facing outcome separately from subsystem metrics.
- Never claim a performance improvement from a partial suite, unmatched workload, stale hotload, single average, or missing corpus output. Compare equivalent scene, hardware, engine, resolution, graphics, warm-up, and scenario settings, and call out configuration differences.

## Completion Git flow

- After every completed task, commit only that task's changes and push the commit.
- Keep commit titles to five words or fewer; never use ten or more.
- Do not report completion until the push succeeds.

## End-user acceptance gate

The final shipped feature is judged by the end-user experience. A change passes only when it advances the project goal and the resulting game behavior is correct, usable, coherent, and fit for real play.

- Feature scope, quality, and player experience are product outcomes. Performance, implementation complexity, and engineering metrics are constraints and supporting evidence.
- A metric improvement is not a win when it weakens, removes, delays, or materially changes the intended feature. Treat such tradeoffs explicitly as product decisions.
- Evaluate the integrated player-facing result, including multiplayer behavior where relevant. Compilation, tests, logs, counters, profiler captures, and isolated subsystem results cannot replace that judgment.
