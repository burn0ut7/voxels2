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

		// The benchmark is owned by basic_example. When that world is already
		// running, invoking the command must not reload it and inject another
		// startup/pipeline-cache hitch into the measurement.
		var benchmark = activeScene.GetAllComponents<VoxelTerrainBenchmark>().FirstOrDefault();
		if ( benchmark is not null )
		{
			benchmark.RunBenchmark();
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
