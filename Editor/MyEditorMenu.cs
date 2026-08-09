public static class MyEditorMenu
{
	[Menu("Editor", "playercontrollertemplate/My Menu Option")]
	public static void OpenMyMenu()
	{
		EditorUtility.DisplayDialog("It worked!", "This is being called from your library's editor code!");
	}

	[Menu("Editor", "Voxel Clipbox/Run Planner Proof")]
	public static void RunVoxelClipboxPlannerProof()
	{
		var report = VoxelClipboxDiagnostics.RunPlannerProof();
		var status = report.Passed ? "PASSED" : "FAILED";
		Log.Info( $"Voxel clipbox planner proof {status}: cases={report.Cases}, configurations={report.Configurations}, active={report.ActiveRegularCount}, stableSlots={report.StableRegularSlots}, allocatedAfterWarmup={report.AllocatedBytesAfterWarmup}B, failure={report.Failure}." );
		EditorUtility.DisplayDialog( $"Voxel Clipbox Planner: {status}", report.Passed ? $"{report.Cases} proof cases passed.\nNo post-warm-up planner allocation." : report.Failure );
	}

	[Menu("Editor", "Voxel GPU/Run Indirect Render Proof")]
	public static void RunVoxelGpuIndirectRenderProof()
	{
		var report = VoxelGpuIndirectRenderDiagnostics.RunProof();
		var status = report.Passed ? "PASSED" : "FAILED";
		Log.Info( $"Voxel GPU indirect render proof {status}: counts={report.TestedCommandCounts}, max={report.MaximumCommandCount}, groupSize={report.CommandGroupSize}, boundary={report.BoundaryCommandCount}, boundaryLists={report.BoundaryActiveCommandLists}, failure={report.Failure}." );
		EditorUtility.DisplayDialog( $"Voxel GPU Indirect Render: {status}", report.Passed ? $"Validated {report.TestedCommandCounts} command counts through {report.MaximumCommandCount}.\n49-command boundary uses {report.BoundaryActiveCommandLists} active list(s)." : report.Failure );
	}
}
