internal sealed class VoxelClipboxRuntimePlanner
{
	private readonly VoxelClipboxConfig _config;
	private readonly VoxelClipboxLevelState[] _currentLevels;
	private readonly VoxelClipboxLevelState[] _desiredLevels;
	private readonly VoxelClipboxRegularSlotAssignment[] _currentSlots;
	private readonly VoxelClipboxRegularSlotAssignment[] _desiredSlots;
	private readonly VoxelClipboxTransitionSlotAssignment[] _currentTransitions;
	private readonly VoxelClipboxTransitionSlotAssignment[] _desiredTransitions;
	private readonly int[] _changedSlotIds;
	private readonly int[] _changedTransitionSlotIds;
	private readonly bool[] _changedRegularFlags;
	private readonly VoxelVisualBlockKey[] _changedPreviousSlotKeys;
	private readonly VoxelVisualBlockKey[] _changedPreviousTransitionKeys;
	private readonly List<(BBox Bounds, uint Revision)> _editRegions = new();
	private bool _hasCurrentPlan;
	private int _chunkSize = VoxelClipboxConfig.CellsPerBlock;
	private ulong _revision;
	private int _changedSlotCount;
	private int _changedTransitionSlotCount;

	public VoxelClipboxConfig Config => _config;
	public bool HasCurrentPlan => _hasCurrentPlan;
	public ulong Revision => _revision;
	public Vector3Int ObserverBaseBlock { get; private set; }
	public int ChangedSlotCount => _changedSlotCount;
	public int ChangedTransitionSlotCount => _changedTransitionSlotCount;
	public int ActiveRegularCount { get; private set; }
	public int CurrentActiveRegularCount { get; private set; }
	public int ActiveTransitionCount { get; private set; }
	public int CurrentActiveTransitionCount { get; private set; }
	public int StableTransitionSlotCount => _desiredTransitions.Length;
	public VoxelClipboxLevelState[] CurrentLevels => _currentLevels;
	public VoxelClipboxLevelState[] DesiredLevels => _desiredLevels;
	public VoxelClipboxRegularSlotAssignment[] CurrentSlots => _currentSlots;
	public VoxelClipboxRegularSlotAssignment[] DesiredSlots => _desiredSlots;
	public VoxelClipboxTransitionSlotAssignment[] CurrentTransitions => _currentTransitions;
	public VoxelClipboxTransitionSlotAssignment[] DesiredTransitions => _desiredTransitions;

	public VoxelClipboxRuntimePlanner( VoxelClipboxConfig config )
	{
		_config = config;
		_currentLevels = new VoxelClipboxLevelState[config.LevelCount];
		_desiredLevels = new VoxelClipboxLevelState[config.LevelCount];
		_currentSlots = new VoxelClipboxRegularSlotAssignment[config.StableRegularSlotCount];
		_desiredSlots = new VoxelClipboxRegularSlotAssignment[config.StableRegularSlotCount];
		_currentTransitions = new VoxelClipboxTransitionSlotAssignment[config.StableTransitionSlotCount];
		_desiredTransitions = new VoxelClipboxTransitionSlotAssignment[_currentTransitions.Length];
		_changedSlotIds = new int[config.StableRegularSlotCount];
		_changedTransitionSlotIds = new int[_currentTransitions.Length];
		_changedRegularFlags = new bool[config.StableRegularSlotCount];
		_changedPreviousSlotKeys = new VoxelVisualBlockKey[config.StableRegularSlotCount];
		_changedPreviousTransitionKeys = new VoxelVisualBlockKey[_currentTransitions.Length];
	}

	public bool Update( Vector3Int observerCanonicalSample )
	{
		ObserverBaseBlock = VoxelClipboxCoordinates.GetObserverBaseBlock( observerCanonicalSample );
		ActiveRegularCount = VoxelClipboxReferencePlanner.Populate( ObserverBaseBlock, _config, _desiredLevels, _desiredSlots );
		ApplyStoredEditRevisions();
		ActiveTransitionCount = VoxelClipboxTransitionPlanner.Populate( _config, _desiredLevels, _desiredSlots, _desiredTransitions );
		_changedSlotCount = 0;
		_changedTransitionSlotCount = 0;
		for ( var index = 0; index < _desiredSlots.Length; index++ )
		{
			if ( ( !_hasCurrentPlan && _desiredSlots[index].Active ) || ( _hasCurrentPlan && !SlotsMatchForDelta( _currentSlots[index], _desiredSlots[index] ) ) )
				_changedSlotIds[_changedSlotCount++] = index;
		}
		for ( var index = 0; index < _desiredTransitions.Length; index++ )
		{
			if ( ( !_hasCurrentPlan && _desiredTransitions[index].Active ) || ( _hasCurrentPlan && !TransitionsMatchForDelta( _currentTransitions[index], _desiredTransitions[index] ) ) )
				_changedTransitionSlotIds[_changedTransitionSlotCount++] = index;
		}
		_revision++;
		return _changedSlotCount != 0 || _changedTransitionSlotCount != 0;
	}

