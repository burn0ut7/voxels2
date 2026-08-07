# Project Guidance

## Project identity

This is an **s&box** project, not Unity.

- Use the s&box `Sandbox` API and component/scene model. Never add `UnityEngine`, `MonoBehaviour`, Unity packages, Unity lifecycle methods, or Unity assumptions.
- For every s&box task—including API, code, scenes, assets, editor tools, input, rendering, physics, debugging, tests, performance, networking, review, and packaging—invoke and follow the `sbox` skill before acting. This is the required Sandbox skill; performance-only work is not exempt.
- Never guess engine APIs. Verify them against the installed s&box assemblies and generated references, then the compiler; use current official documentation for intent.
- Keep runtime code under `Code/` and editor-only tooling under `Editor/`.
- `Code/VoxelManager.cs` is the current voxel-grid entry point.

## Project goal

Build a large-scale, procedurally generated SDF smooth-mesh voxel world with underground terrain, live terrain editing for players, and multiplayer support. Every voxel node has a material type.

The [Zylann Godot Voxel project](https://github.com/Zylann/godot_voxel) is this project's main inspiration and target. Use it as the primary reference for voxel-system capabilities, architecture, and end-user experience while implementing those ideas with native s&box APIs and patterns.

## Single-system rule

Each feature has exactly one authoritative implementation and one ownership boundary. Extend or replace that system directly. Never add fallback, compatibility, alternate, duplicate, or cascading pipelines for the same behavior. Remove superseded implementations before delivery.

## Working rules

- Inspect nearby code, project settings, and `.editorconfig` before editing.
- Keep changes scoped to the requested slice and preserve unrelated work in the working tree.
- Do not hand-edit generated files, `obj/`, compiled scene files (`*_c`, `*_d`), or build output.
- Diagnostics must be opt-in, bounded, and inert when disabled. They may observe authoritative systems but never become alternate gameplay or data pipelines.
- Use the `sbox` skill's evidence workflow for API discovery, compilation, live verification, and delivery.

## End-user acceptance gate

The final shipped feature is judged by the end-user experience. A change passes only when it advances the project goal and the resulting game behavior is correct, usable, coherent, and fit for real play.

- Feature scope, quality, and player experience are product outcomes. Performance, implementation complexity, and engineering metrics are constraints and supporting evidence.
- A metric improvement is not a win when it weakens, removes, delays, or materially changes the intended feature. Treat such tradeoffs explicitly as product decisions.
- Evaluate the integrated player-facing result, including multiplayer behavior where relevant. Compilation, tests, logs, counters, profiler captures, and isolated subsystem results cannot replace that judgment.
