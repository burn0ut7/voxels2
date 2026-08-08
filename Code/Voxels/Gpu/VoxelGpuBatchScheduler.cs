internal sealed class VoxelGpuBatchScheduler
{
	private readonly Queue<ScheduledRequest> _requests = new();
	private readonly Dictionary<VoxelVisualBlockKey, uint> _latestGenerations = new();

	public int PendingCount => _requests.Count;

	public uint Enqueue( VoxelVisualBlockKey key )
	{
		var generation = _latestGenerations.TryGetValue( key, out var current ) ? checked( current + 1 ) : 1u;
		_latestGenerations[key] = generation;
		_requests.Enqueue( new ScheduledRequest( key, generation, System.Diagnostics.Stopwatch.GetTimestamp() ) );
		return generation;
	}

	public ScheduledRequest[] TakeBatch( int maximumCount )
	{
		var count = System.Math.Min( maximumCount, _requests.Count );
		var batch = new ScheduledRequest[count];
		for ( var index = 0; index < count; index++ ) batch[index] = _requests.Dequeue();
		return batch;
	}

	public bool IsCurrent( VoxelVisualBlockKey key, uint generation ) =>
		_latestGenerations.TryGetValue( key, out var current ) && current == generation;

	public void Clear()
	{
		_requests.Clear();
		_latestGenerations.Clear();
	}

	internal readonly record struct ScheduledRequest( VoxelVisualBlockKey Key, uint Generation, long RequestedTimestamp );
}
