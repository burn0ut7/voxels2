internal readonly record struct VoxelClipboxBounds( Vector3Int Min, Vector3Int MaxExclusive )
{
	public int SizeX => MaxExclusive.x - Min.x;
	public int SizeY => MaxExclusive.y - Min.y;
	public int SizeZ => MaxExclusive.z - Min.z;
	public bool Contains( Vector3Int coordinate ) =>
		coordinate.x >= Min.x && coordinate.x < MaxExclusive.x &&
		coordinate.y >= Min.y && coordinate.y < MaxExclusive.y &&
		coordinate.z >= Min.z && coordinate.z < MaxExclusive.z;
	public int Volume => checked( SizeX * SizeY * SizeZ );
}

internal readonly record struct VoxelClipboxLevelState(
	int Level,
	Vector3Int Origin,
	VoxelClipboxBounds Outer,
	VoxelClipboxBounds Inner,
	int ActiveCount,
	int StableSlotBase,
	int SampleSpacing )
{
	public bool IsActive( Vector3Int coordinate ) => Outer.Contains( coordinate ) && (Level == 0 || !Inner.Contains( coordinate ));
	public int StableSlotCount => Outer.Volume;
}

internal enum VoxelClipboxSlotState
{
	Inactive,
	Current,
	PendingCount,
	PendingAllocation,
	PendingEmit,
	PendingCommit,
	FailedDeferred
}

internal readonly record struct VoxelClipboxRegularSlotAssignment(
	int StableSlotId,
	int Lod,
	Vector3Int Coordinate,
	bool Active,
	VoxelVisualBlockKey Key );
