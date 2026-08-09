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
	private bool _hasCurrentPlan;
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
	}

	public bool Update( Vector3Int observerCanonicalSample )
	{
		ObserverBaseBlock = VoxelClipboxCoordinates.GetObserverBaseBlock( observerCanonicalSample );
		ActiveRegularCount = VoxelClipboxReferencePlanner.Populate( ObserverBaseBlock, _config, _desiredLevels, _desiredSlots );
		ActiveTransitionCount = VoxelClipboxTransitionPlanner.Populate( _config, _desiredLevels, _desiredTransitions );
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
