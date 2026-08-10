internal static class VoxelTerrainBenchmarkCommands
{
	[ConCmd( "voxel_benchmark_run" )]
	public static void Run()
	{
		const string benchmarkScene = "scenes/terrain_benchmark.scene";
		if ( !Game.ActiveScene.LoadFromFile( benchmarkScene ) )
		{
			Log.Error( $"Voxel terrain benchmark could not load {benchmarkScene}." );
		}
	}
}
