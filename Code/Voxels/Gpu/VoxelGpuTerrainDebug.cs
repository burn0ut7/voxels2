public enum VoxelGpuClipboxDebugMode
{
	Off,
	Lod,
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
