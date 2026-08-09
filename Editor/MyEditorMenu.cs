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
}
