internal readonly record struct VoxelGpuTerrainDiagnostics(
	string Backend,
	bool Available,
	int RuleVersion,
	string LodPolicy,
	int BatchCapacity,
	int ScratchRingCount,
	string VertexAddressing,
	string RetirementMechanism,
	bool MultiDrawIndirect,
	bool IndirectBaseVertex,
	bool IndirectFirstInstance,
	int IndirectCommandGroupSize,
	int RequestedBlocks,
	int ResidentBlocks,
	int PendingCountBatches,
	int PendingEmitBatches,
	int BackpressureEvents,
	int AllocationFailures,
	int StalePublicationsRejected,
	int VisibleDrawCommands,
	long ScratchBytes,
	long PoolCapacityBytes,
	long PoolUsedBytes,
	long PeakPoolUsedBytes,
	long GeometryReadbackBytes,
	double CountSubmissionMilliseconds,
	double CountReadbackAverageMilliseconds,
	int CountReadbackCount,
	double EmitSubmissionMilliseconds,
	double CountSubmissionPerBlockMilliseconds,
	double EmitSubmissionPerBlockMilliseconds,
	VoxelTimingDistribution RequestToVisible,
	VoxelTimingDistribution BatchCompletion,
	int DesiredBlocks,
	int ResidentCapacity,
	int PendingRequestCapacity,
	int PendingRequestCount,
	int PendingPublicationCount,
	bool QueuesBounded,
	string RenderShader,
	bool ProductionLighting,
	bool DepthPrepass,
	int DepthPrepassCommandLists,
	int OpaqueCommandLists,
	string Failure,
	bool CapacityLimited,
	int BlockedRequests,
	int CapacityEvictions,
	int CapacityDeferrals,
	int VertexFree,
	int IndexFree,
	int VertexLargestFree,
	int IndexLargestFree,
	int VertexFreeRangeCount,
	int IndexFreeRangeCount,
	ulong ClipboxRevision,
	int ClipboxChangedSlots,
	int ClipboxPendingRevisionCount,
	int ClipboxMaximumPendingRevisionCount,
	int ClipboxStableSlotCount,
	int ClipboxActiveSlotCount,
	int ClipboxDroppedWork,
	int ClipboxStationaryUpdates,
	int ClipboxTransitionCapacity,
	int ClipboxTransitionActiveSlotCount,
	int ClipboxTransitionChangedSlots,
	int ClipboxTransitionPendingSlots,
	int ClipboxTransitionDependencyMismatches,
	int ClipboxTransitionStationaryUpdates,
	int TransitionResidentBlocks,
	int TransitionRenderableResidents,
	int TransitionVisibleDrawCommands,
	int TransitionPendingRequests,
	int TransitionBlockedRequests,
	int TransitionAllocatedVertexCount,
	int TransitionAllocatedIndexCount,
	long TransitionAllocatedBytes,
	long TransitionGeometryReadbackBytes,
	long TransitionCpuSdfEvaluations,
	int TransitionStaleSchedulerRejections,
	int TransitionStaleDependencyRejections );

internal sealed class VoxelGpuTerrainDiagnosticCounters
{
	public int DesiredBlocks;
	public int ResidentCapacity;
	public int PendingRequestCapacity;
	public int PendingRequestCount;
	public int PendingPublicationCount;
	public int TransitionPendingRequests;
	public int TransitionBlockedRequests;
	public bool QueuesBounded = true;
	private double _countReadbackTotalMilliseconds;
	private int _countReadbackCount;
	private readonly List<double> _requestToVisibleMilliseconds = new();
	private readonly List<double> _batchCompletionMilliseconds = new();

	public int RequestedBlocks;
	public int RuleVersion;
	public string LodPolicy = "fixed_lod_0";
	public int PendingCountBatches;
	public int PendingEmitBatches;
	public int BackpressureEvents;
	public int StalePublicationsRejected;
	public int VisibleDrawCommands;
	public long ScratchBytes;
	public double CountSubmissionMilliseconds;
	public double EmitSubmissionMilliseconds;
	public string Failure = string.Empty;
	public bool CapacityLimited;
	public int BlockedRequests;
	public int CapacityEvictions;
	public int CapacityDeferrals;
	public ulong ClipboxRevision;
	public int ClipboxChangedSlots;
	public int ClipboxPendingRevisionCount;
	public int ClipboxMaximumPendingRevisionCount;
	public int ClipboxStableSlotCount;
	public int ClipboxActiveSlotCount;
	public int ClipboxDroppedWork;
	public int ClipboxStationaryUpdates;
	public int ClipboxTransitionCapacity;
	public int ClipboxTransitionActiveSlotCount;
	public int ClipboxTransitionChangedSlots;
	public int ClipboxTransitionPendingSlots;
	public int ClipboxTransitionDependencyMismatches;
	public int ClipboxTransitionStationaryUpdates;
	public int TransitionVisibleDrawCommands;
	public int TransitionStaleSchedulerRejections;
	public int TransitionStaleDependencyRejections;

	public void RecordCountReadback( double milliseconds )
	{
		_countReadbackTotalMilliseconds += milliseconds;
		_countReadbackCount++;
	}

