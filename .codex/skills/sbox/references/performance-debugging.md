# s&box performance surfaces

This reference maps the s&box performance APIs and live-editor tools. Resolve every identifier against the installed editor because diagnostics and console commands are versioned with the engine.

## Live editor access

`editor_status` provides the engine version, project, scene, play state, and engine/log paths. `search_tools` exposes the current compiler, play, console-command, screenshot, scene-query, and project-specific diagnostic operations. `read_console` returns bounded logs from the editor and game.

The frame-time overlay is represented in installed documentation by `Sandbox.DebugOverlay.FrameTimeGraph`. Some engine versions expose the console command `overlay_fps`; the current live console tool and installed build determine command availability.

## Runtime diagnostic APIs

| API | Available information |
| --- | --- |
| `Sandbox.Diagnostics.PerformanceStats` | CPU frame time, GPU frame time and frame number, allocation and collection counts, process memory, exceptions, and period metrics |
| `Sandbox.Diagnostics.PerformanceStats.Timings` | Named timing groups and histories such as Update, Render, Physics, UI, GC pause, and Idle |
| `Sandbox.Diagnostics.Performance.Scope(string)` | Disposable named CPU timing section for project instrumentation |
| `Sandbox.Diagnostics.GpuProfilerStats` | Named GPU timing hierarchy and video-memory budget/usage |
| `Sandbox.Diagnostics.FrameStats.Current` | Draw calls, triangles, rendered and culled objects, material changes, shadows, lights, render-target activity, buffers, and texture streaming |

Inspect the installed declarations with, for example:

```powershell
./scripts/inspect-installed-api.ps1 -Pattern '^Sandbox\.Diagnostics\.PerformanceStats' -Kind Type
./scripts/inspect-installed-api.ps1 -Pattern '^Sandbox\.Diagnostics\.PerformanceStats\.' -Kind Property
./scripts/inspect-installed-api.ps1 -Pattern '^Sandbox\.Diagnostics\.GpuProfilerStats' -Kind Type
./scripts/inspect-installed-api.ps1 -Pattern '^Sandbox\.Diagnostics\.FrameStats' -Kind Type
./scripts/search-installed-api.ps1 -Pattern 'Sandbox.Diagnostics.Performance'
```

`PerformanceStats.FrameTime` is CPU-side frame processing in seconds. `GpuFrametime` is an asynchronous GPU measurement in milliseconds, and `GpuFrameNumber` identifies its associated frame. `Idle`, VSync, and frame caps affect the interpretation of total frame time. CPU and GPU values can overlap and have different reporting latency.

`GpuProfilerStats.Entries` uses slash-separated timing paths. The paths form a parent/child hierarchy, while `GetSmoothedDuration` and `GetMaxDuration` expose stable and spike-oriented views. `FrameStats` supplies rendering volume and state-change context for those timings.

## Measurement context

A comparable performance observation is identified by the engine version, hardware, scene, camera, resolution, graphics settings, VSync or frame cap, warm-up state, and workload. Frame-time distribution or representative percentiles expose hitch behavior that average FPS hides.

For runtime, streaming, terrain, interaction, or networking work, make the workload an active player-driven journey: move through the world, cross the relevant boundaries, change direction, interact, and revisit affected areas at a plausible cadence. Capture diagnostics while that work is happening. An idle scene, stationary camera, direct subsystem call, or post-run average may supplement the result but cannot represent the player workload by itself; follow the full [player-driven scenario protocol](live-verification.md#player-driven-scenario-protocol).

Feature correctness and player/user experience remain separate outcome dimensions. A performance result includes their observed state so a lower timing number is not mistaken for overall success when responsiveness or correctness changed.

External RenderDoc, Nsight, PIX, or ETW captures are additional evidence surfaces. The live s&box MCP does not parse those captures automatically.

## Official API cross-checks

- [PerformanceStats](https://sbox.game/api/Sandbox.Diagnostics.PerformanceStats)
- [PerformanceStats.Timings](https://sbox.game/api/Sandbox.Diagnostics.PerformanceStats.Timings/Video)
- [GpuProfilerStats](https://sbox.game/api/Sandbox.Diagnostics.GpuProfilerStats)
- [FrameStats](https://sbox.game/api/Sandbox.Diagnostics.FrameStats)
- [Performance](https://sbox.game/api/Sandbox.Diagnostics.Performance)

These pages describe the online release. Installed metadata and XML identify the surface in the editor currently open.
