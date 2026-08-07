---
name: sbox
description: Use s&box APIs, project resources, installed engine references, official sources, and the live editor tool registry for development, inspection, debugging, player-driven testing, and performance work.
---

# s&box

This skill is an operational map of s&box: where its authoritative information lives, how to discover the installed `Sandbox` API, and how to operate the live editor tools.

## Outcome vocabulary

Use these definitions to evaluate results. They describe the outcome, not the implementation method.

| Dimension | Success | Failure |
| --- | --- | --- |
| Feature | The requested behavior is present, correct, and reflected by the authoritative game state. | Behavior is missing, incorrect, incomplete, or disagrees across state, visuals, collision, persistence, or networking. |
| Player and user experience | The intended task is understandable and responsive at its normal cadence, with coherent feedback and a correct final result. | The task feels delayed, discontinuous, stale, lossy, confusing, or produces an incoherent result. |
| Performance | The representative scenario meets its stated target or shows a measured improvement without degrading feature correctness or experience. | The target is missed, regressions or hitches remain, resource cost becomes unacceptable, or improved metrics conceal worse correctness or experience. |

Compilation, logs, counters, profiler captures, screenshots, and state readbacks are evidence for these outcomes; none defines success by itself.

For runtime, streaming, interaction, networking, or performance work, require a representative player journey as part of validation. Exercise the feature through the same game-facing path an end user uses, observe the integrated result while the player is acting, and treat isolated calls to generators, loaders, or diagnostic hooks as supporting evidence only. A lower frame time or successful tool call does not establish success if the player journey is untested or feels wrong.

## Sandbox information map

| Need | Use |
| --- | --- |
| Current project, engine version, scene, play state, and paths | Live `editor_status` |
| Current editor operations and their schemas | Live `search_tools`, `list_toolsets`, and `describe_toolset` |
| Scene, component, asset, package, play, camera, trace, and compile operations | The hotloaded editor registry through `call_tool` or `call_tools` |
| Fresh editor and game diagnostics | Live `read_console` |
| Exact types and member signatures in the installed build | `scripts/inspect-installed-api.ps1` against `Sandbox.*.dll` |
| Installed API summaries and member IDs | `scripts/search-installed-api.ps1` against `Sandbox.*.xml` |
| Project, installed examples, official docs, and public engine source | `scripts/search-sbox-source.ps1` |
| Machine-readable official API data | `scripts/search-api-schema.ps1` |
| Concepts and supported workflows | Official s&box documentation |
| Examples from public games and libraries | Official s&box Code Search |

For detailed source locations, cache commands, and version handling, read [source-strategy.md](references/source-strategy.md) when researching an API, engine behavior, or example.

## Live editor tools

The connected `sbox` MCP operates on the project currently open in the editor. Its visible entry points are a gateway to a registry that changes when editor or add-on code hotloads.

1. Call `editor_status` to identify the live editor context.
2. Use `search_tools` for a task-oriented lookup, or `list_toolsets` followed by `describe_toolset` to inspect a complete tool group.
3. Invoke a discovered operation with `call_tool`. Use `call_tools` for an ordered batch; a failed call stops the remaining batch.
4. Read each result's `isError` state. Use `read_console` after editor or play operations to inspect emitted errors, exceptions, warnings, and logs.

The live registry uses these conventions:

- Paging parameters are `limit` and `offset`.
- Positions are `"x,y,z"`; angles are `"pitch,yaw,roll"` in degrees.
- Coordinates use Source convention: +x forward, +y left, +z up; one unit is one inch.
- Game objects and components use GUIDs.
- Assets use the relative paths returned by asset search.
- Scene-editing tools create editor undo steps.
- Tool failures are returned as results rather than MCP protocol failures.

Read [live-verification.md](references/live-verification.md) when the task needs compile state, play mode, scene mutation, screenshots, runtime readback, or hotload diagnosis. Discover schemas live instead of relying on remembered parameter names.

## End-user behavior verification

When a change is triggered by player behavior, validate it with a player-driven scenario in play mode before relying on isolated readbacks or synthetic calls.

1. Define the journey, starting state, actors, route, interactions, and observable acceptance criteria. Include the normal cadence and the edge transitions that matter to the feature.
2. Discover the live play, input, camera, screenshot, state-readback, and diagnostic tools. Drive the game through the user-facing controls or the existing player/controller path; do not invoke the authoritative subsystem directly as a substitute for player behavior.
3. For streaming or voxel work, make the player actually move across several region/chunk boundaries, vary direction and speed, turn around or backtrack, and perform relevant edits/interactions before revisiting changed areas. Include vertical movement when terrain or streaming makes it relevant. For multiplayer work, exercise the journey with the relevant client/server roles and observe replication from the player's perspective.
4. Observe the integrated result during and after the journey: visible terrain, collision, edits, persistence, loaded/unloaded state, networking, responsiveness, frame-time spikes, allocations, and bounded fresh console diagnostics. Capture evidence at the moment of activity, not only after the system becomes idle.
5. Judge the feature and the player experience separately from performance. A scenario passes only when the behavior remains correct, coherent, and responsive at the intended cadence and the measured workload meets its target. Compare against a baseline when making a regression or improvement claim.

If the live registry cannot drive the required player action, state that limitation, use only an existing project-specific runtime path that reaches the same authoritative behavior, and do not claim full end-user validation. Read [live-verification.md](references/live-verification.md) for the scenario protocol and evidence requirements.

## Bundled scripts

Read [script-tools.md](references/script-tools.md) before invoking a bundled script. It contains the routing rules, prerequisites, complete parameter contracts, output shapes, empty-result meanings, failure behavior, and examples needed to run every helper without opening its source.

Installed metadata describes the editor's exact declarations. Installed XML adds summaries. Cached official sources and schema add intended usage and broader discovery, with their recorded revisions identifying which release they describe.

## Specialized branches

- For profiling, frame pacing, allocation, GPU timing, or rendering-cost tools, read [performance-debugging.md](references/performance-debugging.md).
- For script maintenance and regression checks, use the maintenance section of [script-tools.md](references/script-tools.md).
