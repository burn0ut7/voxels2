public enum VoxelGpuClipboxDebugMode
{
	Off,
	/// <summary>Draw only active regular blocks that have published index data.</summary>
	Lod,
	/// <summary>Draw every active regular slot, including missing, pending, and empty slots.</summary>
	SlotAndMesh,
	/// <summary>Draw only regular blocks that own one or more transition boundary layers.</summary>
	TransitionMasks,
	/// <summary>Draw transition bounds and hide regular block bounds.</summary>
	TransitionOnly,
	/// <summary>Highlight missing transition work in red.</summary>
	MissingTransitions,
	/// <summary>Highlight transition dependency mismatches in orange.</summary>
	DependencyMismatches,
	/// <summary>Draw active regular and transition bounds with revision-oriented labels.</summary>
	Revision,
	/// <summary>Draw the regular and transition ownership wireframe.</summary>
	Wireframe,
	/// <summary>Draw seam proof markers from the opt-in production readback.</summary>
	SeamErrors,
	Full
}

internal enum VoxelGpuDebugBlockState
{
	Missing,
	Pending,
	Resident,
	Deferred
}

internal readonly record struct VoxelGpuClipboxDebugBlock(
	Vector3Int Coordinate,
	int Lod,
	uint TransitionFaceMask,
	int StableSlotId,
	VoxelGpuDebugBlockState State,
	uint Generation,
	int VertexOffset,
	int VertexCapacity,
	int IndexOffset,
	int IndexCapacity,
	uint IndexCount,
	Vector3 DrawOrigin,
	Vector3 BoundsMin,
	Vector3 BoundsMax );

internal readonly record struct VoxelGpuClipboxDebugTransition(
	Vector3Int FineCoordinate,
	Vector3Int CoarseCoordinate,
	int FineLod,
	int CoarseLod,
	VoxelClipboxFaceDirection Face,
	int StableSlotId,
	VoxelGpuDebugBlockState State,
	uint FineGeneration,
	uint CoarseGeneration,
	bool DependenciesValid,
	int VertexOffset,
	int VertexCapacity,
	int IndexOffset,
	int IndexCapacity,
	uint IndexCount );

internal readonly record struct VoxelGpuClipboxSeamProofReport(
	bool Passed,
	string Failure,
	int ActiveTransitions,
	int PublishedTransitions,
	int MissingTransitions,
	int DependencyMismatches,
	int FaceMaskMismatches,
	int ZeroGeometryTransitions,
	int InvalidIndices,
	int DegenerateTriangles,
	int BoundaryVertices,
	int UnmatchedBoundaryVertices,
	float MaximumSeamPositionError,
	float MaximumOriginAlignmentError,
	long GeometryReadbackBytes,
	double GeometryReadbackMilliseconds );
