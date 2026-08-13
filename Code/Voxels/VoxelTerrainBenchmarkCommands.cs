internal static class VoxelTerrainBenchmarkCommands
{
	[ConCmd( "voxel_benchmark_run" )]
	public static void Run()
	{
		const string benchmarkScene = "scenes/basic_example.scene";
		var activeScene = Game.ActiveScene;
		if ( activeScene is null )
		{
			Log.Error( "Voxel terrain benchmark requires basic_example to be running in play mode." );
			return;
		}

		if ( !activeScene.LoadFromFile( benchmarkScene ) )
		{
			Log.Error( $"Voxel terrain benchmark could not load {benchmarkScene}." );
			return;
		}

		activeScene.GetAllComponents<VoxelTerrainBenchmark>().FirstOrDefault()?.RunBenchmark();
	}
}