	public void RecordRequestToVisible( double milliseconds ) => _requestToVisibleMilliseconds.Add( milliseconds );
	public void RecordBatchCompletion( double milliseconds ) => _batchCompletionMilliseconds.Add( milliseconds );

	public VoxelGpuTerrainDiagnostics Snapshot( VoxelGpuCapabilityReport capabilities, VoxelGpuResidentTable residents, VoxelGpuMeshPool pool, VoxelGpuTerrainRenderer renderer )
	{
		var usedBytes = pool is null ? 0 : (long)pool.UsedVertices * 44 + (long)pool.UsedIndices * sizeof( uint );
		var peakBytes = pool is null ? 0 : (long)pool.PeakUsedVertices * 44 + (long)pool.PeakUsedIndices * sizeof( uint );
		var transitionResidents = 0;
		var transitionRenderable = 0;
		var transitionVertices = 0;
		var transitionIndices = 0;
		residents?.GetPublishedAllocationTotals( true, out transitionResidents, out transitionRenderable, out transitionVertices, out transitionIndices );
		return new VoxelGpuTerrainDiagnostics(
			"gpu_persistent_fixed_lod",
			capabilities.Available,
			RuleVersion,
			LodPolicy,
			VoxelGpuScratchArena.MaximumBatchSize,
			VoxelGpuScratchArena.RingSize,
			capabilities.VertexAddressing,
			capabilities.RetirementMechanism,
			capabilities.MultiDrawIndirect,
			capabilities.IndirectBaseVertex,
			capabilities.IndirectFirstInstance,
			capabilities.IndirectCommandGroupSize,
			RequestedBlocks,
			residents?.PublishedCountFor( false ) ?? 0,
			PendingCountBatches,
			PendingEmitBatches,
			BackpressureEvents,
			pool?.AllocationFailures ?? 0,
			StalePublicationsRejected,
			VisibleDrawCommands,
			ScratchBytes,
			pool?.CapacityBytes ?? 0,
			usedBytes,
			peakBytes,
			0,
			CountSubmissionMilliseconds,
			_countReadbackCount == 0 ? 0.0 : _countReadbackTotalMilliseconds / _countReadbackCount,
			_countReadbackCount,
			EmitSubmissionMilliseconds,
			RequestedBlocks == 0 ? 0.0 : CountSubmissionMilliseconds / RequestedBlocks,
			RequestedBlocks == 0 ? 0.0 : EmitSubmissionMilliseconds / RequestedBlocks,
			Summarize( _requestToVisibleMilliseconds ),
			Summarize( _batchCompletionMilliseconds ),
			DesiredBlocks,
			ResidentCapacity,
			PendingRequestCapacity,
			PendingRequestCount,
			PendingPublicationCount,
			QueuesBounded,
			VoxelGpuTerrainRenderer.ShaderName,
			renderer?.UsesProductionLighting == true,
			renderer?.UsesDepthPrepass == true,
			renderer?.DepthPrepassCommandListCount ?? 0,
			renderer?.OpaqueCommandListCount ?? 0,
			Failure.Length > 0 ? Failure : capabilities.Failure,
			CapacityLimited,
			BlockedRequests,
			CapacityEvictions,
			CapacityDeferrals,
			pool?.VertexFree ?? 0,
			pool?.IndexFree ?? 0,
			pool?.VertexLargestFree ?? 0,
			pool?.IndexLargestFree ?? 0,
			pool?.VertexFreeRangeCount ?? 0,
			pool?.IndexFreeRangeCount ?? 0,
			ClipboxRevision,
			ClipboxChangedSlots,
			ClipboxPendingRevisionCount,
			ClipboxMaximumPendingRevisionCount,
			ClipboxStableSlotCount,
			ClipboxActiveSlotCount,
			ClipboxDroppedWork,
			ClipboxStationaryUpdates,
			ClipboxTransitionCapacity,
			ClipboxTransitionActiveSlotCount,
			ClipboxTransitionChangedSlots,
			ClipboxTransitionPendingSlots,
			ClipboxTransitionDependencyMismatches,
			ClipboxTransitionStationaryUpdates,
			transitionResidents,
			transitionRenderable,
			TransitionVisibleDrawCommands,
			TransitionPendingRequests,
			TransitionBlockedRequests,
			transitionVertices,
			transitionIndices,
			(long)transitionVertices * 44 + (long)transitionIndices * sizeof( uint ),
			0,
			0,
			TransitionStaleSchedulerRejections,
			TransitionStaleDependencyRejections );
	}

	private static VoxelTimingDistribution Summarize( List<double> values )
	{
		if ( values.Count == 0 ) return default;
		var sorted = values.OrderBy( value => value ).ToArray();
		var total = sorted.Sum();
		var p95Index = System.Math.Clamp( (int)System.Math.Ceiling( sorted.Length * 0.95 ) - 1, 0, sorted.Length - 1 );
		return new VoxelTimingDistribution( sorted.Length, total / sorted.Length, sorted[p95Index], sorted[^1] );
	}
}
