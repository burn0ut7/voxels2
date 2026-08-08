internal readonly record struct VoxelTransvoxelBackendCompareResult(
	bool Passed,
	string Failure,
	VoxelTransvoxelBackendCompareBatchResult[] BatchResults
);

internal readonly record struct VoxelTransvoxelBackendCompareBatchResult(
	int BatchSize,
	double CpuDensityGenerationMilliseconds,
	double CpuSnapshotPreparationMilliseconds,
	double CpuMeshMilliseconds,
	double CpuPublicationMilliseconds,
	double CpuTotalWallMilliseconds,
	double CpuChunksPerSecond,
	bool CpuPassed,
	string CpuFailure,
	uint CpuVertexCount,
	uint CpuIndexCount,
	uint CpuSurfaceBlockCount,
	double GpuDensityGenerationMilliseconds,
	double GpuSubmissionMilliseconds,
	double GpuCompletionMilliseconds,
	double GpuPublicationMilliseconds,
	double GpuTotalWallMilliseconds,
	double GpuChunksPerSecond,
	bool GpuPassed,
	string GpuFailure,
	uint GpuVertexCount,
	uint GpuIndexCount,
	uint GpuSurfaceBlockCount,
	uint GpuDispatchCount
);
