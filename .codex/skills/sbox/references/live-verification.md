# Live s&box editor tools

The `sbox` MCP server is embedded in the open editor. Its stable entry points expose a hotloaded registry containing the current scene, component, asset, package, compiler, play, camera, trace, and project-specific tools.

## Registry entry points

| Entry point | Use |
| --- | --- |
| `editor_status` | Read engine version, project, active scene, unsaved state, play state, tool count, and relevant paths. |
| `list_toolsets` | List groups of registered tools. |
| `describe_toolset` | Return every tool and full input schema in one group. |
| `search_tools` | Search live names, descriptions, and parameter schemas with space-separated terms. |
| `call_tool` | Invoke one registered tool by name with a JSON argument object. |
| `call_tools` | Invoke an ordered batch. Results retain call order; one failure skips the remaining calls. |
| `read_console` | Read recent editor and game logs, filtered by severity or text. |

Tool schemas can change after a hotload. A fresh `search_tools` or `describe_toolset` call returns the registry currently executing in the editor.

## Compile and hotload

Find `compile_status` through the registry and inspect all compiler records. Its build flags show whether compilation is still active or pending; its success state and diagnostics describe the latest settled build.

Shader gotcha: after changing shader or material source, explicitly compile the shaders through the discovered live editor tool before judging the result. A successful code compile or hotload does not guarantee that shader changes have been compiled and applied; rediscover the registry afterward if the shader compile changes any hotloaded tools or state.

After a source edit, the useful live signals are:

- a settled `compile_status` result;
- fresh compile, hotload, and initialization entries from `read_console`;
- a newly discovered schema for any editor tool affected by the edit;
- a readback whose scene, play state, and behavior match the changed implementation.

Old stack line numbers, duplicate registered names, a tool schema that disagrees with its result, or editor-scene data returned during play identify stale hotload state. Runtime and editor assemblies can be rebuilt through the corresponding discovered tools, followed by registry rediscovery and another readback.

## Play and observation

The live registry supplies the available play controls, scene queries, input or project probes, camera screenshots, traces, and component readbacks. Their exact names and parameters come from `search_tools`.

Tool acceptance means the editor accepted an operation. Observable results come from the matching state readback, screenshot, trace, or console entry. For asynchronous operations, the terminal readback distinguishes accepted work from completed work.

`camera_screenshot` represents game output; `editor_camera_screenshot` represents the authored editor view when those tools are registered. The returned image is the visual result.

Play transitions are observable state changes. If a task starts play, the matching stop operation returns the editor to edit state. Console output after the transition includes runtime exceptions and shutdown errors that may not exist in the earlier sample.

## Player-driven scenario protocol

Use this protocol for any feature whose behavior depends on movement, input, streaming, interaction, networking, or sustained runtime load. It is the required complement to compile checks, direct readbacks, and isolated subsystem tests.

### Scenario setup

Record the engine/build context, scene, starting location, player role, graphics settings, resolution, VSync or frame cap, and any warm-up or reset procedure. Define the player journey and acceptance criteria before measuring. Use a clean or explicitly known world state so edits, cached chunks, and prior sessions do not hide failures.

### Drive the real path

Start play and use the live input or player-control route discovered from the registry. Keep the player moving and interacting at a plausible cadence; do not call a chunk generator, loader, mesh builder, replication method, or diagnostic hook directly to stand in for the player. Direct calls may establish narrow invariants, but they cannot be the only validation for a player-triggered feature.

For chunk, terrain, or streaming features, the minimum representative journey should include:

- a sustained traversal across multiple chunk or region boundaries;
- changes in direction and speed, including a turn or backtrack across recently visited boundaries;
- relevant player interactions such as terrain edits, collision contact, or material use;
- a revisit after the system has had time to stream, build, unload, persist, or replicate; and
- vertical traversal when the feature's workload or visibility depends on elevation.

For multiplayer, run the same journey with the relevant server and client roles. Check what each player sees and can collide with while movement and edits are in progress, then verify the converged state after the journey.

If no live tool can provide the required input, use an existing project-specific runtime probe only when it enters the same authoritative player/controller path. Record exactly what was simulated and mark the result as partial when it does not reproduce the real input path.

### Observe while the player acts

Collect evidence during active traversal and interaction as well as after settling. Use game screenshots or camera state for the visible result; project readbacks for authoritative loaded, mesh, collision, edit, persistence, and replication state; and bounded fresh console output for exceptions, warnings, and streaming failures. Verify that the player remains responsive and that visible terrain, collision, edits, and network state agree throughout the journey.

For performance work, sample the same journey after warm-up and report frame-time distribution or representative percentiles, hitch locations, CPU/GPU timing, allocations, memory, and relevant rendering counters. Keep diagnostics opt-in and bounded. A stationary camera, idle benchmark, or post-run average can supplement the scenario but cannot replace the active player workload when player movement is the source of work.

### Acceptance and cleanup

Separate feature correctness, player experience, and performance in the result. Do not call a scenario successful because a tool accepted an operation, a counter increased, or an average FPS number improved. Stop play, read the terminal console state, and restore any temporary scene or diagnostic state. If a baseline is needed, repeat the same route and settings so the workload is comparable.

## Authored scene data

Scene objects and components are addressed by GUID. Assets are addressed by relative path. Inspection tools return the identifiers needed by mutation tools.

Scene mutations create editor undo entries. Mutation results and a subsequent hierarchy or property readback expose the resulting authored state. `save_scene` persists the active scene when that operation is registered; unsaved state remains visible through `editor_status`. The registry's undo tool applies the editor undo record and supports a restoration readback.

## Evidence by claim

| Claim | Live evidence surface |
| --- | --- |
| Current code compiles | Settled `compile_status` result and diagnostics |
| Hotload is current | Fresh schema, matching behavior readback, and console state |
| Scene data changed | Object/component property or hierarchy readback |
| Runtime state changed | Play-mode state or project-specific readback |
| Visual output changed | Inspected game or editor screenshot |
| Collision or spatial behavior changed | Trace result in the relevant scene and play state |
| Runtime emitted no relevant error | Bounded fresh `read_console` result after the operation |
