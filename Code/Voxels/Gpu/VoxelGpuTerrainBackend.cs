internal sealed class VoxelGpuTerrainBackend : SceneCustomObject, System.IDisposable
{
	private readonly VoxelGpuCapabilityReport _capabilities;
	private readonly VoxelGpuTerrainDiagnosticCounters _diagnostics = new();
	private readonly VoxelGpuBatchScheduler _scheduler = new();
	private readonly VoxelGpuScratchArena[] _scratchRing;
	private readonly VoxelGpuMeshPool _pool;
	private readonly VoxelGpuResidentTable _residents;
	private readonly VoxelGpuTerrainRenderer _renderer;
	private readonly Queue<PendingPublication> _publications = new();
	private readonly int _chunkSize;
	private readonly float _voxelSize;
	private readonly BatchContext[] _activeBatches;
	private ulong _epoch;
	private long _nextProgressLogTimestamp;
	private bool _settledLogged;
	private bool _disposed;

	public bool IsSettled => _scheduler.PendingCount == 0 && _activeBatches.All( batch => batch is null ) && _publications.Count == 0 && _scratchRing.All( scratch => scratch.IsIdle );
	public bool IsAvailable => _capabilities.Available;

	public VoxelGpuTerrainBackend(
		SceneWorld world,
		CameraComponent camera,
		int chunkSize,
		float voxelSize,
		float sdfClampDistance,
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
		_scratchRing = new VoxelGpuScratchArena[VoxelGpuScratchArena.RingSize];
		for ( var index = 0; index < _scratchRing.Length; index++ )
			_scratchRing[index] = new VoxelGpuScratchArena( chunkSize, voxelSize, sdfClampDistance );
		_activeBatches = new BatchContext[_scratchRing.Length];
		_pool = new VoxelGpuMeshPool( vertexCapacity, indexCapacity );
		_residents = new VoxelGpuResidentTable( residentCapacity );
		_renderer = new VoxelGpuTerrainRenderer( world, camera, _pool, _residents, _diagnostics );
		_diagnostics.ScratchBytes = _scratchRing.Sum( scratch => scratch.CapacityBytes );
		_nextProgressLogTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() + 10 * System.Diagnostics.Stopwatch.Frequency;
		Bounds = BBox.FromPositionAndSize( Vector3.Zero, Vector3.One * 1_000_000_000.0f );
	}

	public void QueueStaticSet( IEnumerable<Vector3Int> coordinates, int ruleVersion )
	{
		_diagnostics.RuleVersion = ruleVersion;
		foreach ( var coordinate in coordinates )
		{
			_scheduler.Enqueue( new VoxelVisualBlockKey( coordinate, 0, ruleVersion ) );
			_diagnostics.RequestedBlocks++;
		}
		UpdatePendingCountBatches();
	}

	public VoxelGpuTerrainDiagnostics CaptureDiagnostics() => _diagnostics.Snapshot( _capabilities, _residents, _pool );

	public override void RenderSceneObject()
	{
		if ( _disposed ) return;
		try
		{
			_epoch++;
			_pool.Reclaim( _epoch );
			PublishCompletedEmits();
			for ( var index = 0; index < _scratchRing.Length; index++ ) ProcessCountReadback( index );
			SubmitCountBatches();
			LogProgress();
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
		Log.Info( $"Voxel GPU terrain progress: settled={IsSettled}, requested={diagnostics.RequestedBlocks:N0}, residents={diagnostics.ResidentBlocks:N0}, pendingCount={diagnostics.PendingCountBatches:N0}, pendingEmit={diagnostics.PendingEmitBatches:N0}, readbacks={diagnostics.CountReadbackCount:N0}, visibleDraws={diagnostics.VisibleDrawCommands:N0}, poolUsed={diagnostics.PoolUsedBytes:N0}/{diagnostics.PoolCapacityBytes:N0}B, backpressure={diagnostics.BackpressureEvents:N0}, allocationFailures={diagnostics.AllocationFailures:N0}, failure={diagnostics.Failure}." );
	}

	private void SubmitCountBatches()
	{
		for ( var arenaIndex = 0; arenaIndex < _scratchRing.Length && _scheduler.PendingCount > 0; arenaIndex++ )
		{
			if ( _activeBatches[arenaIndex] is not null || !_scratchRing[arenaIndex].IsIdle ) continue;
			var scheduled = _scheduler.TakeBatch( VoxelGpuScratchArena.MaximumBatchSize );
			var requests = new VoxelGpuBlockRequest[scheduled.Length];
			var slots = new int[scheduled.Length];
			for ( var index = 0; index < scheduled.Length; index++ )
			{
				var item = scheduled[index];
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

			_activeBatches[arenaIndex] = new BatchContext( scheduled, slots );
			if ( !_scratchRing[arenaIndex].TrySubmitCount( requests, out var submissionMilliseconds ) )
			{
				_activeBatches[arenaIndex] = null;
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
		if ( activeBatch is null || !scratch.TryTakeCounts( out var counts, out var readbackMilliseconds ) ) return;
		_diagnostics.RecordCountReadback( readbackMilliseconds );
		var allocations = new VoxelGpuAllocationDescriptor[counts.Length];
		var pending = new List<PendingResident>( counts.Length );
		for ( var index = 0; index < counts.Length; index++ )
		{
			var scheduled = activeBatch.Requests[index];
			var count = counts[index];
			if ( count.RequestIndex != index || count.Generation != scheduled.Generation || !_scheduler.IsCurrent( scheduled.Key, scheduled.Generation ) )
			{
				_diagnostics.StalePublicationsRejected++;
				continue;
			}
			if ( count.Overflow != 0 || count.VertexCount > int.MaxValue || count.IndexCount > int.MaxValue ||
				!_pool.TryAllocate( (int)count.VertexCount, (int)count.IndexCount, count.Generation, out var handle ) )
			{
				_diagnostics.BackpressureEvents++;
				_residents.CancelUnpublishedReservation( scheduled.Key );
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
				IndexCount = count.IndexCount
			};
			pending.Add( new PendingResident( activeBatch.Slots[index], scheduled.Key, scheduled.Generation, scheduled.RequestedTimestamp, handle, descriptor ) );
		}

		if ( !scratch.TrySubmitEmit( allocations, _pool, out var emitMilliseconds ) )
		{
			foreach ( var resident in pending ) _pool.ReleaseImmediately( resident.Allocation );
			throw new System.InvalidOperationException( "scratch arena rejected an emit batch after count publication" );
		}
		_diagnostics.EmitSubmissionMilliseconds += emitMilliseconds;
		_diagnostics.PendingEmitBatches++;
		_publications.Enqueue( new PendingPublication( _epoch + 1, activeBatch.Requests.Min( request => request.RequestedTimestamp ), pending.ToArray() ) );
		_activeBatches[arenaIndex] = null;
		UpdatePendingCountBatches();
	}

	private void UpdatePendingCountBatches()
	{
		_diagnostics.PendingCountBatches = _activeBatches.Count( batch => batch is not null ) +
			(_scheduler.PendingCount + VoxelGpuScratchArena.MaximumBatchSize - 1) / VoxelGpuScratchArena.MaximumBatchSize;
	}

	private void PublishCompletedEmits()
	{
		while ( _publications.TryPeek( out var publication ) && publication.PublishEpoch <= _epoch )
		{
			_publications.Dequeue();
			foreach ( var resident in publication.Residents )
			{
				if ( !_scheduler.IsCurrent( resident.Key, resident.Generation ) ||
					!_residents.TryPublish( resident.Slot, resident.Key, resident.Generation, resident.Allocation, resident.Descriptor, out var replaced ) )
				{
					_diagnostics.StalePublicationsRejected++;
					_pool.Retire( resident.Allocation, _epoch + VoxelGpuCapabilities.RetirementEpochs );
					continue;
				}
				if ( !replaced.IsEmpty ) _pool.Retire( replaced, _epoch + VoxelGpuCapabilities.RetirementEpochs );
				_diagnostics.RecordRequestToVisible( System.Diagnostics.Stopwatch.GetElapsedTime( resident.RequestedTimestamp ).TotalMilliseconds );
			}
			_diagnostics.RecordBatchCompletion( System.Diagnostics.Stopwatch.GetElapsedTime( publication.RequestedTimestamp ).TotalMilliseconds );
			_diagnostics.PendingEmitBatches--;
			_renderer.MarkDirty();
		}
	}

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

	private sealed record BatchContext( VoxelGpuBatchScheduler.ScheduledRequest[] Requests, int[] Slots );
	private readonly record struct PendingResident( int Slot, VoxelVisualBlockKey Key, uint Generation, long RequestedTimestamp, VoxelGpuAllocationHandle Allocation, VoxelGpuResidentDescriptor Descriptor );
	private readonly record struct PendingPublication( ulong PublishEpoch, long RequestedTimestamp, PendingResident[] Residents );
}
