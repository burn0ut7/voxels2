internal sealed class VoxelGpuTerrainBackend : SceneCustomObject, System.IDisposable
{
	private readonly VoxelGpuCapabilityReport _capabilities;
	private readonly VoxelGpuTerrainDiagnosticCounters _diagnostics = new();
	private readonly VoxelGpuBatchScheduler _scheduler;
	private readonly VoxelGpuBatchScheduler _transitionScheduler;
	private readonly VoxelGpuScratchArena[] _scratchRing;
	private readonly VoxelGpuTransitionScratchArena _transitionScratch;
	private readonly VoxelGpuMeshPool _pool;
	private readonly VoxelGpuResidentTable _residents;
	private readonly VoxelGpuTerrainRenderer _renderer;
	private readonly VoxelClipboxRuntimePlanner _clipboxPlanner;
	private readonly VoxelGpuTransitionMetadata _clipboxTransitions;
	private readonly List<VoxelVisualBlockKey> _clipboxKeyScratch;
	private readonly HashSet<VoxelVisualBlockKey> _clipboxCombinedKeySet = new();
	private readonly List<VoxelVisualBlockKey> _clipboxCombinedKeyScratch = new();
	private readonly Queue<PendingPublication> _publications = new();
	private readonly int _chunkSize;
	private readonly float _voxelSize;
	private readonly BatchContext[] _activeBatches;
	private readonly VoxelGpuBatchScheduler.ScheduledRequest[][] _scheduledScratch;
	private readonly VoxelGpuBatchScheduler.ScheduledRequest[] _transitionScheduledScratch;
	private readonly VoxelGpuBlockRequest[][] _requestScratch;
	private readonly VoxelGpuTransitionRequest[] _transitionRequestScratch;
	private readonly int[][] _slotScratch;
	private readonly int[] _transitionSlotScratch;
	private readonly VoxelGpuAllocationDescriptor[][] _allocationScratch;
	private readonly VoxelGpuAllocationDescriptor[] _transitionAllocationScratch;
	private readonly PendingResident[][] _pendingScratch;
	private readonly PendingResident[] _transitionPendingScratch;
	private readonly PendingResident[][] _publicationScratch;
	private readonly PendingResident[] _transitionPublicationScratch;
	private readonly HashSet<VoxelVisualBlockKey> _desiredKeys = new();
	private readonly HashSet<VoxelVisualBlockKey> _desiredTransitionKeys = new();
	private readonly HashSet<VoxelVisualBlockKey> _desiredScratch = new();
	private readonly HashSet<VoxelVisualBlockKey> _desiredTransitionScratch = new();
	private readonly List<VoxelVisualBlockKey> _desiredOrder = new();
	private readonly List<VoxelVisualBlockKey> _desiredOrderScratch = new();
	private readonly List<VoxelVisualBlockKey> _desiredKeyInputScratch = new();
	private readonly Dictionary<VoxelVisualBlockKey, int> _desiredRanks = new();
	private readonly List<VoxelVisualBlockKey> _leavingScratch = new();
	private readonly List<VoxelVisualBlockKey> _transitionLeavingScratch = new();
	private readonly HashSet<VoxelVisualBlockKey> _pendingRequests = new();
	private readonly HashSet<VoxelVisualBlockKey> _pendingTransitionRequests = new();
	private readonly HashSet<VoxelVisualBlockKey> _blockedRequests = new();
	private readonly HashSet<VoxelVisualBlockKey> _blockedTransitionRequests = new();
	private readonly Dictionary<VoxelVisualBlockKey, BlockedRequest> _blockedDetails = new();
	private readonly List<VoxelVisualBlockKey> _wakeScratch = new();
	private readonly List<VoxelVisualBlockKey> _transitionWakeScratch = new();
	private readonly VoxelGpuResidentTable.ResidentEntry[] _publishedScratch;
	private readonly List<EvictionCandidate> _evictionCandidates;
	private readonly Dictionary<VoxelVisualBlockKey, VoxelGpuTransitionKey> _transitionDependencyKeys = new();
	private readonly Dictionary<VoxelVisualBlockKey, VoxelGpuTransitionKey> _publishedTransitionDependencies = new();
	private readonly HashSet<VoxelVisualBlockKey> _transitionRebuildKeys = new();
	private readonly Dictionary<VoxelVisualBlockKey, BlockedRequest> _blockedTransitionDetails = new();
	private readonly object _desiredSync = new();
	private ulong _epoch;
	private long _nextProgressLogTimestamp;
	private bool _settledLogged;
	private bool _disposed;
	private int _slowRenderLogCount;
	private int _slowDesiredSetLogCount;
	private bool _processingEnabled = true;
	private bool _clipboxRevisionPending;
	private int _retireUndesiredRequested;
	private int _worstPublishedRank = -1;
	private readonly long _createdTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
	private long _frameCounter;
	private long _clipboxRevisionPlannedTimestamp;
	private string _structuredDebugReportJson = "{}";
	private string _latestRevisionEventJson = "{}";
	private string _latestRevisionEventState = "none";
	private readonly List<string> _revisionEventHistory = new( 64 );
	private readonly List<VoxelGpuHitchTrace> _hitchTrace = new( 32 );
	private readonly long[] _levelCountBatches = System.Array.Empty<long>();
	private readonly long[] _levelEmitBatches = System.Array.Empty<long>();
	private readonly long[] _levelLastUpdateFrame = System.Array.Empty<long>();
	private readonly long[] _levelUpdateCount = System.Array.Empty<long>();

	public bool IsSettled => !_clipboxRevisionPending && IsStreamingWorkIdle && HasExactlyDesiredResidents();
	public bool IsCapacityLimited => BlockedRequestCount > 0;
	private int DesiredCount { get { lock ( _desiredSync ) return _desiredKeys.Count; } }
	private int TransitionDesiredCount { get { lock ( _desiredSync ) return _desiredTransitionKeys.Count; } }
	private int PendingRequestCount { get { lock ( _desiredSync ) return _pendingRequests.Count; } }
	private int PendingTransitionRequestCount { get { lock ( _desiredSync ) return _pendingTransitionRequests.Count; } }
	private int BlockedRequestCount { get { lock ( _desiredSync ) return _blockedRequests.Count; } }
	private int BlockedTransitionRequestCount { get { lock ( _desiredSync ) return _blockedTransitionRequests.Count; } }
	private bool IsDesired( VoxelVisualBlockKey key ) { lock ( _desiredSync ) return key.IsTransition ? _desiredTransitionKeys.Contains( key ) : _desiredKeys.Contains( key ); }
	private bool HasExactlyDesiredResidents()
	{
		lock ( _desiredSync )
		{
			var desiredCount = _desiredKeys.Count + _desiredTransitionKeys.Count;
			var blockedCount = _blockedRequests.Count + _blockedTransitionRequests.Count;
			if ( _residents.PublishedCount > desiredCount || _residents.PublishedCount < desiredCount - blockedCount ) return false;
			foreach ( var key in _desiredKeys ) if ( !_residents.ContainsKey( key ) && !_blockedRequests.Contains( key ) ) return false;
			foreach ( var key in _desiredTransitionKeys ) if ( !_residents.ContainsKey( key ) && !_blockedTransitionRequests.Contains( key ) ) return false;
			return true;
		}
	}
	private bool IsStreamingWorkIdle => _scheduler.PendingCount == 0 && _transitionScheduler.PendingCount == 0 && PendingRequestCount == 0 && PendingTransitionRequestCount == 0 && _activeBatches.All( batch => batch is null || batch.Count == 0 ) && _transitionBatch.Count == 0 && _publications.Count == 0 && _scratchRing.All( scratch => scratch.IsIdle ) && _transitionScratch.IsIdle;
	private BatchContext _transitionBatch;
	public bool IsAvailable => _capabilities.Available;
	public bool IsTerrainRenderingEnabled => _renderer.IsTerrainRenderingEnabled;
	public bool IsProcessingEnabled => _processingEnabled;
	public int DesiredBlockCount => DesiredCount;
	public bool UsesRegularClipbox => _clipboxPlanner is not null;
	public string StructuredDebugReportJson => _structuredDebugReportJson;

	public VoxelGpuTerrainBackend(
		SceneWorld world,
		CameraComponent camera,
		int chunkSize,
		float voxelSize,
		float sdfClampDistance,
		float simplexFrequency,
		float simplexAmplitude,
		float simplexBaseHeight,
		int simplexSeed,
		int cullingPaddingChunks,
		int residentCapacity,
		int vertexCapacity,
		int indexCapacity,
		VoxelClipboxConfig? clipboxConfig = null )
		: base( world )
	{
		_capabilities = VoxelGpuCapabilities.Detect();
		if ( !_capabilities.Available ) throw new System.InvalidOperationException( _capabilities.Failure );
		var levelCount = clipboxConfig?.LevelCount ?? 0;
		_levelCountBatches = new long[levelCount];
		_levelEmitBatches = new long[levelCount];
		_levelLastUpdateFrame = new long[levelCount];
		_levelUpdateCount = new long[levelCount];
		VoxelGpuContractValidation.AssertLayouts();
		_chunkSize = chunkSize;
		_voxelSize = voxelSize;
		_scheduler = new VoxelGpuBatchScheduler( System.Math.Max( 1, residentCapacity * 2 ) );
		_transitionScheduler = new VoxelGpuBatchScheduler( System.Math.Max( 1, residentCapacity ) );
		_scratchRing = new VoxelGpuScratchArena[VoxelGpuScratchArena.RingSize];
		for ( var index = 0; index < _scratchRing.Length; index++ )
			_scratchRing[index] = new VoxelGpuScratchArena( chunkSize, voxelSize, sdfClampDistance, simplexFrequency, simplexAmplitude, simplexBaseHeight, simplexSeed );
		_activeBatches = new BatchContext[_scratchRing.Length];
		_scheduledScratch = new VoxelGpuBatchScheduler.ScheduledRequest[_scratchRing.Length][];
		_requestScratch = new VoxelGpuBlockRequest[_scratchRing.Length][];
		_slotScratch = new int[_scratchRing.Length][];
		_allocationScratch = new VoxelGpuAllocationDescriptor[_scratchRing.Length][];
		_pendingScratch = new PendingResident[_scratchRing.Length][];
		_publicationScratch = new PendingResident[_scratchRing.Length][];
		for ( var index = 0; index < _scratchRing.Length; index++ )
		{
			_scheduledScratch[index] = new VoxelGpuBatchScheduler.ScheduledRequest[VoxelGpuScratchArena.MaximumBatchSize];
			_requestScratch[index] = new VoxelGpuBlockRequest[VoxelGpuScratchArena.MaximumBatchSize];
			_slotScratch[index] = new int[VoxelGpuScratchArena.MaximumBatchSize];
			_allocationScratch[index] = new VoxelGpuAllocationDescriptor[VoxelGpuScratchArena.MaximumBatchSize];
			_pendingScratch[index] = new PendingResident[VoxelGpuScratchArena.MaximumBatchSize];
			_publicationScratch[index] = new PendingResident[VoxelGpuScratchArena.MaximumBatchSize];
			_activeBatches[index] = new BatchContext( _scheduledScratch[index], _slotScratch[index] );
		}
		_transitionPublicationScratch = new PendingResident[VoxelGpuTransitionScratchArena.MaximumBatchSize];
		_transitionScratch = new VoxelGpuTransitionScratchArena( chunkSize, voxelSize, sdfClampDistance, simplexFrequency, simplexAmplitude, simplexBaseHeight, simplexSeed );
		_transitionScheduledScratch = new VoxelGpuBatchScheduler.ScheduledRequest[VoxelGpuTransitionScratchArena.MaximumBatchSize];
		_transitionRequestScratch = new VoxelGpuTransitionRequest[VoxelGpuTransitionScratchArena.MaximumBatchSize];
		_transitionSlotScratch = new int[VoxelGpuTransitionScratchArena.MaximumBatchSize];
		_transitionAllocationScratch = new VoxelGpuAllocationDescriptor[VoxelGpuTransitionScratchArena.MaximumBatchSize];
		_transitionPendingScratch = new PendingResident[VoxelGpuTransitionScratchArena.MaximumBatchSize];
		_transitionBatch = new BatchContext( _transitionScheduledScratch, _transitionSlotScratch );
		_pool = new VoxelGpuMeshPool( vertexCapacity, indexCapacity );
		_residents = new VoxelGpuResidentTable( residentCapacity );
		if ( clipboxConfig.HasValue )
		{
			_clipboxPlanner = new VoxelClipboxRuntimePlanner( clipboxConfig.Value );
			_clipboxTransitions = new VoxelGpuTransitionMetadata( _clipboxPlanner.StableTransitionSlotCount );
			_clipboxKeyScratch = new List<VoxelVisualBlockKey>( clipboxConfig.Value.ExpectedActiveRegularCount );
			_diagnostics.LodPolicy = $"regular_clipbox_b{clipboxConfig.Value.BlocksPerAxis}_l{clipboxConfig.Value.LevelCount}";
			_diagnostics.ClipboxStableSlotCount = clipboxConfig.Value.StableRegularSlotCount;
			_diagnostics.ClipboxTransitionCapacity = _clipboxTransitions.Capacity;
		}
		_publishedScratch = new VoxelGpuResidentTable.ResidentEntry[residentCapacity];
		_evictionCandidates = new List<EvictionCandidate>( residentCapacity );
		_diagnostics.ResidentCapacity = residentCapacity;
		_diagnostics.PendingRequestCapacity = _scheduler.MaximumPendingRequests;
		_renderer = new VoxelGpuTerrainRenderer( world, camera, _pool, _residents, _diagnostics, _capabilities.IndirectCommandGroupSize, System.Math.Max( 0, cullingPaddingChunks ) * chunkSize * voxelSize );
		_diagnostics.ScratchBytes = _scratchRing.Sum( scratch => scratch.CapacityBytes ) + _transitionScratch.CapacityBytes;
		_nextProgressLogTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() + 10 * System.Diagnostics.Stopwatch.Frequency;
		Bounds = BBox.FromPositionAndSize( Vector3.Zero, Vector3.One * 1_000_000_000.0f );
	}

	public void QueueStaticSet( IEnumerable<Vector3Int> coordinates, int ruleVersion ) => UpdateDesiredSet( coordinates, ruleVersion );

	public bool QueueClipboxObserver( Vector3Int observerCanonicalSample )
	{
		if ( _clipboxPlanner is null ) throw new System.InvalidOperationException( "Regular clipbox residency was not enabled for this GPU backend." );
		if ( !_clipboxPlanner.Update( observerCanonicalSample ) )
		{
			_diagnostics.ClipboxStationaryUpdates++;
			_diagnostics.ClipboxTransitionStationaryUpdates++;
			_diagnostics.ClipboxRevision = _clipboxPlanner.Revision;
			return false;
		}
		_diagnostics.ClipboxRevision = _clipboxPlanner.Revision;
		_diagnostics.ClipboxChangedSlots = _clipboxPlanner.ChangedSlotCount;
		_diagnostics.ClipboxActiveSlotCount = _clipboxPlanner.ActiveRegularCount;
		_diagnostics.ClipboxTransitionChangedSlots = _clipboxPlanner.ChangedTransitionSlotCount;
		_diagnostics.ClipboxTransitionActiveSlotCount = _clipboxPlanner.ActiveTransitionCount;
		_clipboxCombinedKeySet.Clear();
		foreach ( var assignment in _clipboxPlanner.CurrentSlots ) if ( assignment.Active ) _clipboxCombinedKeySet.Add( assignment.Key );
		foreach ( var assignment in _clipboxPlanner.DesiredSlots ) if ( assignment.Active ) _clipboxCombinedKeySet.Add( assignment.Key );
		_clipboxCombinedKeyScratch.Clear();
		_clipboxCombinedKeyScratch.AddRange( _clipboxCombinedKeySet );
		var combinedDesiredCount = _clipboxCombinedKeySet.Count;
		var desiredCount = UpdateDesiredKeys( _clipboxCombinedKeyScratch );
		if ( desiredCount != combinedDesiredCount )
		{
			_diagnostics.ClipboxDroppedWork += combinedDesiredCount - desiredCount;
			_diagnostics.Failure = $"GPU regular clipbox admitted {desiredCount} of {combinedDesiredCount} staged slots.";
		}
		_clipboxTransitions.Update( _clipboxPlanner.DesiredTransitions, _residents, _scheduler );
		_diagnostics.ClipboxTransitionPendingSlots = _clipboxTransitions.PendingCount;
		_diagnostics.ClipboxTransitionDependencyMismatches = _clipboxTransitions.DependencyMismatchCount;
		if ( !_clipboxTransitions.DependenciesValid )
			_diagnostics.Failure = $"GPU transition metadata has {_clipboxTransitions.DependencyMismatchCount} unresolved regular generation dependencies.";
		UpdateDesiredTransitions( true );
		_clipboxRevisionPending = true;
		_diagnostics.ClipboxTransitionPendingSlots = _clipboxTransitions.PendingCount;
		Log.Info( $"Voxel GPU transition scheduling: active={_clipboxTransitions.ActiveCount}, desired={TransitionDesiredCount}, dependenciesValid={_clipboxTransitions.DependenciesValid}, dependencyMismatches={_clipboxTransitions.DependencyMismatchCount}, schedulerPending={_transitionScheduler.PendingCount}." );
		_clipboxRevisionPlannedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		RecordRevisionEvent( "planned" );
		return true;
	}

	private void UpdateDesiredTransitions( bool includeCurrentPlan )
	{
		if ( _clipboxTransitions is null ) return;
		lock ( _desiredSync )
		{
			_desiredTransitionScratch.Clear();
			_transitionRebuildKeys.Clear();
			foreach ( var entry in _clipboxTransitions.Desired )
			{
				if ( !entry.Active ) continue;
				var transitionKey = VoxelClipboxTransitionPlanner.GetVisualKey( _clipboxPlanner.DesiredTransitions[entry.StableSlotId] );
				_desiredTransitionScratch.Add( transitionKey );
				if ( _transitionDependencyKeys.TryGetValue( transitionKey, out var previousDependency ) && previousDependency != entry.Key )
				{
					_transitionScheduler.Cancel( transitionKey );
					_pendingTransitionRequests.Remove( transitionKey );
					_residents.CancelUnpublishedReservation( transitionKey );
					_transitionRebuildKeys.Add( transitionKey );
				}
				_transitionDependencyKeys[transitionKey] = entry.Key;
			}
			if ( includeCurrentPlan )
			foreach ( var assignment in _clipboxPlanner.CurrentTransitions )
			{
				if ( !assignment.Active ) continue;
				var transitionKey = VoxelClipboxTransitionPlanner.GetVisualKey( assignment );
				if ( _desiredTransitionScratch.Add( transitionKey ) ) _transitionDependencyKeys[transitionKey] = _clipboxTransitions.Current[assignment.StableSlotId].Key;
			}
			_transitionLeavingScratch.Clear();
			foreach ( var key in _desiredTransitionKeys ) if ( !_desiredTransitionScratch.Contains( key ) ) _transitionLeavingScratch.Add( key );
			foreach ( var key in _transitionLeavingScratch )
			{
				_transitionScheduler.Cancel( key );
				_pendingTransitionRequests.Remove( key );
				_blockedTransitionRequests.Remove( key );
				_blockedTransitionDetails.Remove( key );
				_residents.CancelUnpublishedReservation( key );
				_desiredTransitionKeys.Remove( key );
				_transitionDependencyKeys.Remove( key );
			}
			foreach ( var key in _desiredTransitionScratch )
			{
				_desiredTransitionKeys.Add( key );
				if ( (!_transitionRebuildKeys.Contains( key ) && _residents.ContainsKey( key )) || !_pendingTransitionRequests.Add( key ) ) continue;
				if ( !_transitionScheduler.TryEnqueue( key, out _ ) )
				{
					_pendingTransitionRequests.Remove( key );
					_blockedTransitionRequests.Add( key );
				}
			}
		}
	}

	public void SetRenderingEnabled( bool enabled ) => _renderer.SetRenderingEnabled( enabled );
	public void SetProcessingEnabled( bool enabled ) => _processingEnabled = enabled;

	public void UpdateDesiredSet( IEnumerable<Vector3Int> coordinates, int ruleVersion )
	{
		_desiredKeyInputScratch.Clear();
		foreach ( var coordinate in coordinates ) _desiredKeyInputScratch.Add( new VoxelVisualBlockKey( coordinate, 0, ruleVersion ) );
		UpdateDesiredKeys( _desiredKeyInputScratch );
	}

	private int UpdateDesiredKeys( IEnumerable<VoxelVisualBlockKey> keys )
	{
		var updateStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var desiredCount = 0;
		lock ( _desiredSync )
		{
			_desiredScratch.Clear();
			_desiredOrderScratch.Clear();
			foreach ( var key in keys )
			{
				if ( _desiredScratch.Count >= _residents.Capacity )
				{
					_diagnostics.BackpressureEvents++;
					break;
				}
				if ( _desiredScratch.Add( key ) ) _desiredOrderScratch.Add( key );
			}
			if ( _desiredOrderScratch.Count > 0 ) _diagnostics.RuleVersion = _desiredOrderScratch[0].RuleVersion;
			desiredCount = _desiredScratch.Count;

			_leavingScratch.Clear();
			foreach ( var key in _desiredKeys )
				if ( !_desiredScratch.Contains( key ) ) _leavingScratch.Add( key );
			foreach ( var key in _leavingScratch )
			{
				_scheduler.Cancel( key );
				_pendingRequests.Remove( key );
				_blockedRequests.Remove( key );
				_blockedDetails.Remove( key );
				_residents.CancelUnpublishedReservation( key );
				_desiredKeys.Remove( key );
			}
			if ( _leavingScratch.Count > 0 ) System.Threading.Interlocked.Exchange( ref _retireUndesiredRequested, 1 );
			_desiredOrder.Clear();
			_desiredOrder.AddRange( _desiredOrderScratch );
			_desiredRanks.Clear();
			for ( var index = 0; index < _desiredOrder.Count; index++ ) _desiredRanks[_desiredOrder[index]] = index;

			foreach ( var key in _desiredOrder )
			{
				if ( !_desiredKeys.Add( key ) ) continue;
				if ( _residents.ContainsKey( key ) || !_pendingRequests.Add( key ) ) continue;
				if ( !_scheduler.TryEnqueue( key, out _ ) )
				{
					_pendingRequests.Remove( key );
					_blockedRequests.Add( key );
					_diagnostics.BackpressureEvents++;
				}
			}
			_scheduler.PruneToDesired( _desiredKeys );
			UpdateQueueDiagnostics();
			UpdatePendingCountBatches();
			_settledLogged = false;
		}
		RecomputeWorstPublishedRank();
		var updateMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( updateStart ).TotalMilliseconds;
		if ( updateMilliseconds >= 8.0 && System.Threading.Interlocked.Increment( ref _slowDesiredSetLogCount ) <= 32 )
			Log.Info( $"Voxel GPU desired-set hitch trace: {updateMilliseconds:F2}ms, desired={desiredCount:N0}, pending={PendingRequestCount:N0}, scheduler={_scheduler.PendingCount:N0}." );
		return desiredCount;
	}

	public int CopyClipboxDebugBlocks( List<VoxelGpuClipboxDebugBlock> destination )
	{
		if ( destination is null ) throw new System.ArgumentNullException( nameof( destination ) );
		destination.Clear();
		if ( _clipboxPlanner is null ) return 0;
		foreach ( var assignment in _clipboxPlanner.DesiredSlots )
		{
			if ( !assignment.Active ) continue;
			var state = VoxelGpuDebugBlockState.Missing;
			var generation = 0u;
			var vertexOffset = 0;
			var vertexCapacity = 0;
			var indexOffset = 0;
			var indexCapacity = 0;
			var indexCount = 0u;
			var drawOrigin = Vector3.Zero;
			var boundsMin = Vector3.Zero;
			var boundsMax = Vector3.Zero;
			if ( _residents.TryGetPublished( assignment.Key, out var resident ) )
			{
				state = VoxelGpuDebugBlockState.Resident;
				generation = resident.Generation;
				vertexOffset = checked( (int)resident.Descriptor.VertexOffset );
				vertexCapacity = resident.Allocation.Vertices.Count;
				indexOffset = checked( (int)resident.Descriptor.IndexOffset );
				indexCapacity = resident.Allocation.Indices.Count;
				indexCount = resident.Descriptor.IndexCount;
				drawOrigin = new Vector3( resident.Descriptor.DrawOrigin.x, resident.Descriptor.DrawOrigin.y, resident.Descriptor.DrawOrigin.z );
				boundsMin = new Vector3( resident.Descriptor.BoundsMin.x, resident.Descriptor.BoundsMin.y, resident.Descriptor.BoundsMin.z );
				boundsMax = new Vector3( resident.Descriptor.BoundsMax.x, resident.Descriptor.BoundsMax.y, resident.Descriptor.BoundsMax.z );
			}
			else if ( IsPending( assignment.Key ) ) state = VoxelGpuDebugBlockState.Pending;
			else if ( IsBlocked( assignment.Key ) ) state = VoxelGpuDebugBlockState.Deferred;
			destination.Add( new VoxelGpuClipboxDebugBlock( assignment.Coordinate, assignment.Lod, assignment.StableSlotId, state, generation, vertexOffset, vertexCapacity, indexOffset, indexCapacity, indexCount, drawOrigin, boundsMin, boundsMax ) );
		}
		return destination.Count;
	}

	public int CopyClipboxDebugTransitions( List<VoxelGpuClipboxDebugTransition> destination )
	{
		if ( destination is null ) throw new System.ArgumentNullException( nameof( destination ) );
		destination.Clear();
		if ( _clipboxPlanner is null ) return 0;
		foreach ( var entry in _clipboxTransitions.Desired )
		{
			if ( !entry.Active ) continue;
			var vertexOffset = 0;
			var vertexCapacity = 0;
			var indexOffset = 0;
			var indexCapacity = 0;
			var indexCount = 0u;
			var transitionKey = VoxelClipboxTransitionPlanner.GetVisualKey( _clipboxPlanner.DesiredTransitions[entry.StableSlotId] );
			var published = _residents.TryGetPublished( transitionKey, out var resident );
			var state = !entry.DependenciesValid ? VoxelGpuDebugBlockState.Deferred : published ? VoxelGpuDebugBlockState.Resident : IsPending( transitionKey ) || entry.Pending ? VoxelGpuDebugBlockState.Pending : IsBlocked( transitionKey ) ? VoxelGpuDebugBlockState.Deferred : VoxelGpuDebugBlockState.Missing;
			if ( published )
			{
				vertexOffset = checked( (int)resident.Descriptor.VertexOffset );
				vertexCapacity = resident.Allocation.Vertices.Count;
				indexOffset = checked( (int)resident.Descriptor.IndexOffset );
				indexCapacity = resident.Allocation.Indices.Count;
				indexCount = resident.Descriptor.IndexCount;
			}
			destination.Add( new VoxelGpuClipboxDebugTransition( entry.FineCoordinate, entry.CoarseCoordinate, entry.FineLevel, entry.CoarseLevel, entry.Face, entry.StableSlotId, state, entry.Key.FineGeneration, entry.Key.CoarseGeneration, entry.DependenciesValid, vertexOffset, vertexCapacity, indexOffset, indexCapacity, indexCount ) );
		}
		return destination.Count;
	}

	private bool IsPending( VoxelVisualBlockKey key ) { lock ( _desiredSync ) return _pendingRequests.Contains( key ); }
	private bool IsBlocked( VoxelVisualBlockKey key ) { lock ( _desiredSync ) return _blockedRequests.Contains( key ); }

	public VoxelGpuTerrainDiagnostics CaptureDiagnostics()
	{
		UpdateQueueDiagnostics();
		return _diagnostics.Snapshot( _capabilities, _residents, _pool, _renderer );
	}

	private void RecordHitchTrace()
	{
		var frameMilliseconds = Sandbox.Diagnostics.PerformanceStats.FrameTime * 1000.0;
		if ( frameMilliseconds < 16.6667 ) return;
		if ( _hitchTrace.Count >= 32 ) _hitchTrace.RemoveAt( 0 );
		_hitchTrace.Add( new VoxelGpuHitchTrace(
			_frameCounter,
			frameMilliseconds,
			Sandbox.Diagnostics.PerformanceStats.GpuFrametime,
			_diagnostics.ClipboxRevision,
			_clipboxRevisionPending,
			_renderer.VisibleRegularCommandCount,
			_renderer.VisibleTransitionCommandCount,
			_diagnostics.PendingCountBatches,
			_diagnostics.PendingEmitBatches,
			(long)_pool.UsedVertices * 44 + (long)_pool.UsedIndices * sizeof( uint ),
			_pool.CapacityBytes,
			Sandbox.Diagnostics.PerformanceStats.GcPause,
			Sandbox.Diagnostics.PerformanceStats.BytesAllocated ) );
		if ( _revisionEventHistory.Count > 0 ) RefreshStructuredDebugReport();
	}

	private void RecordRevisionEvent( string state )
	{
		_latestRevisionEventState = state;
		_latestRevisionEventJson = BuildRevisionEventJson( state );
		if ( _revisionEventHistory.Count >= 64 ) _revisionEventHistory.RemoveAt( 0 );
		_revisionEventHistory.Add( _latestRevisionEventJson );
		RefreshStructuredDebugReport();
	}

	private string BuildRevisionEventJson( string state )
	{
		var builder = new System.Text.StringBuilder( 512 );
		AppendRevisionEventJson( builder, state );
		return builder.ToString();
	}

	private void RefreshStructuredDebugReport()
	{
		var builder = new System.Text.StringBuilder( 8192 );
		builder.Append( "{\"revision_event\":" ).Append( _latestRevisionEventJson );
		builder.Append( ",\"revision_history\":[" );
		for ( var eventIndex = 0; eventIndex < _revisionEventHistory.Count; eventIndex++ )
		{
			if ( eventIndex != 0 ) builder.Append( ',' );
			builder.Append( _revisionEventHistory[eventIndex] );
		}
		builder.Append( "]" );
		builder.Append( ",\"levels\":[" );
		if ( _clipboxPlanner is not null )
		{
			for ( var level = 0; level < _clipboxPlanner.DesiredLevels.Length; level++ )
			{
				if ( level != 0 ) builder.Append( ',' );
				AppendLevelDiagnosticsJson( builder, level );
			}
		}
		builder.Append( "],\"renderer\":{\"visible_regular_commands\":" );
		builder.Append( _renderer.VisibleRegularCommandCount );
		builder.Append( ",\"visible_transition_commands\":" ).Append( _renderer.VisibleTransitionCommandCount );
		builder.Append( ",\"culled_regular\":" ).Append( System.Math.Max( 0, _renderer.PublishedRenderableRegularCommandCount - _renderer.VisibleRegularCommandCount ) );
		builder.Append( ",\"culled_transition\":" ).Append( System.Math.Max( 0, _renderer.PublishedRenderableTransitionCommandCount - _renderer.VisibleTransitionCommandCount ) );
		builder.Append( ",\"active_command_lists_per_pass\":{\"depth\":" ).Append( _renderer.DepthPrepassCommandListCount ).Append( ",\"opaque\":" ).Append( _renderer.OpaqueCommandListCount ).Append( "}" );
		builder.Append( ",\"commands_per_submission\":" ).Append( _renderer.CommandsPerSubmission );
		builder.Append( ",\"argument_upload_bytes\":" ).Append( _renderer.LastArgumentUploadBytes );
		builder.Append( ",\"resident_upload_bytes\":0" );
		builder.Append( ",\"culling_cpu_ms\":" ).Append( Number( _renderer.LastCullingMilliseconds ) );
		builder.Append( ",\"argument_build_cpu_ms\":" ).Append( Number( _renderer.LastArgumentBuildMilliseconds ) );
		builder.Append( ",\"terrain_depth_gpu_ms\":null,\"terrain_opaque_gpu_ms\":null}" );
		builder.Append( ",\"transition\":{\"desired_seam_slots\":" ).Append( _clipboxTransitions?.ActiveCount ?? 0 );
		builder.Append( ",\"published_seam_slots\":" ).Append( _residents.PublishedCountFor( true ) );
		builder.Append( ",\"pending_seam_slots\":" ).Append( _clipboxTransitions?.PendingCount ?? 0 );
		builder.Append( ",\"transition_cells_classified\":null,\"active_transition_cells\":null" );
		builder.Append( ",\"vertices\":" ).Append( _diagnostics.TransitionAllocatedVertexCount );
		builder.Append( ",\"indices\":" ).Append( _diagnostics.TransitionAllocatedIndexCount );
		builder.Append( ",\"count_gpu_ms\":null,\"emit_gpu_ms\":null" );
		builder.Append( ",\"count_readback_latency_ms\":" ).Append( Number( _diagnostics.CountReadbackAverageMilliseconds ) );
		builder.Append( ",\"generation_mismatch_rejects\":" ).Append( _diagnostics.TransitionStaleSchedulerRejections + _diagnostics.TransitionStaleDependencyRejections );
		builder.Append( ",\"coherence_wait_ms\":" ).Append( _latestRevisionEventState == "committed" && _clipboxRevisionPlannedTimestamp != 0 ? Number( System.Diagnostics.Stopwatch.GetElapsedTime( _clipboxRevisionPlannedTimestamp ).TotalMilliseconds ) : "null" ).Append( "}" );
		builder.Append( ",\"memory\":{\"regular_scratch_bytes\":" ).Append( _scratchRing.Sum( scratch => scratch.CapacityBytes ) );
		builder.Append( ",\"transition_scratch_bytes\":" ).Append( _transitionScratch.CapacityBytes );
		builder.Append( ",\"vertex_pool_used_bytes\":" ).Append( (long)_pool.UsedVertices * 44 );
		builder.Append( ",\"vertex_pool_peak_bytes\":" ).Append( (long)_pool.PeakUsedVertices * 44 );
		builder.Append( ",\"index_pool_used_bytes\":" ).Append( (long)_pool.UsedIndices * sizeof( uint ) );
		builder.Append( ",\"index_pool_peak_bytes\":" ).Append( (long)_pool.PeakUsedIndices * sizeof( uint ) );
		builder.Append( ",\"pool_capacity_bytes\":" ).Append( _pool.CapacityBytes );
		builder.Append( ",\"current_regular_allocations\":" ).Append( _residents.PublishedCountFor( false ) );
		builder.Append( ",\"pending_regular_allocations\":" ).Append( PendingRequestCount );
		builder.Append( ",\"current_seam_allocations\":" ).Append( _residents.PublishedCountFor( true ) );
		builder.Append( ",\"pending_seam_allocations\":" ).Append( PendingTransitionRequestCount );
		builder.Append( ",\"retired_allocations\":" ).Append( _pool.DeferredAllocationCount );
		builder.Append( ",\"free_range_count\":{\"vertex\":" ).Append( _pool.VertexFreeRangeCount ).Append( ",\"index\":" ).Append( _pool.IndexFreeRangeCount ).Append( "}" );
		builder.Append( ",\"largest_free_range\":{\"vertex\":" ).Append( _pool.VertexLargestFree ).Append( ",\"index\":" ).Append( _pool.IndexLargestFree ).Append( "}" );
		builder.Append( ",\"managed_allocation_bytes_per_sec\":null}" );
		builder.Append( ",\"hitch_trace\":[" );
		for ( var index = 0; index < _hitchTrace.Count; index++ )
		{
			if ( index != 0 ) builder.Append( ',' );
			var hitch = _hitchTrace[index];
			builder.Append( "{\"frame\":" ).Append( hitch.Frame ).Append( ",\"frame_ms\":" ).Append( Number( hitch.FrameMilliseconds ) ).Append( ",\"gpu_ms\":" ).Append( Number( hitch.GpuMilliseconds ) );
			builder.Append( ",\"clipbox_revision\":" ).Append( hitch.ClipboxRevision ).Append( ",\"revision_pending\":" ).Append( hitch.RevisionPending.ToString().ToLowerInvariant() );
			builder.Append( ",\"visible_regular\":" ).Append( hitch.VisibleRegularCommands ).Append( ",\"visible_transition\":" ).Append( hitch.VisibleTransitionCommands );
			builder.Append( ",\"pending_count_batches\":" ).Append( hitch.PendingCountBatches ).Append( ",\"pending_emit_batches\":" ).Append( hitch.PendingEmitBatches );
			builder.Append( ",\"pool_used_bytes\":" ).Append( hitch.PoolUsedBytes ).Append( ",\"pool_capacity_bytes\":" ).Append( hitch.PoolCapacityBytes );
			builder.Append( ",\"gc_pause_ticks\":" ).Append( hitch.GcPauseTicks ).Append( ",\"allocated_bytes\":" ).Append( hitch.AllocatedBytes ).Append( "}" );
		}
		builder.Append( "]}" );
		_structuredDebugReportJson = builder.ToString();
		_diagnostics.StructuredDebugReportJson = _structuredDebugReportJson;
	}

	private void AppendRevisionEventJson( System.Text.StringBuilder builder, string state )
	{
		var revision = _clipboxPlanner?.Revision ?? _diagnostics.ClipboxRevision;
		var observer = _clipboxPlanner?.ObserverBaseBlock ?? Vector3Int.Zero;
		builder.Append( "{\"state\":\"" ).Append( state ).Append( "\",\"frame\":" ).Append( _frameCounter );
		builder.Append( ",\"revision\":" ).Append( revision );
		builder.Append( ",\"observer_base_block\":[" ).Append( observer.x ).Append( ',' ).Append( observer.y ).Append( ',' ).Append( observer.z ).Append( "]" );
		builder.Append( ",\"level_origins\":[" );
		if ( _clipboxPlanner is not null )
		{
			for ( var level = 0; level < _clipboxPlanner.DesiredLevels.Length; level++ )
			{
				if ( level != 0 ) builder.Append( ',' );
				var origin = _clipboxPlanner.DesiredLevels[level].Origin;
				builder.Append( '[' ).Append( origin.x ).Append( ',' ).Append( origin.y ).Append( ',' ).Append( origin.z ).Append( ']' );
			}
		}
		builder.Append( "]" );
		builder.Append( ",\"regular_active\":" ).Append( _clipboxPlanner?.ActiveRegularCount ?? 0 );
		builder.Append( ",\"regular_changed\":" ).Append( _diagnostics.ClipboxChangedSlots );
		builder.Append( ",\"regular_pending\":" ).Append( _diagnostics.ClipboxPendingRevisionCount );
		builder.Append( ",\"seam_active\":" ).Append( _clipboxPlanner?.ActiveTransitionCount ?? 0 );
		builder.Append( ",\"seam_changed\":" ).Append( _diagnostics.ClipboxTransitionChangedSlots );
		builder.Append( ",\"seam_pending\":" ).Append( _diagnostics.ClipboxTransitionPendingSlots );
		builder.Append( ",\"cancelled_stale\":" ).Append( _diagnostics.StalePublicationsRejected );
		builder.Append( ",\"deferred_budget\":" ).Append( _diagnostics.CapacityDeferrals );
		builder.Append( ",\"commit_latency_ms\":" ).Append( state == "committed" && _clipboxRevisionPlannedTimestamp != 0 ? Number( System.Diagnostics.Stopwatch.GetElapsedTime( _clipboxRevisionPlannedTimestamp ).TotalMilliseconds ) : "null" ).Append( '}' );
	}

	private void AppendLevelDiagnosticsJson( System.Text.StringBuilder builder, int level )
	{
		var desired = _clipboxPlanner.DesiredLevels[level];
		var currentSurface = 0;
		var currentEmpty = 0;
		var pending = 0;
		var entering = 0;
		var leaving = 0;
		long meshBytes = 0;
		var first = desired.StableSlotBase;
		var end = first + desired.StableSlotCount;
		for ( var slot = first; slot < end; slot++ )
		{
			var desiredAssignment = _clipboxPlanner.DesiredSlots[slot];
			var currentAssignment = _clipboxPlanner.CurrentSlots[slot];
			if ( desiredAssignment.Active && ( !currentAssignment.Active || currentAssignment.Key != desiredAssignment.Key ) ) entering++;
			if ( currentAssignment.Active && ( !desiredAssignment.Active || currentAssignment.Key != desiredAssignment.Key ) ) leaving++;
			if ( desiredAssignment.Active && !_residents.TryGetPublished( desiredAssignment.Key, out _ ) && ( IsPending( desiredAssignment.Key ) || IsBlocked( desiredAssignment.Key ) ) ) pending++;
			if ( !currentAssignment.Active || !_residents.TryGetPublished( currentAssignment.Key, out var resident ) ) continue;
			if ( resident.Descriptor.IndexCount == 0 ) currentEmpty++;
			else currentSurface++;
			meshBytes += (long)resident.Allocation.Vertices.Count * 44 + (long)resident.Allocation.Indices.Count * sizeof( uint );
		}
		var elapsedSeconds = System.Diagnostics.Stopwatch.GetElapsedTime( _createdTimestamp ).TotalSeconds;
		var updatesPerSecond = elapsedSeconds <= 0.0 ? 0.0 : _levelUpdateCount[level] / elapsedSeconds;
		var origin = desired.Origin;
		builder.Append( "{\"level\":" ).Append( level ).Append( ",\"origin\":[" ).Append( origin.x ).Append( ',' ).Append( origin.y ).Append( ',' ).Append( origin.z ).Append( "]" );
		builder.Append( ",\"sample_spacing\":" ).Append( desired.SampleSpacing ).Append( ",\"stable_slots\":" ).Append( desired.StableSlotCount ).Append( ",\"active_slots\":" ).Append( desired.ActiveCount );
		builder.Append( ",\"current_surface\":" ).Append( currentSurface ).Append( ",\"current_empty\":" ).Append( currentEmpty ).Append( ",\"current_solid\":null,\"current_solid_available\":false" );
		builder.Append( ",\"pending\":" ).Append( pending ).Append( ",\"entering\":" ).Append( entering ).Append( ",\"leaving\":" ).Append( leaving );
		builder.Append( ",\"count_batches\":" ).Append( _levelCountBatches[level] ).Append( ",\"emit_batches\":" ).Append( _levelEmitBatches[level] ).Append( ",\"mesh_bytes\":" ).Append( meshBytes );
		builder.Append( ",\"last_update_frame\":" ).Append( _levelLastUpdateFrame[level] ).Append( ",\"updates_per_second\":" ).Append( Number( updatesPerSecond ) ).Append( '}' );
	}

	private static string Number( double value ) => value.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture );

	public override void RenderSceneObject()
	{
		if ( _disposed || !_processingEnabled ) return;
		_frameCounter++;
		var renderStart = System.Diagnostics.Stopwatch.GetTimestamp();
		try
		{
			_epoch++;
			if ( System.Threading.Interlocked.Exchange( ref _retireUndesiredRequested, 0 ) != 0 ) RetireUndesiredResidents();
			var reclaimed = _pool.Reclaim( _epoch );
			if ( reclaimed > 0 ) { WakeBlockedRequests(); WakeBlockedTransitionRequests(); }
			UpdateQueueDiagnostics();
			PublishCompletedEmits();
			TryCommitClipboxRevision();
			for ( var index = 0; index < _scratchRing.Length; index++ ) ProcessCountReadback( index );
			ProcessTransitionCountReadback();
			SubmitCountBatches();
			SubmitTransitionCountBatch();
			LogProgress();
			var renderMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( renderStart ).TotalMilliseconds;
			RecordHitchTrace();
			if ( renderMilliseconds >= 8.0 && System.Threading.Interlocked.Increment( ref _slowRenderLogCount ) <= 32 )
				Log.Info( $"Voxel GPU terrain render tick: {renderMilliseconds:F2}ms, pendingCount={_scheduler.PendingCount}, pendingEmit={_publications.Count}, residents={_residents.PublishedCount}/{DesiredCount}." );
		}
		catch ( System.Exception exception )
		{
			_diagnostics.Failure = exception.Message;
			Log.Error( $"Voxel GPU terrain backend failed: {exception}" );
		}
	}

	private void LogProgress()
	{
		var now = System.Diagnostics.Stopwatch.GetTimestamp();
		if ( IsSettled )
		{
			if ( _settledLogged ) return;
			_settledLogged = true;
		}
		else if ( now < _nextProgressLogTimestamp )
		{
			return;
		}
		_nextProgressLogTimestamp = now + 10 * System.Diagnostics.Stopwatch.Frequency;
		var diagnostics = CaptureDiagnostics();
			Log.Info( $"Voxel GPU terrain progress: settled={IsSettled}, capacityLimited={diagnostics.CapacityLimited}, requested={diagnostics.RequestedBlocks:N0}, residents={diagnostics.ResidentBlocks:N0}, blocked={diagnostics.BlockedRequests:N0}, pendingCount={diagnostics.PendingCountBatches:N0}, pendingEmit={diagnostics.PendingEmitBatches:N0}, readbacks={diagnostics.CountReadbackCount:N0}, visibleDraws={diagnostics.VisibleDrawCommands:N0}, transitionResidents/renderable/visible={diagnostics.TransitionResidentBlocks:N0}/{diagnostics.TransitionRenderableResidents:N0}/{diagnostics.TransitionVisibleDrawCommands:N0}, transitionPending/blocked={diagnostics.TransitionPendingRequests:N0}/{diagnostics.TransitionBlockedRequests:N0}, transitionAllocBytes={diagnostics.TransitionAllocatedBytes:N0}, transitionStaleScheduler/dependency={diagnostics.TransitionStaleSchedulerRejections:N0}/{diagnostics.TransitionStaleDependencyRejections:N0}, cullingRebuilds={_renderer.CullingRebuildCount:N0}, argumentUploads={_renderer.ArgumentUploadCount:N0}, poolUsed={diagnostics.PoolUsedBytes:N0}/{diagnostics.PoolCapacityBytes:N0}B, vertexFree/largest={diagnostics.VertexFree:N0}/{diagnostics.VertexLargestFree:N0}, indexFree/largest={diagnostics.IndexFree:N0}/{diagnostics.IndexLargestFree:N0}, backpressure={diagnostics.BackpressureEvents:N0}, allocationFailures={diagnostics.AllocationFailures:N0}, deferrals={diagnostics.CapacityDeferrals:N0}, failure={diagnostics.Failure}." );
	}

	private void TryCommitClipboxRevision()
	{
		if ( !_clipboxRevisionPending || _clipboxPlanner is null ) return;
		foreach ( var assignment in _clipboxPlanner.DesiredSlots )
			if ( assignment.Active && !_residents.TryGetPublished( assignment.Key, out _ ) ) return;
		foreach ( var assignment in _clipboxPlanner.DesiredTransitions )
		{
			if ( !assignment.Active ) continue;
			var metadata = _clipboxTransitions.Desired[assignment.StableSlotId];
			var transitionVisualKey = VoxelClipboxTransitionPlanner.GetVisualKey( assignment );
			if ( !metadata.DependenciesValid || !_residents.TryGetPublished( transitionVisualKey, out _ ) || !_publishedTransitionDependencies.TryGetValue( transitionVisualKey, out var publishedDependency ) || publishedDependency != metadata.Key ) return;
		}

		_clipboxPlanner.Commit();
		_clipboxTransitions.Commit();
		_clipboxRevisionPending = false;
		for ( var level = 0; level < _levelUpdateCount.Length; level++ )
		{
			_levelUpdateCount[level]++;
			_levelLastUpdateFrame[level] = _frameCounter;
		}
		_clipboxKeyScratch.Clear();
		foreach ( var assignment in _clipboxPlanner.DesiredSlots ) if ( assignment.Active ) _clipboxKeyScratch.Add( assignment.Key );
		UpdateDesiredKeys( _clipboxKeyScratch );
		UpdateDesiredTransitions( false );
		_diagnostics.ClipboxPendingRevisionCount = 0;
		_diagnostics.ClipboxTransitionPendingSlots = _clipboxTransitions.PendingCount;
		_diagnostics.ClipboxTransitionDependencyMismatches = _clipboxTransitions.DependencyMismatchCount;
		RecordRevisionEvent( "committed" );
		Log.Info( $"Voxel GPU clipbox revision committed: revision={_clipboxPlanner.Revision}, frame={_frameCounter}, observer={_clipboxPlanner.ObserverBaseBlock}, regular={_clipboxPlanner.ActiveRegularCount}/{_clipboxPlanner.ChangedSlotCount}, seam={_clipboxPlanner.ActiveTransitionCount}/{_clipboxPlanner.ChangedTransitionSlotCount}, latencyMs={System.Diagnostics.Stopwatch.GetElapsedTime( _clipboxRevisionPlannedTimestamp ).TotalMilliseconds:F2}, stale={_diagnostics.StalePublicationsRejected}, deferred={_diagnostics.CapacityDeferrals}." );
	}

	private void SubmitCountBatches()
	{
		for ( var arenaIndex = 0; arenaIndex < _scratchRing.Length && _scheduler.PendingCount > 0; arenaIndex++ )
		{
			var batch = _activeBatches[arenaIndex];
			if ( batch is null )
			{
				batch = new BatchContext( _scheduledScratch[arenaIndex], _slotScratch[arenaIndex] );
				_activeBatches[arenaIndex] = batch;
			}
			if ( batch.Count != 0 || !_scratchRing[arenaIndex].IsIdle ) continue;
			var batchCapacity = System.Math.Min( VoxelGpuScratchArena.MaximumBatchSize, batch.Requests.Length );
			var scheduledCount = _scheduler.TakeBatch( batchCapacity, batch.Requests );
			var requests = _requestScratch[arenaIndex];
			var slots = batch.Slots;
			for ( var index = 0; index < scheduledCount; index++ )
			{
				var item = batch.Requests[index];
				if ( !_residents.TryReserve( item.Key, item.Generation, out var slot ) )
				{
					_diagnostics.BackpressureEvents++;
					throw new System.InvalidOperationException( $"resident table exhausted while reserving {item.Key}" );
				}
				slots[index] = slot;
				var sampleScale = 1 << item.Key.Lod;
				var sampleOrigin = new Vector3( item.Key.Coordinate.x * _chunkSize * sampleScale, item.Key.Coordinate.y * _chunkSize * sampleScale, (item.Key.Coordinate.z - 1) * _chunkSize * sampleScale );
				requests[index] = new VoxelGpuBlockRequest
				{
					SampleOrigin = new Vector4( sampleOrigin, 0.0f ),
					SampleScale = new Vector4( sampleScale, sampleScale, sampleScale, 0.0f ),
					CoordinateX = item.Key.Coordinate.x,
					CoordinateY = item.Key.Coordinate.y,
					CoordinateZ = item.Key.Coordinate.z,
					Lod = item.Key.Lod,
					RuleVersion = (uint)item.Key.RuleVersion,
					Generation = item.Generation,
					ResidentSlot = (uint)slot
				};
			}

			batch.Count = scheduledCount;
			var countLevelMask = 0;
			for ( var index = 0; index < scheduledCount; index++ )
				if ( (uint)batch.Requests[index].Key.Lod < (uint)_levelCountBatches.Length ) countLevelMask |= 1 << batch.Requests[index].Key.Lod;
			if ( !_scratchRing[arenaIndex].TrySubmitCount( requests, scheduledCount, out var submissionMilliseconds ) )
			{
				batch.Count = 0;
				throw new System.InvalidOperationException( $"scratch arena {arenaIndex} rejected a count batch while idle" );
			}
			_diagnostics.CountSubmissionMilliseconds += submissionMilliseconds;
			for ( var level = 0; level < _levelCountBatches.Length; level++ )
				if ( (countLevelMask & (1 << level)) != 0 ) _levelCountBatches[level]++;
		}
		UpdatePendingCountBatches();
	}

	private void SubmitTransitionCountBatch()
	{
		if ( _transitionBatch.Count != 0 || !_transitionScratch.IsIdle || _transitionScheduler.PendingCount == 0 ) return;
		var scheduledCount = _transitionScheduler.TakeBatch( VoxelGpuTransitionScratchArena.MaximumBatchSize, _transitionBatch.Requests );
		var acceptedCount = 0;
		for ( var index = 0; index < scheduledCount; index++ )
		{
			var item = _transitionBatch.Requests[index];
			if ( !item.Key.IsTransition || item.Key.TransitionSlotId < 0 || item.Key.TransitionSlotId >= _clipboxTransitions.Capacity || !_residents.TryReserve( item.Key, item.Generation, out var slot ) )
			{
				_diagnostics.BackpressureEvents++;
				continue;
			}
			var entry = _clipboxTransitions.Desired[item.Key.TransitionSlotId];
			var fineStep = 1 << entry.FineLevel;
			var coarseStep = 1 << entry.CoarseLevel;
			_transitionBatch.Requests[acceptedCount] = item;
			_transitionSlotScratch[acceptedCount] = slot;
			_transitionRequestScratch[acceptedCount] = new VoxelGpuTransitionRequest
			{
				FineOrigin = new Vector4( entry.FineCoordinate.x * _chunkSize * fineStep, entry.FineCoordinate.y * _chunkSize * fineStep, (entry.FineCoordinate.z - 1) * _chunkSize * fineStep, 0.0f ),
				CoarseOrigin = new Vector4( entry.CoarseCoordinate.x * _chunkSize * coarseStep, entry.CoarseCoordinate.y * _chunkSize * coarseStep, (entry.CoarseCoordinate.z - 1) * _chunkSize * coarseStep, 0.0f ),
				FineStep = (uint)fineStep,
				CoarseStep = (uint)coarseStep,
				Face = (uint)entry.Face,
				Generation = item.Generation,
				RequestIndex = (uint)acceptedCount,
				ResidentSlot = (uint)slot,
				TransitionSlot = (uint)item.Key.TransitionSlotId
			};
			acceptedCount++;
		}
		_transitionBatch.Count = acceptedCount;
		if ( acceptedCount == 0 ) return;
		if ( !_transitionScratch.TrySubmitCount( _transitionRequestScratch, acceptedCount, out var submissionMilliseconds ) ) throw new System.InvalidOperationException( "transition scratch arena rejected a count batch while idle" );
		_diagnostics.CountSubmissionMilliseconds += submissionMilliseconds;
	}

	private void ProcessTransitionCountReadback()
	{
		if ( _transitionBatch.Count == 0 || !_transitionScratch.TryTakeCounts( out var counts, out var count, out var readbackMilliseconds ) ) return;
		_diagnostics.RecordCountReadback( readbackMilliseconds );
		var allocations = _transitionAllocationScratch;
		var pending = _transitionPendingScratch;
		var pendingCount = 0;
		System.Array.Clear( allocations, 0, allocations.Length );
		var resultCount = System.Math.Min( count, _transitionBatch.Count );
		for ( var index = 0; index < resultCount; index++ )
		{
			var scheduled = _transitionBatch.Requests[index];
			var countResult = counts[index];
			var dependencyValid = scheduled.Key.TransitionSlotId >= 0 && scheduled.Key.TransitionSlotId < _clipboxTransitions.Capacity && _clipboxTransitions.Desired[scheduled.Key.TransitionSlotId].DependenciesValid;
			if ( countResult.RequestIndex != index || countResult.Generation != scheduled.Generation || !_transitionScheduler.IsCurrent( scheduled.Key, scheduled.Generation ) || !dependencyValid )
			{
				if ( dependencyValid ) RetryTransitionRequest( scheduled.Key );
				else
				{
					RemovePendingTransitionRequest( scheduled.Key );
					_residents.CancelUnpublishedReservation( scheduled.Key );
				}
				_diagnostics.StalePublicationsRejected++;
				continue;
			}
			if ( countResult.VertexCount == 0 || countResult.IndexCount == 0 )
			{
				RemovePendingTransitionRequest( scheduled.Key );
				_residents.TryPublish( _transitionBatch.Slots[index], scheduled.Key, scheduled.Generation, default, new VoxelGpuResidentDescriptor { Generation = scheduled.Generation }, out _ );
				_publishedTransitionDependencies[scheduled.Key] = _clipboxTransitions.Desired[scheduled.Key.TransitionSlotId].Key;
				continue;
			}
			if ( countResult.Overflow != 0 || countResult.VertexCount > int.MaxValue || countResult.IndexCount > int.MaxValue )
			{
				RemovePendingTransitionRequest( scheduled.Key );
				_residents.CancelUnpublishedReservation( scheduled.Key );
				_diagnostics.Failure = $"GPU transition count overflow for slot {scheduled.Key.TransitionSlotId}";
				_transitionScheduler.Cancel( scheduled.Key );
				continue;
			}
			if ( !_pool.TryAllocate( (int)countResult.VertexCount, (int)countResult.IndexCount, countResult.Generation, out var handle, out var allocationFailure ) )
			{
				RemovePendingTransitionRequest( scheduled.Key );
				_residents.CancelUnpublishedReservation( scheduled.Key );
				_diagnostics.BackpressureEvents++;
				_diagnostics.CapacityDeferrals++;
				if ( allocationFailure is VoxelGpuAllocationFailureReason.OversizedVertex or VoxelGpuAllocationFailureReason.OversizedIndex ) _diagnostics.Failure = $"GPU transition slot {scheduled.Key.TransitionSlotId} exceeds the persistent mesh pool";
				else BlockTransitionRequest( scheduled.Key, (int)countResult.VertexCount, (int)countResult.IndexCount );
				continue;
			}
			var entry = _clipboxTransitions.Desired[scheduled.Key.TransitionSlotId];
			var scale = 1 << entry.FineLevel;
			var drawOrigin = new Vector3( entry.FineCoordinate.x * _chunkSize * scale * _voxelSize, entry.FineCoordinate.y * _chunkSize * scale * _voxelSize, (entry.FineCoordinate.z - 1) * _chunkSize * scale * _voxelSize );
			var extent = _chunkSize * scale * _voxelSize;
			allocations[index] = new VoxelGpuAllocationDescriptor
			{
				VertexOffset = (uint)handle.Vertices.Offset,
				VertexCapacity = (uint)handle.Vertices.Count,
				IndexOffset = (uint)handle.Indices.Offset,
				IndexCapacity = (uint)handle.Indices.Count,
				Generation = scheduled.Generation,
				ResidentSlot = (uint)_transitionBatch.Slots[index],
				RequestIndex = (uint)index,
				Flags = 2,
				DrawOrigin = new Vector4( drawOrigin, 0.0f ),
				DrawScale = new Vector4( 1, 1, 1, 0 )
			};
			var descriptor = new VoxelGpuResidentDescriptor
			{
				DrawOrigin = new Vector4( drawOrigin, 0.0f ),
				BoundsMin = new Vector4( drawOrigin - Vector3.One * _voxelSize * scale * 2.0f, 0.0f ),
				BoundsMax = new Vector4( drawOrigin + Vector3.One * extent + Vector3.One * _voxelSize * scale * 2.0f, 0.0f ),
				Generation = scheduled.Generation,
				VertexOffset = (uint)handle.Vertices.Offset,
				IndexOffset = (uint)handle.Indices.Offset,
				IndexCount = countResult.IndexCount
			};
			pending[pendingCount++] = new PendingResident( _transitionBatch.Slots[index], scheduled.Key, scheduled.Generation, scheduled.RequestedTimestamp, handle, descriptor, entry.Key );
		}
		if ( !_transitionScratch.TrySubmitEmit( allocations, _transitionBatch.Count, _pool, out var emitMilliseconds ) )
		{
			for ( var index = 0; index < pendingCount; index++ ) _pool.ReleaseImmediately( pending[index].Allocation );
			throw new System.InvalidOperationException( "transition scratch arena rejected an emit batch after count publication" );
		}
		_diagnostics.EmitSubmissionMilliseconds += emitMilliseconds;
		_diagnostics.PendingEmitBatches++;
		var publicationResidents = _transitionPublicationScratch;
		System.Array.Copy( pending, publicationResidents, pendingCount );
		var requestedTimestamp = _transitionBatch.Requests[0].RequestedTimestamp;
		for ( var index = 1; index < _transitionBatch.Count; index++ ) requestedTimestamp = System.Math.Min( requestedTimestamp, _transitionBatch.Requests[index].RequestedTimestamp );
		_publications.Enqueue( new PendingPublication( _epoch + 1, requestedTimestamp, publicationResidents, pendingCount ) );
		_transitionBatch.Count = 0;
		UpdatePendingCountBatches();
	}

	private void ProcessCountReadback( int arenaIndex )
	{
		var activeBatch = _activeBatches[arenaIndex];
		var scratch = _scratchRing[arenaIndex];
		if ( activeBatch is null || activeBatch.Count == 0 || !scratch.TryTakeCounts( out var counts, out var count, out var readbackMilliseconds ) ) return;
		_diagnostics.RecordCountReadback( readbackMilliseconds );
		var allocations = _allocationScratch[arenaIndex];
		var pending = _pendingScratch[arenaIndex];
		var pendingCount = 0;
		var batchCount = activeBatch.Count;
		System.Array.Clear( allocations, 0, allocations.Length );
		var resultCount = System.Math.Min( count, batchCount );
		for ( var index = 0; index < resultCount; index++ )
		{
			var scheduled = activeBatch.Requests[index];
			var countResult = counts[index];
			if ( countResult.RequestIndex != index || countResult.Generation != scheduled.Generation || !_scheduler.IsCurrent( scheduled.Key, scheduled.Generation ) )
			{
				if ( !IsDesired( scheduled.Key ) )
				{
					RemovePendingRequest( scheduled.Key );
					_residents.CancelUnpublishedReservation( scheduled.Key );
				}
				_diagnostics.StalePublicationsRejected++;
				continue;
			}
			if ( countResult.Overflow != 0 || countResult.VertexCount > int.MaxValue || countResult.IndexCount > int.MaxValue )
			{
				RemovePendingRequest( scheduled.Key );
				_residents.CancelUnpublishedReservation( scheduled.Key );
				_diagnostics.Failure = $"GPU terrain count overflow for {scheduled.Key.Coordinate}";
				_scheduler.Cancel( scheduled.Key );
				continue;
			}
			if ( !_pool.TryAllocate( (int)countResult.VertexCount, (int)countResult.IndexCount, countResult.Generation, out var handle, out var allocationFailure ) )
			{
				RemovePendingRequest( scheduled.Key );
				_residents.CancelUnpublishedReservation( scheduled.Key );
				_diagnostics.BackpressureEvents++;
				_diagnostics.CapacityDeferrals++;
				if ( allocationFailure is VoxelGpuAllocationFailureReason.OversizedVertex or VoxelGpuAllocationFailureReason.OversizedIndex )
				{
					_diagnostics.Failure = $"GPU terrain block {scheduled.Key.Coordinate} exceeds the persistent mesh pool";
					_scheduler.Cancel( scheduled.Key );
				}
				else
				{
					EvictForCapacity( scheduled.Key, (int)countResult.VertexCount, (int)countResult.IndexCount );
					BlockRequest( scheduled.Key, (int)countResult.VertexCount, (int)countResult.IndexCount );
				}
				continue;
			}

			var drawScale = 1 << scheduled.Key.Lod;
			var drawOrigin = new Vector3(
				scheduled.Key.Coordinate.x * _chunkSize * drawScale * _voxelSize,
				scheduled.Key.Coordinate.y * _chunkSize * drawScale * _voxelSize,
				(scheduled.Key.Coordinate.z - 1) * _chunkSize * drawScale * _voxelSize );
			allocations[index] = new VoxelGpuAllocationDescriptor
			{
				VertexOffset = (uint)handle.Vertices.Offset,
				VertexCapacity = (uint)handle.Vertices.Count,
				IndexOffset = (uint)handle.Indices.Offset,
				IndexCapacity = (uint)handle.Indices.Count,
				Generation = scheduled.Generation,
				ResidentSlot = (uint)activeBatch.Slots[index],
				RequestIndex = (uint)index,
				Flags = 1,
				DrawOrigin = new Vector4( drawOrigin, 0.0f ),
				DrawScale = new Vector4( drawScale, drawScale, drawScale, 0.0f )
			};
			var extent = _chunkSize * drawScale * _voxelSize;
			var descriptor = new VoxelGpuResidentDescriptor
			{
				DrawOrigin = new Vector4( drawOrigin, 0.0f ),
				BoundsMin = new Vector4( drawOrigin - Vector3.One * _voxelSize * drawScale * 4.0f, 0.0f ),
				BoundsMax = new Vector4( drawOrigin + new Vector3( extent, extent, extent ) + Vector3.One * _voxelSize * drawScale * 4.0f, 0.0f ),
				Generation = scheduled.Generation,
				VertexOffset = (uint)handle.Vertices.Offset,
				IndexOffset = (uint)handle.Indices.Offset,
				IndexCount = countResult.IndexCount
			};
			pending[pendingCount++] = new PendingResident( activeBatch.Slots[index], scheduled.Key, scheduled.Generation, scheduled.RequestedTimestamp, handle, descriptor );
		}

		if ( !scratch.TrySubmitEmit( allocations, batchCount, _pool, out var emitMilliseconds ) )
		{
			for ( var index = 0; index < pendingCount; index++ ) _pool.ReleaseImmediately( pending[index].Allocation );
			throw new System.InvalidOperationException( "scratch arena rejected an emit batch after count publication" );
		}
		_diagnostics.EmitSubmissionMilliseconds += emitMilliseconds;
		_diagnostics.PendingEmitBatches++;
		var emitLevelMask = 0;
		for ( var index = 0; index < batchCount; index++ )
			if ( (uint)activeBatch.Requests[index].Key.Lod < (uint)_levelEmitBatches.Length ) emitLevelMask |= 1 << activeBatch.Requests[index].Key.Lod;
		for ( var level = 0; level < _levelEmitBatches.Length; level++ )
			if ( (emitLevelMask & (1 << level)) != 0 ) _levelEmitBatches[level]++;
		var publicationResidents = _publicationScratch[arenaIndex];
		System.Array.Copy( pending, publicationResidents, pendingCount );
		var requestedTimestamp = activeBatch.Requests[0].RequestedTimestamp;
		for ( var index = 1; index < batchCount; index++ ) requestedTimestamp = System.Math.Min( requestedTimestamp, activeBatch.Requests[index].RequestedTimestamp );
		_publications.Enqueue( new PendingPublication( _epoch + 1, requestedTimestamp, publicationResidents, pendingCount ) );
		activeBatch.Count = 0;
		UpdatePendingCountBatches();
	}

	private void UpdatePendingCountBatches()
	{
		_diagnostics.PendingCountBatches = _activeBatches.Count( batch => batch is not null && batch.Count != 0 ) +
			(_scheduler.PendingCount + VoxelGpuScratchArena.MaximumBatchSize - 1) / VoxelGpuScratchArena.MaximumBatchSize +
			(_transitionScheduler.PendingCount + VoxelGpuTransitionScratchArena.MaximumBatchSize - 1) / VoxelGpuTransitionScratchArena.MaximumBatchSize +
			(_transitionBatch.Count == 0 ? 0 : 1);
	}

	private void PublishCompletedEmits()
	{
		while ( _publications.TryPeek( out var publication ) && publication.PublishEpoch <= _epoch )
		{
			_publications.Dequeue();
			for ( var residentIndex = 0; residentIndex < publication.Count; residentIndex++ )
			{
				var resident = publication.Residents[residentIndex];
				var schedulerCurrent = resident.Key.IsTransition ? _transitionScheduler.IsCurrent( resident.Key, resident.Generation ) : _scheduler.IsCurrent( resident.Key, resident.Generation );
				var dependencyCurrent = !resident.Key.IsTransition || (resident.Key.TransitionSlotId >= 0 && resident.Key.TransitionSlotId < _clipboxTransitions.Capacity && _clipboxTransitions.Desired[resident.Key.TransitionSlotId].Key == resident.TransitionDependency);
				if ( resident.Key.IsTransition )
				{
					if ( !schedulerCurrent ) _diagnostics.TransitionStaleSchedulerRejections++;
					if ( !dependencyCurrent ) _diagnostics.TransitionStaleDependencyRejections++;
				}
				var current = schedulerCurrent && dependencyCurrent;
				if ( !current ||
					!_residents.TryPublish( resident.Slot, resident.Key, resident.Generation, resident.Allocation, resident.Descriptor, out var replaced ) )
				{
					if ( resident.Key.IsTransition ) RetryTransitionRequest( resident.Key );
					else if ( !IsDesired( resident.Key ) )
					{
						RemovePendingRequest( resident.Key );
						_residents.CancelUnpublishedReservation( resident.Key );
					}
					_diagnostics.StalePublicationsRejected++;
					_pool.Retire( resident.Allocation, _epoch + VoxelGpuCapabilities.RetirementEpochs );
					continue;
				}
				if ( !replaced.IsEmpty ) _pool.Retire( replaced, _epoch + VoxelGpuCapabilities.RetirementEpochs );
				if ( resident.Key.IsTransition ) _publishedTransitionDependencies[resident.Key] = resident.TransitionDependency;
				if ( resident.Key.IsTransition ) RemovePendingTransitionRequest( resident.Key );
				else RemovePendingRequest( resident.Key );
				UpdateWorstPublishedRank( resident.Key );
				_diagnostics.RecordRequestToVisible( System.Diagnostics.Stopwatch.GetElapsedTime( resident.RequestedTimestamp ).TotalMilliseconds );
			}
			_diagnostics.RecordBatchCompletion( System.Diagnostics.Stopwatch.GetElapsedTime( publication.RequestedTimestamp ).TotalMilliseconds );
			_diagnostics.PendingEmitBatches--;
			_renderer.MarkDirty();
		}
	}

	private void UpdateQueueDiagnostics()
	{
		_diagnostics.DesiredBlocks = DesiredCount;
		_diagnostics.RequestedBlocks = DesiredCount;
		_diagnostics.PendingRequestCount = _scheduler.PendingCount;
		_diagnostics.TransitionPendingRequests = _transitionScheduler.PendingCount + PendingTransitionRequestCount;
		_diagnostics.TransitionBlockedRequests = BlockedTransitionRequestCount;
		_diagnostics.PendingPublicationCount = _publications.Count;
		_diagnostics.BlockedRequests = BlockedRequestCount;
		_diagnostics.CapacityLimited = BlockedRequestCount > 0;
		_diagnostics.QueuesBounded = _scheduler.PendingCount <= _scheduler.MaximumPendingRequests && _transitionScheduler.PendingCount <= _transitionScheduler.MaximumPendingRequests && _publications.Count <= VoxelGpuScratchArena.RingSize + 1;
		if ( _clipboxPlanner is not null )
		{
			_diagnostics.ClipboxPendingRevisionCount = _clipboxRevisionPending ? 1 : 0;
			_diagnostics.ClipboxMaximumPendingRevisionCount = System.Math.Max( _diagnostics.ClipboxMaximumPendingRevisionCount, _diagnostics.ClipboxPendingRevisionCount );
		}
	}

	private void RetireUndesiredResidents()
	{
		var retired = 0;
		var publishedCount = _residents.CopyPublishedEntries( _publishedScratch );
		for ( var index = 0; index < publishedCount; index++ )
		{
			var entry = _publishedScratch[index];
			if ( IsDesired( entry.Key ) ) continue;
			if ( !_residents.TryRemove( entry.Key, out var allocation ) ) continue;
			if ( entry.Key.IsTransition ) _publishedTransitionDependencies.Remove( entry.Key );
			if ( !allocation.IsEmpty ) _pool.Retire( allocation, _epoch + VoxelGpuCapabilities.RetirementEpochs );
			retired++;
		}
		if ( retired > 0 )
		{
			RecomputeWorstPublishedRank();
			_renderer.MarkDirty();
		}
	}

	private void RemovePendingRequest( VoxelVisualBlockKey key ) { lock ( _desiredSync ) _pendingRequests.Remove( key ); }
	private void RemovePendingTransitionRequest( VoxelVisualBlockKey key ) { lock ( _desiredSync ) _pendingTransitionRequests.Remove( key ); }

	private void RetryTransitionRequest( VoxelVisualBlockKey key )
	{
		lock ( _desiredSync )
		{
			_pendingTransitionRequests.Remove( key );
			_blockedTransitionRequests.Remove( key );
			_blockedTransitionDetails.Remove( key );
			_residents.CancelUnpublishedReservation( key );
			if ( !_desiredTransitionKeys.Contains( key ) ) return;
			_transitionScheduler.Cancel( key );
			if ( !_pendingTransitionRequests.Add( key ) ) return;
			if ( !_transitionScheduler.TryEnqueue( key, out _ ) )
			{
				_pendingTransitionRequests.Remove( key );
				_blockedTransitionRequests.Add( key );
			}
		}
	}

	private void BlockRequest( VoxelVisualBlockKey key, int vertexCount, int indexCount )
	{
		lock ( _desiredSync )
			if ( _desiredKeys.Contains( key ) )
			{
				_blockedRequests.Add( key );
				_blockedDetails[key] = new BlockedRequest( vertexCount, indexCount );
			}
	}

	private void BlockTransitionRequest( VoxelVisualBlockKey key, int vertexCount, int indexCount )
	{
		lock ( _desiredSync )
			if ( _desiredTransitionKeys.Contains( key ) )
			{
				_blockedTransitionRequests.Add( key );
				_blockedTransitionDetails[key] = new BlockedRequest( vertexCount, indexCount );
			}
	}

	private void WakeBlockedRequests()
	{
		lock ( _desiredSync )
		{
			if ( _blockedRequests.Count == 0 ) return;
			_wakeScratch.Clear();
			foreach ( var key in _desiredOrder )
			{
				if ( !_blockedRequests.Contains( key ) || _residents.ContainsKey( key ) || !_blockedDetails.TryGetValue( key, out var blocked ) ) continue;
				if ( _pool.VertexLargestFree < blocked.VertexCount || _pool.IndexLargestFree < blocked.IndexCount ) continue;
				_wakeScratch.Add( key );
				if ( _wakeScratch.Count >= VoxelGpuScratchArena.MaximumBatchSize ) break;
			}
			foreach ( var key in _wakeScratch )
			{
				if ( !_blockedRequests.Contains( key ) || !_blockedDetails.TryGetValue( key, out var blocked ) ) continue;
				_blockedRequests.Remove( key );
				if ( !_pendingRequests.Add( key ) ) continue;
				_blockedDetails.Remove( key );
				if ( !_scheduler.TryEnqueue( key, out _ ) )
				{
					_pendingRequests.Remove( key );
					_blockedRequests.Add( key );
					_blockedDetails[key] = blocked;
					break;
				}
			}
		}
	}

	private void WakeBlockedTransitionRequests()
	{
		lock ( _desiredSync )
		{
			if ( _blockedTransitionRequests.Count == 0 ) return;
			_transitionWakeScratch.Clear();
			foreach ( var key in _desiredTransitionKeys )
			{
				if ( !_blockedTransitionRequests.Contains( key ) || _residents.ContainsKey( key ) || !_blockedTransitionDetails.TryGetValue( key, out var blocked ) ) continue;
				if ( _pool.VertexLargestFree < blocked.VertexCount || _pool.IndexLargestFree < blocked.IndexCount ) continue;
				_transitionWakeScratch.Add( key );
				if ( _transitionWakeScratch.Count >= VoxelGpuTransitionScratchArena.MaximumBatchSize ) break;
			}
			foreach ( var key in _transitionWakeScratch )
			{
				if ( !_blockedTransitionDetails.TryGetValue( key, out var blocked ) || !_blockedTransitionRequests.Remove( key ) ) continue;
				_blockedTransitionDetails.Remove( key );
				if ( !_pendingTransitionRequests.Add( key ) ) continue;
				if ( !_transitionScheduler.TryEnqueue( key, out _ ) )
				{
					_pendingTransitionRequests.Remove( key );
					_blockedTransitionRequests.Add( key );
					_blockedTransitionDetails[key] = blocked;
				}
			}
		}
	}

	private void EvictForCapacity( VoxelVisualBlockKey requestedKey, int vertexCount, int indexCount )
	{
		var requestedRank = PriorityRank( requestedKey );
		if ( requestedRank >= System.Threading.Interlocked.CompareExchange( ref _worstPublishedRank, 0, 0 ) ) return;
		var availableVertices = _pool.VertexFree;
		var availableIndices = _pool.IndexFree;
		var publishedCount = _residents.CopyPublishedEntries( _publishedScratch );
		_evictionCandidates.Clear();
		lock ( _desiredSync )
		{
			for ( var index = 0; index < publishedCount; index++ )
			{
				var entry = _publishedScratch[index];
				if ( entry.Key == requestedKey || !_desiredRanks.TryGetValue( entry.Key, out var rank ) || rank <= requestedRank ) continue;
				_evictionCandidates.Add( new EvictionCandidate( entry, rank ) );
			}
		}
		_evictionCandidates.Sort( static ( left, right ) => right.Rank.CompareTo( left.Rank ) );
		var evicted = 0;
		foreach ( var candidate in _evictionCandidates )
		{
			if ( evicted > 0 && availableVertices >= vertexCount && availableIndices >= indexCount ) break;
			var entry = candidate.Entry;
			if ( PriorityRank( entry.Key ) <= requestedRank ) continue;
			if ( !_residents.TryRemove( entry.Key, out var allocation ) ) continue;
			if ( !allocation.IsEmpty )
			{
				_pool.Retire( allocation, _epoch + VoxelGpuCapabilities.RetirementEpochs );
				availableVertices += allocation.Vertices.Count;
				availableIndices += allocation.Indices.Count;
			}
			_diagnostics.CapacityEvictions++;
			evicted++;
		}
		if ( evicted == 0 ) return;
		RecomputeWorstPublishedRank();
		_renderer.MarkDirty();
	}

	private void UpdateWorstPublishedRank( VoxelVisualBlockKey key )
	{
		var rank = PriorityRank( key );
		var current = System.Threading.Interlocked.CompareExchange( ref _worstPublishedRank, 0, 0 );
		if ( rank > current ) System.Threading.Interlocked.Exchange( ref _worstPublishedRank, rank );
	}

	private void RecomputeWorstPublishedRank()
	{
		var publishedCount = _residents.CopyPublishedEntries( _publishedScratch );
		var worstRank = -1;
		lock ( _desiredSync )
		{
			for ( var index = 0; index < publishedCount; index++ )
				if ( _desiredRanks.TryGetValue( _publishedScratch[index].Key, out var rank ) && rank > worstRank ) worstRank = rank;
		}
		System.Threading.Interlocked.Exchange( ref _worstPublishedRank, worstRank );
	}

	private int PriorityRank( VoxelVisualBlockKey key )
	{
		lock ( _desiredSync ) return _desiredRanks.TryGetValue( key, out var rank ) ? rank : int.MaxValue;
	}

	private readonly record struct BlockedRequest( int VertexCount, int IndexCount );
	private readonly record struct EvictionCandidate( VoxelGpuResidentTable.ResidentEntry Entry, int Rank );

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		_renderer.Dispose();
		foreach ( var scratch in _scratchRing ) scratch.Dispose();
		_transitionScratch.Dispose();
		_pool.Dispose();
		_scheduler.Clear();
		_transitionScheduler.Clear();
		Delete();
	}

	private sealed class BatchContext
	{
		public VoxelGpuBatchScheduler.ScheduledRequest[] Requests { get; }
		public int[] Slots { get; }
		public int Count { get; set; }
		public BatchContext( VoxelGpuBatchScheduler.ScheduledRequest[] requests, int[] slots ) { Requests = requests; Slots = slots; }
	}
	private readonly record struct PendingResident( int Slot, VoxelVisualBlockKey Key, uint Generation, long RequestedTimestamp, VoxelGpuAllocationHandle Allocation, VoxelGpuResidentDescriptor Descriptor, VoxelGpuTransitionKey TransitionDependency = default );
	private readonly record struct PendingPublication( ulong PublishEpoch, long RequestedTimestamp, PendingResident[] Residents, int Count );
}