	public bool ApplyEdit( VoxelEditOp operation, uint editRevision, int chunkSize )
	{
		if ( _editRegions.Count != 0 && chunkSize != _chunkSize ) throw new System.InvalidOperationException( "The clipbox chunk size cannot change while sparse edits are active." );
		_chunkSize = chunkSize;
		_editRegions.Add( (VoxelEditJournal.GetBounds( operation ), editRevision) );
		_changedSlotCount = 0;
		_changedTransitionSlotCount = 0;
		System.Array.Clear( _changedRegularFlags );
		for ( var index = 0; index < _desiredSlots.Length; index++ )
		{
			var assignment = _desiredSlots[index];
			if ( !assignment.Active || assignment.Key.EditRevision >= editRevision || !VoxelEditInvalidation.OverlapsVisualBlock( operation, assignment.Key, chunkSize ) ) continue;
			_changedPreviousSlotKeys[_changedSlotCount] = assignment.Key;
			_desiredSlots[index] = assignment with { Key = assignment.Key with { EditRevision = editRevision } };
			_changedRegularFlags[index] = true;
			_changedSlotIds[_changedSlotCount++] = index;
		}
		for ( var index = 0; index < _desiredTransitions.Length; index++ )
		{
			var assignment = _desiredTransitions[index];
			if ( !assignment.Active || (!_changedRegularFlags[assignment.FineRegularSlotId] && !_changedRegularFlags[assignment.CoarseRegularSlotId]) ) continue;
			var fineKey = _desiredSlots[assignment.FineRegularSlotId].Key;
			var coarseKey = _desiredSlots[assignment.CoarseRegularSlotId].Key;
			var revision = System.Math.Max( fineKey.EditRevision, coarseKey.EditRevision );
			var updated = assignment with { EditRevision = revision, Key = fineKey, CoarseKey = coarseKey };
			if ( updated == assignment ) continue;
			_changedPreviousTransitionKeys[_changedTransitionSlotCount] = VoxelClipboxTransitionPlanner.GetVisualKey( assignment );
			_desiredTransitions[index] = updated;
			_changedTransitionSlotIds[_changedTransitionSlotCount++] = index;
		}
		_revision++;
		return _changedSlotCount != 0 || _changedTransitionSlotCount != 0;
	}

	private void ApplyStoredEditRevisions()
	{
		if ( _editRegions.Count == 0 ) return;
		for ( var index = 0; index < _desiredSlots.Length; index++ )
		{
			var assignment = _desiredSlots[index];
			if ( !assignment.Active ) continue;
			var step = 1 << assignment.Lod;
			var origin = VoxelGpuCanonicalCoordinates.CanonicalBlockOriginSamples( assignment.Coordinate, assignment.Lod, _chunkSize );
			var halo = step * 2.0f;
			var bounds = new BBox( origin - Vector3.One * halo, origin + Vector3.One * (_chunkSize * step + halo) );
			var revision = assignment.Key.EditRevision;
			foreach ( var region in _editRegions )
				if ( bounds.Overlaps( region.Bounds ) ) revision = System.Math.Max( revision, region.Revision );
			if ( revision != assignment.Key.EditRevision ) _desiredSlots[index] = assignment with { Key = assignment.Key with { EditRevision = revision } };
		}
	}

	private static bool SlotsMatchForDelta( VoxelClipboxRegularSlotAssignment current, VoxelClipboxRegularSlotAssignment desired ) =>
		!current.Active && !desired.Active || current == desired;

	private static bool TransitionsMatchForDelta( VoxelClipboxTransitionSlotAssignment current, VoxelClipboxTransitionSlotAssignment desired ) =>
		!current.Active && !desired.Active || current == desired;

	public int GetChangedSlotId( int index )
	{
		if ( index < 0 || index >= _changedSlotCount ) throw new System.ArgumentOutOfRangeException( nameof( index ) );
		return _changedSlotIds[index];
	}

	public int GetChangedTransitionSlotId( int index )
	{
		if ( index < 0 || index >= _changedTransitionSlotCount ) throw new System.ArgumentOutOfRangeException( nameof( index ) );
		return _changedTransitionSlotIds[index];
	}

	public VoxelVisualBlockKey GetPreviousChangedSlotKey( int index )
	{
		if ( index < 0 || index >= _changedSlotCount ) throw new System.ArgumentOutOfRangeException( nameof( index ) );
		return _changedPreviousSlotKeys[index];
	}

	public VoxelVisualBlockKey GetPreviousChangedTransitionKey( int index )
	{
		if ( index < 0 || index >= _changedTransitionSlotCount ) throw new System.ArgumentOutOfRangeException( nameof( index ) );
		return _changedPreviousTransitionKeys[index];
	}

	public void Commit()
	{
		if ( !_hasCurrentPlan )
			_hasCurrentPlan = true;
		System.Array.Copy( _desiredLevels, _currentLevels, _desiredLevels.Length );
		System.Array.Copy( _desiredSlots, _currentSlots, _desiredSlots.Length );
		System.Array.Copy( _desiredTransitions, _currentTransitions, _desiredTransitions.Length );
		CurrentActiveRegularCount = ActiveRegularCount;
		CurrentActiveTransitionCount = ActiveTransitionCount;
		_changedSlotCount = 0;
		_changedTransitionSlotCount = 0;
	}
}
