internal readonly record struct VoxelGpuTransitionKey(
	int FineSlotId,
	VoxelClipboxFaceDirection Face,
	uint FineGeneration,
	int CoarseSlotId,
	uint CoarseGeneration,
	ushort RuleVersion,
	uint EditRevision );

internal readonly record struct VoxelGpuTransitionMetadataEntry(
	int StableSlotId,
	int FineLevel,
	int CoarseLevel,
	int FineRegularSlotId,
	int CoarseRegularSlotId,
	Vector3Int FineCoordinate,
	Vector3Int CoarseCoordinate,
	VoxelClipboxFaceDirection Face,
	int FaceU,
	int FaceV,
	bool Active,
	bool Pending,
	bool DependenciesValid,
	VoxelGpuTransitionKey Key );

internal sealed class VoxelGpuTransitionMetadata
{
	private readonly VoxelGpuTransitionMetadataEntry[] _current;
	private readonly VoxelGpuTransitionMetadataEntry[] _desired;
	private int _changedSlotCount;
	private int _dependencyMismatchCount;

	public int Capacity => _desired.Length;
	public int ChangedSlotCount => _changedSlotCount;
	public int ActiveCount { get; private set; }
	public int CurrentActiveCount { get; private set; }
	public int PendingCount { get; private set; }
	public int DependencyMismatchCount => _dependencyMismatchCount;
	public bool DependenciesValid => _dependencyMismatchCount == 0;
	public VoxelGpuTransitionMetadataEntry[] Current => _current;
	public VoxelGpuTransitionMetadataEntry[] Desired => _desired;

	public VoxelGpuTransitionMetadata( int capacity )
	{
		if ( capacity < 0 ) throw new System.ArgumentOutOfRangeException( nameof( capacity ) );
		_current = new VoxelGpuTransitionMetadataEntry[capacity];
		_desired = new VoxelGpuTransitionMetadataEntry[capacity];
	}

	public bool Update( VoxelClipboxTransitionSlotAssignment[] assignments, VoxelGpuResidentTable residents, VoxelGpuBatchScheduler scheduler )
	{
		if ( assignments is null || assignments.Length != Capacity ) throw new System.ArgumentException( "Transition assignments do not match the metadata capacity.", nameof( assignments ) );
		if ( residents is null ) throw new System.ArgumentNullException( nameof( residents ) );
		if ( scheduler is null ) throw new System.ArgumentNullException( nameof( scheduler ) );

		ActiveCount = 0;
		PendingCount = 0;
		_dependencyMismatchCount = 0;
		_changedSlotCount = 0;
		for ( var index = 0; index < assignments.Length; index++ )
		{
			var assignment = assignments[index];
			var dependenciesValid = true;
			var fineGeneration = 0u;
			var coarseGeneration = 0u;
			if ( assignment.Active )
			{
				ActiveCount++;
				dependenciesValid = TryGetGeneration( assignment.Key, residents, scheduler, out fineGeneration ) &&
					TryGetGeneration( assignment.CoarseKey, residents, scheduler, out coarseGeneration );
				if ( !dependenciesValid ) _dependencyMismatchCount++;
			}

			var key = new VoxelGpuTransitionKey(
				assignment.FineRegularSlotId,
				assignment.Face,
				fineGeneration,
				assignment.CoarseRegularSlotId,
				coarseGeneration,
				assignment.RuleVersion,
				assignment.EditRevision );
			var entry = new VoxelGpuTransitionMetadataEntry(
				assignment.StableSlotId,
				assignment.FineLevel,
				assignment.CoarseLevel,
				assignment.FineRegularSlotId,
				assignment.CoarseRegularSlotId,
				assignment.FineCoordinate,
				assignment.CoarseCoordinate,
				assignment.Face,
				assignment.FaceU,
				assignment.FaceV,
				assignment.Active,
				false,
				dependenciesValid,
				key );
			var changed = !_current[index].Active && !entry.Active || _current[index] != entry;
			if ( changed ) _changedSlotCount++;
			if ( changed && entry.Active )
			{
				PendingCount++;
				entry = entry with { Pending = true };
			}
			_desired[index] = entry;
		}
		return _changedSlotCount != 0;
	}

	private static bool TryGetGeneration( VoxelVisualBlockKey key, VoxelGpuResidentTable residents, VoxelGpuBatchScheduler scheduler, out uint generation )
	{
		if ( scheduler.TryGetGeneration( key, out generation ) ) return true;
		if ( residents.TryGetPublished( key, out var resident ) )
		{
			generation = resident.Generation;
			return generation != 0;
		}
		generation = 0;
		return false;
	}

	public void Commit()
	{
		for ( var index = 0; index < _desired.Length; index++ )
		{
			var committed = _desired[index] with { Pending = false };
			_desired[index] = committed;
			_current[index] = committed;
		}
		CurrentActiveCount = ActiveCount;
		PendingCount = 0;
	}
}
