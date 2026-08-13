internal sealed class VoxelGpuBatchScheduler
{
	private readonly Queue<ScheduledRequest> _requests = new();
	private readonly Queue<ScheduledRequest> _retainedScratch = new();
	private readonly List<ScheduledRequest> _priorityScratch = new();
	private readonly Dictionary<VoxelVisualBlockKey, uint> _latestGenerations = new();
	private readonly object _sync = new();
	private readonly int _maximumPendingRequests;

	public VoxelGpuBatchScheduler( int maximumPendingRequests = 256 )
	{
		_maximumPendingRequests = System.Math.Max( 1, maximumPendingRequests );
	}

	public int PendingCount { get { lock ( _sync ) return _requests.Count; } }
	public int MaximumPendingRequests => _maximumPendingRequests;

	public bool TryEnqueue( VoxelVisualBlockKey key, out uint generation )
	{
		lock ( _sync )
		{
			if ( _requests.Count >= _maximumPendingRequests )
			{
				generation = 0;
				return false;
			}

			generation = _latestGenerations.TryGetValue( key, out var current ) ? checked( current + 1 ) : 1u;
			_latestGenerations[key] = generation;
			_requests.Enqueue( new ScheduledRequest( key, generation, System.Diagnostics.Stopwatch.GetTimestamp() ) );
			return true;
		}
	}

	public void Cancel( VoxelVisualBlockKey key )
	{
		lock ( _sync )
		{
			if ( _latestGenerations.TryGetValue( key, out var current ) ) _latestGenerations[key] = checked( current + 1 );
		}
	}

	public ScheduledRequest[] TakeBatch( int maximumCount )
	{
		lock ( _sync )
		{
			var count = System.Math.Min( maximumCount, _requests.Count );
			var batch = new ScheduledRequest[count];
			for ( var index = 0; index < count; index++ ) batch[index] = _requests.Dequeue();
			return batch;
		}
	}

	public int TakeBatch( int maximumCount, ScheduledRequest[] destination )
	{
		if ( destination is null || destination.Length < maximumCount ) throw new System.ArgumentException( "Destination is smaller than the requested batch.", nameof( destination ) );
		lock ( _sync )
		{
			var count = System.Math.Min( maximumCount, _requests.Count );
			for ( var index = 0; index < count; index++ ) destination[index] = _requests.Dequeue();
			return count;
		}
	}

	public int PruneToDesired( HashSet<VoxelVisualBlockKey> desiredKeys )
	{
		if ( desiredKeys is null ) throw new System.ArgumentNullException( nameof( desiredKeys ) );
		lock ( _sync )
		{
			_retainedScratch.Clear();
			while ( _requests.Count > 0 )
			{
				var request = _requests.Dequeue();
				if ( desiredKeys.Contains( request.Key ) && _latestGenerations.TryGetValue( request.Key, out var generation ) && generation == request.Generation )
					_retainedScratch.Enqueue( request );
			}
			while ( _retainedScratch.Count > 0 ) _requests.Enqueue( _retainedScratch.Dequeue() );
			return _requests.Count;
		}
	}

	public int PruneAndPrioritize( HashSet<VoxelVisualBlockKey> desiredKeys, IReadOnlyDictionary<VoxelVisualBlockKey, int> desiredRanks )
	{
		if ( desiredKeys is null ) throw new System.ArgumentNullException( nameof( desiredKeys ) );
		if ( desiredRanks is null ) throw new System.ArgumentNullException( nameof( desiredRanks ) );
		lock ( _sync )
		{
			_priorityScratch.Clear();
			while ( _requests.Count > 0 )
			{
				var request = _requests.Dequeue();
				if ( desiredKeys.Contains( request.Key ) && _latestGenerations.TryGetValue( request.Key, out var generation ) && generation == request.Generation )
					_priorityScratch.Add( request );
			}
			_priorityScratch.Sort( (left, right) =>
			{
				var leftRank = desiredRanks.TryGetValue( left.Key, out var leftValue ) ? leftValue : int.MaxValue;
				var rightRank = desiredRanks.TryGetValue( right.Key, out var rightValue ) ? rightValue : int.MaxValue;
				var rank = leftRank.CompareTo( rightRank );
				return rank != 0 ? rank : left.RequestedTimestamp.CompareTo( right.RequestedTimestamp );
			} );
			foreach ( var request in _priorityScratch ) _requests.Enqueue( request );
			return _requests.Count;
		}
	}

	public bool IsCurrent( VoxelVisualBlockKey key, uint generation )
	{
		lock ( _sync ) return _latestGenerations.TryGetValue( key, out var current ) && current == generation;
	}

	public bool TryGetGeneration( VoxelVisualBlockKey key, out uint generation )
	{
		lock ( _sync ) return _latestGenerations.TryGetValue( key, out generation ) && generation != 0;
	}

	public void Clear()
	{
		lock ( _sync )
		{
			_requests.Clear();
			_latestGenerations.Clear();
		}
	}

	internal readonly record struct ScheduledRequest( VoxelVisualBlockKey Key, uint Generation, long RequestedTimestamp );
}
