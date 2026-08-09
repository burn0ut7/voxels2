internal sealed class VoxelGpuBatchScheduler
{
	private readonly Queue<ScheduledRequest> _requests = new();
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

	public bool IsCurrent( VoxelVisualBlockKey key, uint generation )
	{
		lock ( _sync ) return _latestGenerations.TryGetValue( key, out var current ) && current == generation;
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
