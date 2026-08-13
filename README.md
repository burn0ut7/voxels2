# Voxels2

## Benchmark workload policy

The authoritative voxel benchmark runs in `Assets/scenes/basic_example.scene`.
`VoxelTerrainBenchmark.TraversalDistance` and `TraversalSpeed` are fixed workload
inputs. They must remain unchanged between runs; reducing the distance or speed
is not a performance improvement and invalidates the comparison.

Every benchmark report must record the effective traversal distance and speed.
Use the complete seven-scenario suite when evaluating terrain performance.
