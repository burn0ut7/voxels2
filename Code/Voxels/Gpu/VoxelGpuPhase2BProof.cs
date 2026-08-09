internal readonly record struct VoxelGpuPhase2BProofResult(
	bool Passed,
	string Test,
	string Failure,
	VoxelGpuMeshPoolLifecycleResult? Lifecycle = null );

internal static class VoxelGpuPhase2BProof
{
	public static VoxelGpuPhase2BProofResult RunLifecycle( string test, int referenceVertexCount, int referenceIndexCount )
	{
		var lifecycle = VoxelGpuMeshPoolLifecycleProof.Run( referenceVertexCount, referenceIndexCount );
		return new VoxelGpuPhase2BProofResult( lifecycle.Passed, test, lifecycle.Failure, lifecycle );
	}

	public static VoxelGpuPhase2BProofResult RunDedicatedServerPolicy()
	{
		var capabilities = VoxelGpuCapabilities.Detect( true );
		var passed = !capabilities.Available && capabilities.Failure.Contains( "dedicated server", System.StringComparison.OrdinalIgnoreCase );
		return new VoxelGpuPhase2BProofResult( passed, "dedicated_server_startup", passed ? string.Empty : "dedicated-server capability policy did not disable GPU resources" );
	}

	public static VoxelGpuPhase2BProofResult ValidateStatic( string test, VoxelGpuTerrainDiagnostics diagnostics, int expectedBlocks, int minimumReadbacks = 1 )
	{
		var failure = diagnostics.Failure;
		if ( string.IsNullOrEmpty( failure ) && !diagnostics.Available ) failure = "persistent GPU backend unavailable";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.RequestedBlocks != expectedBlocks ) failure = $"requested {diagnostics.RequestedBlocks} blocks, expected {expectedBlocks}";
		if ( string.IsNullOrEmpty( failure ) && !diagnostics.CapacityLimited && diagnostics.ResidentBlocks != expectedBlocks ) failure = $"published {diagnostics.ResidentBlocks} residents, expected {expectedBlocks}";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.CapacityLimited && diagnostics.ResidentBlocks == 0 ) failure = "capacity-limited static set published no residents";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.CapacityLimited && diagnostics.BlockedRequests != expectedBlocks - diagnostics.ResidentBlocks ) failure = $"capacity-limited static set reports {diagnostics.BlockedRequests} blocked requests for {expectedBlocks - diagnostics.ResidentBlocks} missing residents";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.PendingCountBatches != 0 ) failure = $"{diagnostics.PendingCountBatches} count batches remain";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.PendingEmitBatches != 0 ) failure = $"{diagnostics.PendingEmitBatches} emit batches remain";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.CountReadbackCount < minimumReadbacks ) failure = $"completed {diagnostics.CountReadbackCount} async count readbacks, expected at least {minimumReadbacks}";
		if ( string.IsNullOrEmpty( failure ) && !diagnostics.CapacityLimited && diagnostics.RequestToVisible.Count != expectedBlocks ) failure = $"recorded {diagnostics.RequestToVisible.Count} request-to-visible samples, expected {expectedBlocks}";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.CapacityLimited && diagnostics.RequestToVisible.Count != diagnostics.ResidentBlocks ) failure = $"recorded {diagnostics.RequestToVisible.Count} request-to-visible samples for {diagnostics.ResidentBlocks} residents";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.BatchCompletion.Count < minimumReadbacks ) failure = $"recorded {diagnostics.BatchCompletion.Count} completed batches, expected at least {minimumReadbacks}";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.GeometryReadbackBytes != 0 ) failure = $"read back {diagnostics.GeometryReadbackBytes} geometry bytes";
		if ( string.IsNullOrEmpty( failure ) && !diagnostics.MultiDrawIndirect ) failure = "indexed multi-draw capability is unavailable";
		if ( string.IsNullOrEmpty( failure ) && !diagnostics.IndirectBaseVertex ) failure = "indirect BaseVertex capability is unavailable";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.PoolUsedBytes == 0 ) failure = "persistent mesh pool contains no renderable geometry";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.PoolUsedBytes > diagnostics.PoolCapacityBytes ) failure = "mesh pool exceeded capacity";
		if ( string.IsNullOrEmpty( failure ) && !diagnostics.CapacityLimited && diagnostics.AllocationFailures != 0 ) failure = $"mesh pool reported {diagnostics.AllocationFailures} allocation failures";
		return new VoxelGpuPhase2BProofResult( string.IsNullOrEmpty( failure ), test, failure ?? string.Empty );
	}

	public static VoxelGpuPhase2BProofResult ValidateRegularClipbox( string test, VoxelGpuTerrainDiagnostics diagnostics, int expectedActiveBlocks, string expectedPolicy, int expectedStableSlots = 0 )
	{
		var proof = ValidateStatic( test, diagnostics, expectedActiveBlocks );
		var failure = proof.Failure;
		if ( string.IsNullOrEmpty( failure ) && diagnostics.LodPolicy != expectedPolicy ) failure = $"reported LOD policy '{diagnostics.LodPolicy}', expected '{expectedPolicy}'";
		if ( string.IsNullOrEmpty( failure ) && expectedStableSlots > 0 && diagnostics.ClipboxStableSlotCount != expectedStableSlots ) failure = $"reported {diagnostics.ClipboxStableSlotCount} stable clipbox slots, expected {expectedStableSlots}";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.ClipboxActiveSlotCount != expectedActiveBlocks ) failure = $"reported {diagnostics.ClipboxActiveSlotCount} active clipbox slots, expected {expectedActiveBlocks}";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.ClipboxDroppedWork != 0 ) failure = $"dropped {diagnostics.ClipboxDroppedWork} clipbox slot requests";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.ClipboxMaximumPendingRevisionCount > 1 ) failure = $"observed {diagnostics.ClipboxMaximumPendingRevisionCount} pending clipbox revisions";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.ClipboxTransitionDependencyMismatches != 0 ) failure = $"observed {diagnostics.ClipboxTransitionDependencyMismatches} unresolved transition generation dependencies";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.ClipboxTransitionPendingSlots != 0 ) failure = $"observed {diagnostics.ClipboxTransitionPendingSlots} uncommitted transition metadata slots";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.DepthPrepassCommandLists != diagnostics.OpaqueCommandLists ) failure = $"depth command lists {diagnostics.DepthPrepassCommandLists} differ from opaque command lists {diagnostics.OpaqueCommandLists}";
		return new VoxelGpuPhase2BProofResult( string.IsNullOrEmpty( failure ), test, failure ?? string.Empty );
	}
}
