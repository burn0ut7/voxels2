public enum VoxelGpuClipboxDebugMode
{
	Off,
	/// <summary>Draw only active regular blocks that have published index data.</summary>
	Lod,
	/// <summary>Draw every active regular slot, including missing, pending, and empty slots.</summary>
	SlotAndMesh,
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
