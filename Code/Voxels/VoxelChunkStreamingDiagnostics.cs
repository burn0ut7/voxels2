public readonly record struct VoxelTimingDistribution(
	int Count,
	double AverageMilliseconds,
	double P95Milliseconds,
	double MaximumMilliseconds
);

public readonly record struct VoxelChunkStreamingDiagnostics(
	int CompletedChunks,
	int FreshGeneratedChunks,
	int CachedChunks,
	int CompletedBatches,
	VoxelTimingDistribution SdfGeneration,
	VoxelTimingDistribution MeshQueue,
	VoxelTimingDistribution SdfSnapshot,
	VoxelTimingDistribution WorkerMesh,
	VoxelTimingDistribution PublicationWait,
	VoxelTimingDistribution MainThreadUpload,
	VoxelTimingDistribution RequestToRender,
	VoxelTimingDistribution BatchCompletion
);
