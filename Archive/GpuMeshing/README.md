# Archived GPU Compute Meshing Prototype

Archived on 2026-08-07 after the CPU MC33 experiment became the authoritative visual mesher.

This directory is intentionally outside `Code/` and `Assets/`. Its C# and shader files are reference material only and are not compiled or loaded by s&box.

## Why it was archived

On the radius-9 flat-world fixture (324 chunks), the CPU MC33 pipeline completed in about 1.4 seconds while the GPU compute pipeline took about 7.7–8.1 seconds. The compute implementation's per-chunk scheduling, synchronization, and buffer management costs outweighed its meshing work for this workload.

## What remains active

The authoritative SDF and MC33 topology generation run on the CPU. Generated vertices and indices are uploaded into s&box `Mesh` GPU buffers and rendered by `ModelRenderer`, so rasterization, material shading, lighting, and shadows still run on the GPU. Collision meshes continue to be generated on the CPU from the authoritative SDF.

## Contents

- `Code/VoxelGpuComputeProbe.cs`: compute dispatch, buffers, indirect drawing, statistics, and prototype scheduling support.
- `Shaders/`: compute and GPU-prototype render shaders, including their archived compiled snapshots.
- The old manager integration was removed so the runtime has one authoritative visual-meshing path. Git history preserves that integration for a future redesign.

If this prototype is revisited, benchmark it as a deliberate replacement for the active CPU mesher. Do not ship both visual meshers or introduce a runtime fallback chain.
