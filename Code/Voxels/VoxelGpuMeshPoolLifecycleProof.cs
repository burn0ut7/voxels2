internal readonly record struct VoxelGpuMeshPoolLifecycleResult(
	bool Passed,
	string Failure,
	long BudgetBytes,
	long PeakUsedBytes,
	int ChurnOperations,
	int AllocationFailures,
	int BackpressureEvents,
	int StalePublicationsRejected,
	double RetainedMemoryDeltaPercent
);

internal static class VoxelGpuMeshPoolLifecycleProof
{
	private const int DeferredReleaseEpochs = 2;
	private const int ChurnOperationCount = 1_024;

	public static VoxelGpuMeshPoolLifecycleResult Run( int referenceVertexCount, int referenceIndexCount )
	{
		var surfaceBytes = Align( checked((long)referenceVertexCount * 44 + (long)referenceIndexCount * sizeof( uint )) );
		var budgetBytes = checked( surfaceBytes * 64 );
		var allocator = new PoolAllocator( budgetBytes );
		var generations = new int[64];
		var epoch = 0;
		uint random = 0x6d2b79f5;

		for ( var operation = 0; operation < ChurnOperationCount; operation++ )
		{
			random = random * 1_664_525u + 1_013_904_223u;
			var block = (int)(random % (uint)generations.Length);
			var generation = ++generations[block];
			var topology = (random >> 8) & 3u;
			var requestedBytes = topology == 0 ? 0 : Align( surfaceBytes * (1 + (random >> 12) % 3) / 2 );
			allocator.Tick( epoch++ );
			if ( !TryReplaceWithBackpressure( allocator, block, generation, requestedBytes, ref epoch ) )
				return allocator.Result( false, $"bounded churn could not allocate block {block} generation {generation}" );
			if ( !allocator.Publish( block, generation ) )
				return allocator.Result( false, $"fresh generation {generation} for block {block} was rejected" );
			if ( generation > 1 && operation % 7 == 0 && allocator.Publish( block, generation - 1 ) )
				return allocator.Result( false, $"stale generation {generation - 1} for block {block} published" );
			if ( operation % 5 == 0 ) allocator.Evict( (block + 17) % generations.Length, epoch );
			if ( !allocator.ValidateNoOverlap( out var overlapFailure ) )
				return allocator.Result( false, overlapFailure );
		}

		allocator.EvictAll( epoch );
		allocator.Tick( epoch += DeferredReleaseEpochs );
		var exhaustionObserved = false;
		for ( var block = 1_000; block < 2_000; block++ )
		{
			if ( allocator.TryReplace( block, 1, surfaceBytes, epoch ) )
			{
				allocator.Publish( block, 1 );
				continue;
			}
			allocator.RecordBackpressure();
			exhaustionObserved = true;
			break;
		}
		if ( !exhaustionObserved ) return allocator.Result( false, "deliberate pool exhaustion did not apply backpressure" );

		allocator.EvictAll( epoch );
		allocator.Tick( epoch += DeferredReleaseEpochs );
		if ( !RunRoute( allocator, surfaceBytes, ref epoch, 1, out var firstRetained ) )
			return allocator.Result( false, "first return-to-origin route could not stabilize" );
		if ( !RunRoute( allocator, surfaceBytes, ref epoch, 2, out var secondRetained ) )
			return allocator.Result( false, "second return-to-origin route could not stabilize" );
		var retainedDelta = firstRetained == 0 ? (secondRetained == 0 ? 0.0 : 100.0) : System.Math.Abs( secondRetained - firstRetained ) * 100.0 / firstRetained;

		for ( var block = 0; block < 16; block++ )
		{
			if ( allocator.Publish( block, 1 ) ) return allocator.Result( false, $"stale publication storm replaced block {block}" );
		}
		if ( !allocator.ValidateNoOverlap( out var finalOverlapFailure ) ) return allocator.Result( false, finalOverlapFailure );
		if ( allocator.PeakUsedBytes > allocator.BudgetBytes ) return allocator.Result( false, "pool usage exceeded its hard budget" );
		if ( retainedDelta > 5.0 ) return allocator.Result( false, $"retained bytes changed by {retainedDelta:F2}% between settled routes" );
		if ( allocator.AllocationFailures == 0 || allocator.BackpressureEvents == 0 ) return allocator.Result( false, "exhaustion/backpressure policy was not exercised" );
		if ( allocator.StalePublicationsRejected == 0 ) return allocator.Result( false, "stale-publication rejection was not exercised" );
		return allocator.Result( true, "bounded allocation, replacement, deferred reclaim, exhaustion, and stale-generation gates match" , retainedDelta );
	}

	private static bool TryReplaceWithBackpressure( PoolAllocator allocator, int block, int generation, long bytes, ref int epoch )
	{
		if ( allocator.TryReplace( block, generation, bytes, epoch ) ) return true;
		allocator.RecordBackpressure();
		for ( var attempt = 0; attempt < 64; attempt++ )
		{
			allocator.Evict( (block + attempt + 1) % 64, epoch );
			allocator.Tick( epoch += DeferredReleaseEpochs );
			if ( allocator.TryReplace( block, generation, bytes, epoch ) ) return true;
		}
		return false;
	}

