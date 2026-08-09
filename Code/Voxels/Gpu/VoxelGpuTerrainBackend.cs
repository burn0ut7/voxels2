internal sealed class VoxelGpuTerrainBackend : SceneCustomObject, System.IDisposable
{
	private readonly VoxelGpuCapabilityReport _capabilities;
	private readonly VoxelGpuTerrainDiagnosticCounters _diagnostics = new();
	private readonly VoxelGpuBatchScheduler _scheduler;
	private readonly VoxelGpuScratchArena[] _scratchRing;
	private readonly VoxelGpuMeshPool _pool;
	private readonly VoxelGpuResidentTable _residents;
	private readonly VoxelGpuTerrainRenderer _renderer;
	private readonly Queue<PendingPublication> _publications = new();
	private readonly int _chunkSize;
	private readonly float _voxelSize;
	private readonly BatchContext[] _activeBatches;
	private readonly VoxelGpuBatchScheduler.ScheduledRequest[][] _scheduledScratch;
	private readonly VoxelGpuBlockRequest[][] _requestScratch;
	private readonly int[][] _slotScratch;
	private readonly VoxelGpuAllocationDescriptor[][] _allocationScratch;
	private readonly PendingResident[][] _pendingScratch;
	private readonly PendingResident[][] _publicationScratch;
	private readonly HashSet<VoxelVisualBlockKey> _desiredKeys = new();
	private readonly HashSet<VoxelVisualBlockKey> _desiredScratch = new();
	private readonly List<VoxelVisualBlockKey> _desiredOrder = new();
	private readonly List<VoxelVisualBlockKey> _desiredOrderScratch = new();
	private readonly Dictionary<VoxelVisualBlockKey, int> _desiredRanks = new();
	private readonly List<VoxelVisualBlockKey> _leavingScratch = new();
	private readonly HashSet<VoxelVisualBlockKey> _pendingRequests = new();
	private readonly HashSet<VoxelVisualBlockKey> _blockedRequests = new();
	private readonly Dictionary<VoxelVisualBlockKey, BlockedRequest> _blockedDetails = new();
	private readonly List<VoxelVisualBlockKey> _wakeScratch = new();
	private readonly VoxelGpuResidentTable.ResidentEntry[] _publishedScratch;
	private readonly List<EvictionCandidate> _evictionCandidates;
	private readonly object _desiredSync = new();
	private ulong _epoch;
	private long _nextProgressLogTimestamp;
	private bool _settledLogged;
	private bool _disposed;
	private int _slowRenderLogCount;
	private int _slowDesiredSetLogCount;
	private bool _processingEnabled = true;
	private int _retireUndesiredRequested;
	private int _worstPublishedRank = -1;

	public bool IsSettled => IsStreamingWorkIdle && (DesiredCount == _residents.PublishedCount || BlockedRequestCount >= System.Math.Max( 0, DesiredCount - _residents.PublishedCount ));
	public bool IsCapacityLimited => BlockedRequestCount > 0;
	private int DesiredCount { get { lock ( _desiredSync ) return _desiredKeys.Count; } }
	private int PendingRequestCount { get { lock ( _desiredSync ) return _pendingRequests.Count; } }
	private int BlockedRequestCount { get { lock ( _desiredSync ) return _blockedRequests.Count; } }
	private bool IsDesired( VoxelVisualBlockKey key ) { lock ( _desiredSync ) return _desiredKeys.Contains( key ); }
	private bool IsStreamingWorkIdle => _scheduler.PendingCount == 0 && PendingRequestCount == 0 && _activeBatches.All( batch => batch is null || batch.Count == 0 ) && _publications.Count == 0 && _scratchRing.All( scratch => scratch.IsIdle );
	public bool IsAvailable => _capabilities.Available;
	public bool IsTerrainRenderingEnabled => _renderer.IsTerrainRenderingEnabled;
	public bool IsProcessingEnabled => _processingEnabled;

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
		int indexCapacity )
		: base( world )
	{
		_capabilities = VoxelGpuCapabilities.Detect();
		if ( !_capabilities.Available ) throw new System.InvalidOperationException( _capabilities.Failure );
		VoxelGpuContractValidation.AssertLayouts();
		_chunkSize = chunkSize;
		_voxelSize = voxelSize;
		_scheduler = new VoxelGpuBatchScheduler( System.Math.Max( 1, residentCapacity * 2 ) );
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
		_pool = new VoxelGpuMeshPool( vertexCapacity, indexCapacity );
		_residents = new VoxelGpuResidentTable( residentCapacity );
		_publishedScratch = new VoxelGpuResidentTable.ResidentEntry[residentCapacity];
		_evictionCandidates = new List<EvictionCandidate>( residentCapacity );
		_diagnostics.ResidentCapacity = residentCapacity;
		_diagnostics.PendingRequestCapacity = _scheduler.MaximumPendingRequests;
		_renderer = new VoxelGpuTerrainRenderer( world, camera, _pool, _residents, _diagnostics, _capabilities.IndirectCommandGroupSize, System.Math.Max( 0, cullingPaddingChunks ) * chunkSize * voxelSize );
		_diagnostics.ScratchBytes = _scratchRing.Sum( scratch => scratch.CapacityBytes );
		_nextProgressLogTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() + 10 * System.Diagnostics.Stopwatch.Frequency;
		Bounds = BBox.FromPositionAndSize( Vector3.Zero, Vector3.One * 1_000_000_000.0f );
	}

	public void QueueStaticSet( IEnumerable<Vector3Int> coordinates, int ruleVersion ) => UpdateDesiredSet( coordinates, ruleVersion );

	public void SetRenderingEnabled( bool enabled ) => _renderer.SetRenderingEnabled( enabled );
	public void SetProcessingEnabled( bool enabled ) => _processingEnabled = enabled;

	public void UpdateDesiredSet( IEnumerable<Vector3Int> coordinates, int ruleVersion )
	{
		var updateStart = System.Diagnostics.Stopwatch.GetTimestamp();
		_diagnostics.RuleVersion = ruleVersion;
		var desiredCount = 0;
		lock ( _desiredSync )
		{
			_desiredScratch.Clear();
			_desiredOrderScratch.Clear();
			foreach ( var coordinate in coordinates )
			{
				if ( _desiredScratch.Count >= _residents.Capacity )
				{
					_diagnostics.BackpressureEvents++;
					break;
				}
				var key = new VoxelVisualBlockKey( coordinate, 0, ruleVersion );
				if ( _desiredScratch.Add( key ) ) _desiredOrderScratch.Add( key );
			}
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
			UpdateQueueDiagnostics();
			UpdatePendingCountBatches();
			_settledLogged = false;
		}
		RecomputeWorstPublishedRank();
		var updateMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( updateStart ).TotalMilliseconds;
		if ( updateMilliseconds >= 8.0 && System.Threading.Interlocked.Increment( ref _slowDesiredSetLogCount ) <= 32 )
			Log.Info( $"Voxel GPU desired-set hitch trace: {updateMilliseconds:F2}ms, desired={desiredCount:N0}, pending={PendingRequestCount:N0}, scheduler={_scheduler.PendingCount:N0}." );
	}

	public VoxelGpuTerrainDiagnostics CaptureDiagnostics()
	{
		UpdateQueueDiagnostics();
		return _diagnostics.Snapshot( _capabilities, _residents, _pool, _renderer );
	}

	public override void RenderSceneObject()
	{
		if ( _disposed || !_processingEnabled ) return;
		var renderStart = System.Diagnostics.Stopwatch.GetTimestamp();
		try
		{
			_epoch++;
			if ( System.Threading.Interlocked.Exchange( ref _retireUndesiredRequested, 0 ) != 0 ) RetireUndesiredResidents();
			var reclaimed = _pool.Reclaim( _epoch );
			if ( reclaimed > 0 ) WakeBlockedRequests();
			UpdateQueueDiagnostics();
			PublishCompletedEmits();
			for ( var index = 0; index < _scratchRing.Length; index++ ) ProcessCountReadback( index );
			SubmitCountBatches();
			LogProgress();
			var renderMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( renderStart ).TotalMilliseconds;
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
			Log.Info( $"Voxel GPU terrain progress: settled={IsSettled}, capacityLimited={diagnostics.CapacityLimited}, requested={diagnostics.RequestedBlocks:N0}, residents={diagnostics.ResidentBlocks:N0}, blocked={diagnostics.BlockedRequests:N0}, pendingCount={diagnostics.PendingCountBatches:N0}, pendingEmit={diagnostics.PendingEmitBatches:N0}, readbacks={diagnostics.CountReadbackCount:N0}, visibleDraws={diagnostics.VisibleDrawCommands:N0}, cullingRebuilds={_renderer.CullingRebuildCount:N0}, argumentUploads={_renderer.ArgumentUploadCount:N0}, poolUsed={diagnostics.PoolUsedBytes:N0}/{diagnostics.PoolCapacityBytes:N0}B, vertexFree/largest={diagnostics.VertexFree:N0}/{diagnostics.VertexLargestFree:N0}, indexFree/largest={diagnostics.IndexFree:N0}/{diagnostics.IndexLargestFree:N0}, backpressure={diagnostics.BackpressureEvents:N0}, allocationFailures={diagnostics.AllocationFailures:N0}, deferrals={diagnostics.CapacityDeferrals:N0}, failure={diagnostics.Failure}." );
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
				var sampleOrigin = new Vector3( item.Key.Coordinate.x * _chunkSize, item.Key.Coordinate.y * _chunkSize, (item.Key.Coordinate.z - 1) * _chunkSize );
				requests[index] = new VoxelGpuBlockRequest
				{
					SampleOrigin = new Vector4( sampleOrigin, 0.0f ),
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
			if ( !_scratchRing[arenaIndex].TrySubmitCount( requests, scheduledCount, out var submissionMilliseconds ) )
			{
				batch.Count = 0;
				throw new System.InvalidOperationException( $"scratch arena {arenaIndex} rejected a count batch while idle" );
			}
			_diagnostics.CountSubmissionMilliseconds += submissionMilliseconds;
		}
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

			var drawOrigin = new Vector3(
				scheduled.Key.Coordinate.x * _chunkSize * _voxelSize,
				scheduled.Key.Coordinate.y * _chunkSize * _voxelSize,
				(scheduled.Key.Coordinate.z - 1) * _chunkSize * _voxelSize );
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
				DrawOrigin = new Vector4( drawOrigin, 0.0f )
			};
			var extent = _chunkSize * _voxelSize;
			var descriptor = new VoxelGpuResidentDescriptor
			{
				DrawOrigin = new Vector4( drawOrigin, 0.0f ),
				BoundsMin = new Vector4( drawOrigin - Vector3.One * _voxelSize * 4.0f, 0.0f ),
				BoundsMax = new Vector4( drawOrigin + new Vector3( extent, extent, extent ) + Vector3.One * _voxelSize * 4.0f, 0.0f ),
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
			(_scheduler.PendingCount + VoxelGpuScratchArena.MaximumBatchSize - 1) / VoxelGpuScratchArena.MaximumBatchSize;
	}

	private void PublishCompletedEmits()
	{
		while ( _publications.TryPeek( out var publication ) && publication.PublishEpoch <= _epoch )
		{
			_publications.Dequeue();
			for ( var residentIndex = 0; residentIndex < publication.Count; residentIndex++ )
			{
				var resident = publication.Residents[residentIndex];
				if ( !_scheduler.IsCurrent( resident.Key, resident.Generation ) ||
					!_residents.TryPublish( resident.Slot, resident.Key, resident.Generation, resident.Allocation, resident.Descriptor, out var replaced ) )
				{
					if ( !IsDesired( resident.Key ) )
					{
						RemovePendingRequest( resident.Key );
						_residents.CancelUnpublishedReservation( resident.Key );
					}
					_diagnostics.StalePublicationsRejected++;
					_pool.Retire( resident.Allocation, _epoch + VoxelGpuCapabilities.RetirementEpochs );
					continue;
				}
				if ( !replaced.IsEmpty ) _pool.Retire( replaced, _epoch + VoxelGpuCapabilities.RetirementEpochs );
				RemovePendingRequest( resident.Key );
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
		_diagnostics.PendingPublicationCount = _publications.Count;
		_diagnostics.BlockedRequests = BlockedRequestCount;
		_diagnostics.CapacityLimited = BlockedRequestCount > 0;
		_diagnostics.QueuesBounded = _scheduler.PendingCount <= _scheduler.MaximumPendingRequests && _publications.Count <= VoxelGpuScratchArena.RingSize;
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

	private void BlockRequest( VoxelVisualBlockKey key, int vertexCount, int indexCount )
	{
		lock ( _desiredSync )
			if ( _desiredKeys.Contains( key ) )
			{
				_blockedRequests.Add( key );
				_blockedDetails[key] = new BlockedRequest( vertexCount, indexCount );
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
		_pool.Dispose();
		_scheduler.Clear();
		Delete();
	}

	private sealed class BatchContext
	{
		public VoxelGpuBatchScheduler.ScheduledRequest[] Requests { get; }
		public int[] Slots { get; }
		public int Count { get; set; }
		public BatchContext( VoxelGpuBatchScheduler.ScheduledRequest[] requests, int[] slots ) { Requests = requests; Slots = slots; }
	}
	private readonly record struct PendingResident( int Slot, VoxelVisualBlockKey Key, uint Generation, long RequestedTimestamp, VoxelGpuAllocationHandle Allocation, VoxelGpuResidentDescriptor Descriptor );
	private readonly record struct PendingPublication( ulong PublishEpoch, long RequestedTimestamp, PendingResident[] Residents, int Count );
}
