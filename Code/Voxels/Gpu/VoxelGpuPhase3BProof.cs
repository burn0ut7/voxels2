internal readonly record struct VoxelGpuPhase3BProofResult( bool Passed, string Test, string Failure );

internal static class VoxelGpuPhase3BProof
{
	public static VoxelGpuPhase3BProofResult ValidateMovement( string test, VoxelGpuTerrainDiagnostics gpu, VoxelTerrainDiagnostics terrain )
	{
		var failure = gpu.Failure;
		if ( string.IsNullOrEmpty( failure ) && !gpu.Available ) failure = "persistent GPU backend unavailable";
		if ( string.IsNullOrEmpty( failure ) && !gpu.QueuesBounded ) failure = "one or more GPU movement queues exceeded its cap";
		if ( string.IsNullOrEmpty( failure ) && !gpu.CapacityLimited && gpu.ResidentBlocks != gpu.DesiredBlocks ) failure = $"published {gpu.ResidentBlocks} residents for {gpu.DesiredBlocks} desired blocks";
		if ( string.IsNullOrEmpty( failure ) && gpu.CapacityLimited && gpu.BlockedRequests > gpu.DesiredBlocks - gpu.ResidentBlocks ) failure = $"capacity-limited state reports {gpu.BlockedRequests} blocked requests for {gpu.DesiredBlocks - gpu.ResidentBlocks} missing residents";
		if ( string.IsNullOrEmpty( failure ) && gpu.PendingRequestCount != 0 ) failure = $"{gpu.PendingRequestCount} GPU requests remained queued";
		if ( string.IsNullOrEmpty( failure ) && gpu.PendingPublicationCount != 0 ) failure = $"{gpu.PendingPublicationCount} GPU publications remained pending";
		if ( string.IsNullOrEmpty( failure ) && !gpu.CapacityLimited && gpu.AllocationFailures != 0 ) failure = $"encountered {gpu.AllocationFailures} GPU allocation failures";
		if ( string.IsNullOrEmpty( failure ) && gpu.VisibleDrawCommands == 0 ) failure = "movement settled without visible GPU draw commands";
		if ( string.IsNullOrEmpty( failure ) && gpu.GeometryReadbackBytes != 0 ) failure = "movement used production geometry readback";
		if ( string.IsNullOrEmpty( failure ) && terrain.ActiveVisualChunks != 0 ) failure = $"CPU visual chunks remained active ({terrain.ActiveVisualChunks})";
		return new VoxelGpuPhase3BProofResult( string.IsNullOrEmpty( failure ), test, failure ?? string.Empty );
	}
}