	private static bool RunRoute( PoolAllocator allocator, long surfaceBytes, ref int epoch, int routeGeneration, out long retainedBytes )
	{
		for ( var block = 0; block < 16; block++ )
		{
			if ( !TryReplaceWithBackpressure( allocator, block, routeGeneration * 2, surfaceBytes, ref epoch ) ) { retainedBytes = 0; return false; }
			allocator.Publish( block, routeGeneration * 2 );
		}
		for ( var block = 100; block < 116; block++ )
		{
			if ( !TryReplaceWithBackpressure( allocator, block, routeGeneration, surfaceBytes, ref epoch ) ) { retainedBytes = 0; return false; }
			allocator.Publish( block, routeGeneration );
		}
		for ( var block = 0; block < 16; block++ ) allocator.Evict( block, epoch );
		allocator.Tick( epoch += DeferredReleaseEpochs );
		for ( var block = 0; block < 16; block++ )
		{
			if ( !TryReplaceWithBackpressure( allocator, block, routeGeneration * 2 + 1, surfaceBytes, ref epoch ) ) { retainedBytes = 0; return false; }
			allocator.Publish( block, routeGeneration * 2 + 1 );
		}
		for ( var block = 100; block < 116; block++ ) allocator.Evict( block, epoch );
		allocator.Tick( epoch += DeferredReleaseEpochs );
		retainedBytes = allocator.UsedBytes;
		return true;
	}

	private static long Align( long bytes ) => (bytes + 255) & ~255L;

	private sealed class PoolAllocator
	{
		private readonly VoxelGpuRangeAllocator _ranges;
		private readonly Dictionary<int, Resident> _residents = new();
		private readonly List<DeferredRange> _deferred = new();

		public long BudgetBytes { get; }
		public long PeakUsedBytes { get; private set; }
		public long UsedBytes => _ranges.UsedCount;
		public int AllocationFailures { get; private set; }
		public int BackpressureEvents { get; private set; }
		public int StalePublicationsRejected { get; private set; }

		public PoolAllocator( long budgetBytes )
		{
			if ( budgetBytes > int.MaxValue ) throw new System.ArgumentOutOfRangeException( nameof( budgetBytes ) );
			BudgetBytes = budgetBytes;
			_ranges = new VoxelGpuRangeAllocator( (int)budgetBytes );
		}

		public bool TryReplace( int block, int generation, long bytes, int epoch )
		{
			if ( _residents.TryGetValue( block, out var existing ) && generation <= existing.Generation )
			{
				StalePublicationsRejected++;
				return false;
			}
			var allocation = bytes == 0 ? new PoolRange( -1, 0 ) : Allocate( bytes );
			if ( bytes != 0 && allocation.Offset < 0 )
			{
				AllocationFailures++;
				return false;
			}
			if ( existing.Size != 0 ) _deferred.Add( new DeferredRange( existing.Offset, existing.Size, epoch + DeferredReleaseEpochs ) );
			_residents[block] = new Resident( allocation.Offset, allocation.Size, generation, false );
			UpdatePeak();
			return true;
		}

		public bool Publish( int block, int generation )
		{
			if ( !_residents.TryGetValue( block, out var resident ) || resident.Generation != generation )
			{
				StalePublicationsRejected++;
				return false;
			}
			_residents[block] = resident with { Published = true };
			return true;
		}

		public void Evict( int block, int epoch )
		{
			if ( !_residents.Remove( block, out var resident ) || resident.Size == 0 ) return;
			_deferred.Add( new DeferredRange( resident.Offset, resident.Size, epoch + DeferredReleaseEpochs ) );
		}

		public void EvictAll( int epoch )
		{
			foreach ( var block in _residents.Keys.ToArray() ) Evict( block, epoch );
		}

		public void Tick( int epoch )
		{
			for ( var index = _deferred.Count - 1; index >= 0; index-- )
			{
				var range = _deferred[index];
				if ( range.ReleaseEpoch > epoch ) continue;
				_ranges.Release( new VoxelGpuPoolRange( (int)range.Offset, (int)range.Size ) );
				_deferred.RemoveAt( index );
			}
			UpdatePeak();
		}

		public void RecordBackpressure() => BackpressureEvents++;

		public bool ValidateNoOverlap( out string failure )
		{
			var ranges = _residents.Values.Where( resident => resident.Size > 0 ).Select( resident => new PoolRange( resident.Offset, resident.Size ) )
				.Concat( _deferred.Select( range => new PoolRange( range.Offset, range.Size ) ) ).OrderBy( range => range.Offset ).ToArray();
			for ( var index = 1; index < ranges.Length; index++ )
			{
				if ( ranges[index - 1].Offset + ranges[index - 1].Size <= ranges[index].Offset ) continue;
				failure = $"mesh pool ranges overlap at byte {ranges[index].Offset:N0}";
				return false;
			}
			failure = string.Empty;
			return true;
		}

		public VoxelGpuMeshPoolLifecycleResult Result( bool passed, string failure, double retainedDeltaPercent = 0.0 ) => new(
			passed, failure, BudgetBytes, PeakUsedBytes, ChurnOperationCount, AllocationFailures, BackpressureEvents,
			StalePublicationsRejected, retainedDeltaPercent );

		private PoolRange Allocate( long bytes )
		{
			if ( bytes <= int.MaxValue && _ranges.TryAllocate( (int)bytes, out var allocation ) )
				return new PoolRange( allocation.Offset, allocation.Count );
			return new PoolRange( -1, 0 );
		}

		private void UpdatePeak() => PeakUsedBytes = System.Math.Max( PeakUsedBytes, UsedBytes );
	}

	private readonly record struct PoolRange( long Offset, long Size );
	private readonly record struct DeferredRange( long Offset, long Size, int ReleaseEpoch );
	private readonly record struct Resident( long Offset, long Size, int Generation, bool Published );
}
