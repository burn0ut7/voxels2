public readonly record struct VoxelCallCount( string Name, long Count );

public readonly record struct VoxelCallCountSnapshot(
	long ManagerUpdates,
	long WorldGenerationRequests,
	long WorldGenerationPolls,
	long WorldChunksGenerated,
	long BrushRequests,
	long BrushChunkTests,
	long BrushSamplesTested,
	long BrushSamplesChanged,
	long BrushRaycasts,
	long BrushRaycastSamples,
	long BrushRaycastEditCandidates,
	long BrushRaycastEditTests,
	long VisualWorldStarts,
	long VisualQueuePumps,
	long VisualBuildsQueued,
	long VisualBuildsStarted,
	long SdfHaloSnapshots,
	long SdfHaloSamplesCopied,
	long VisualBuildsCompleted,
	long VisualUploads,
	long CollisionInterestRefreshes,
	long CollisionQueuePumps,
	long CollisionBuildsQueued,
	long CollisionBuildsStarted,
	long CollisionSnapshotSamplesCopied,
	long CollisionBuildsCompleted,
	long CollisionUploads,
	long PlayerSafetyActivations,
	long PlayerSafetyUpdates,
	long PlayersRepositioned,
	long PlayerTraversalUpdates
)
{
	public VoxelCallCountSnapshot Subtract( VoxelCallCountSnapshot baseline )
	{
		return new VoxelCallCountSnapshot(
			System.Math.Max( 0, ManagerUpdates - baseline.ManagerUpdates ),
			System.Math.Max( 0, WorldGenerationRequests - baseline.WorldGenerationRequests ),
			System.Math.Max( 0, WorldGenerationPolls - baseline.WorldGenerationPolls ),
			System.Math.Max( 0, WorldChunksGenerated - baseline.WorldChunksGenerated ),
			System.Math.Max( 0, BrushRequests - baseline.BrushRequests ),
			System.Math.Max( 0, BrushChunkTests - baseline.BrushChunkTests ),
			System.Math.Max( 0, BrushSamplesTested - baseline.BrushSamplesTested ),
			System.Math.Max( 0, BrushSamplesChanged - baseline.BrushSamplesChanged ),
			System.Math.Max( 0, BrushRaycasts - baseline.BrushRaycasts ),
			System.Math.Max( 0, BrushRaycastSamples - baseline.BrushRaycastSamples ),
			System.Math.Max( 0, BrushRaycastEditCandidates - baseline.BrushRaycastEditCandidates ),
			System.Math.Max( 0, BrushRaycastEditTests - baseline.BrushRaycastEditTests ),
			System.Math.Max( 0, VisualWorldStarts - baseline.VisualWorldStarts ),
			System.Math.Max( 0, VisualQueuePumps - baseline.VisualQueuePumps ),
			System.Math.Max( 0, VisualBuildsQueued - baseline.VisualBuildsQueued ),
			System.Math.Max( 0, VisualBuildsStarted - baseline.VisualBuildsStarted ),
			System.Math.Max( 0, SdfHaloSnapshots - baseline.SdfHaloSnapshots ),
			System.Math.Max( 0, SdfHaloSamplesCopied - baseline.SdfHaloSamplesCopied ),
			System.Math.Max( 0, VisualBuildsCompleted - baseline.VisualBuildsCompleted ),
			System.Math.Max( 0, VisualUploads - baseline.VisualUploads ),
			System.Math.Max( 0, CollisionInterestRefreshes - baseline.CollisionInterestRefreshes ),
			System.Math.Max( 0, CollisionQueuePumps - baseline.CollisionQueuePumps ),
			System.Math.Max( 0, CollisionBuildsQueued - baseline.CollisionBuildsQueued ),
			System.Math.Max( 0, CollisionBuildsStarted - baseline.CollisionBuildsStarted ),
			System.Math.Max( 0, CollisionSnapshotSamplesCopied - baseline.CollisionSnapshotSamplesCopied ),
			System.Math.Max( 0, CollisionBuildsCompleted - baseline.CollisionBuildsCompleted ),
			System.Math.Max( 0, CollisionUploads - baseline.CollisionUploads ),
			System.Math.Max( 0, PlayerSafetyActivations - baseline.PlayerSafetyActivations ),
			System.Math.Max( 0, PlayerSafetyUpdates - baseline.PlayerSafetyUpdates ),
			System.Math.Max( 0, PlayersRepositioned - baseline.PlayersRepositioned ),
			System.Math.Max( 0, PlayerTraversalUpdates - baseline.PlayerTraversalUpdates )
		);
	}

	public IEnumerable<VoxelCallCount> Enumerate()
	{
		yield return new( "manager.updates", ManagerUpdates );
		yield return new( "generation.requests", WorldGenerationRequests );
		yield return new( "generation.polls", WorldGenerationPolls );
		yield return new( "generation.chunks", WorldChunksGenerated );
		yield return new( "brush.requests", BrushRequests );
		yield return new( "brush.chunk_tests", BrushChunkTests );
		yield return new( "brush.samples_tested", BrushSamplesTested );
		yield return new( "brush.samples_changed", BrushSamplesChanged );
		yield return new( "brush.raycasts", BrushRaycasts );
		yield return new( "brush.raycast_samples", BrushRaycastSamples );
		yield return new( "brush.raycast_edit_candidates", BrushRaycastEditCandidates );
		yield return new( "brush.raycast_edit_tests", BrushRaycastEditTests );
		yield return new( "visual.world_starts", VisualWorldStarts );
		yield return new( "visual.queue_pumps", VisualQueuePumps );
		yield return new( "visual.builds_queued", VisualBuildsQueued );
		yield return new( "visual.builds_started", VisualBuildsStarted );
		yield return new( "visual.halo_snapshots", SdfHaloSnapshots );
		yield return new( "visual.halo_samples_copied", SdfHaloSamplesCopied );
		yield return new( "visual.builds_completed", VisualBuildsCompleted );
		yield return new( "visual.uploads", VisualUploads );
		yield return new( "collision.interest_refreshes", CollisionInterestRefreshes );
		yield return new( "collision.queue_pumps", CollisionQueuePumps );
		yield return new( "collision.builds_queued", CollisionBuildsQueued );
		yield return new( "collision.builds_started", CollisionBuildsStarted );
		yield return new( "collision.snapshot_samples_copied", CollisionSnapshotSamplesCopied );
		yield return new( "collision.builds_completed", CollisionBuildsCompleted );
		yield return new( "collision.uploads", CollisionUploads );
		yield return new( "safety.activations", PlayerSafetyActivations );
		yield return new( "safety.update_calls", PlayerSafetyUpdates );
		yield return new( "safety.players_repositioned", PlayersRepositioned );
		yield return new( "player.traversal_updates", PlayerTraversalUpdates );
	}
}
