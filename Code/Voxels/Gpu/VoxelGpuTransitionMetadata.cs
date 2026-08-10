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
	private readonly bool[] _changed;
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
		_changed = new bool[capacity];
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
			_desired[index] = BuildEntry( assignments[index], residents, scheduler );
			_changed[index] = HasChanged( _current[index], _desired[index] );
			if ( _desired[index].Active ) ActiveCount++;
			if ( !_desired[index].DependenciesValid ) _dependencyMismatchCount++;
			if ( !_changed[index] ) continue;
			_changedSlotCount++;
			if ( _desired[index].Active )
			{
				PendingCount++;
				_desired[index] = _desired[index] with { Pending = true };
			}
		}
		return _changedSlotCount != 0;
	}

	public bool UpdateChanged( VoxelClipboxTransitionSlotAssignment[] assignments, VoxelGpuResidentTable residents, VoxelGpuBatchScheduler scheduler, VoxelClipboxRuntimePlanner planner )
	{
		if ( assignments is null || assignments.Length != Capacity ) throw new System.ArgumentException( "Transition assignments do not match the metadata capacity.", nameof( assignments ) );
		if ( residents is null ) throw new System.ArgumentNullException( nameof( residents ) );
		if ( scheduler is null ) throw new System.ArgumentNullException( nameof( scheduler ) );
		if ( planner is null ) throw new System.ArgumentNullException( nameof( planner ) );

		var anyChanged = false;
		for ( var changedIndex = 0; changedIndex < planner.ChangedTransitionSlotCount; changedIndex++ )
		{
			var slotId = planner.GetChangedTransitionSlotId( changedIndex );
			var previous = _desired[slotId];
			if ( previous.Active ) ActiveCount--;
			if ( previous.Active && !previous.DependenciesValid ) _dependencyMismatchCount--;
			if ( _changed[slotId] )
			{
				_changedSlotCount--;
				if ( previous.Active ) PendingCount--;
			}

			var entry = BuildEntry( assignments[slotId], residents, scheduler );
			var changed = HasChanged( _current[slotId], entry );
			if ( entry.Active ) ActiveCount++;
			if ( entry.Active && !entry.DependenciesValid ) _dependencyMismatchCount++;
			if ( changed )
			{
				_changedSlotCount++;
				if ( entry.Active )
				{
					PendingCount++;
					entry = entry with { Pending = true };
				}
			}
			_changed[slotId] = changed;
			_desired[slotId] = entry;
			anyChanged |= previous != entry;
		}
		return anyChanged;
	}

	private static bool HasChanged( VoxelGpuTransitionMetadataEntry current, VoxelGpuTransitionMetadataEntry desired ) =>
		(current.Active || desired.Active) && current != desired;

	private static VoxelGpuTransitionMetadataEntry BuildEntry( VoxelClipboxTransitionSlotAssignment assignment, VoxelGpuResidentTable residents, VoxelGpuBatchScheduler scheduler )
	{
		var dependenciesValid = true;
		var fineGeneration = 0u;
		var coarseGeneration = 0u;
		if ( assignment.Active )
		{
			dependenciesValid = TryGetGeneration( assignment.Key, residents, scheduler, out fineGeneration ) &&
				TryGetGeneration( assignment.CoarseKey, residents, scheduler, out coarseGeneration );
		}
		var key = new VoxelGpuTransitionKey(
			assignment.FineRegularSlotId,
			assignment.Face,
			fineGeneration,
			assignment.CoarseRegularSlotId,
			coarseGeneration,
			assignment.RuleVersion,
			assignment.EditRevision );
		return new VoxelGpuTransitionMetadataEntry(
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
			_changed[index] = false;
		}
		CurrentActiveCount = ActiveCount;
		PendingCount = 0;
	}
}
