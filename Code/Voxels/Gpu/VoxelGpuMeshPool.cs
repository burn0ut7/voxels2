internal sealed class VoxelGpuMeshPool : System.IDisposable
{
	private readonly VoxelGpuRangeAllocator _vertexAllocator;
	private readonly VoxelGpuRangeAllocator _indexAllocator;
	private readonly List<RetiredAllocation> _retired = new();
	private readonly List<VoxelGpuPoolRange> _reclaimVertices = new();
	private readonly List<VoxelGpuPoolRange> _reclaimIndices = new();

	public GpuBuffer<SimpleVertex> Vertices { get; }
	public GpuBuffer<uint> Indices { get; }
	public int VertexCapacity => _vertexAllocator.Capacity;
	public int IndexCapacity => _indexAllocator.Capacity;
	public int UsedVertices => _vertexAllocator.UsedCount;
	public int UsedIndices => _indexAllocator.UsedCount;
	public int PeakUsedVertices { get; private set; }
	public int PeakUsedIndices { get; private set; }
	public int AllocationFailures { get; private set; }
	public int VertexFree => _vertexAllocator.FreeCount;
	public int IndexFree => _indexAllocator.FreeCount;
	public int VertexLargestFree => _vertexAllocator.LargestFreeRange;
	public int IndexLargestFree => _indexAllocator.LargestFreeRange;
	public int VertexFreeRangeCount => _vertexAllocator.FreeRangeCount;
	public int IndexFreeRangeCount => _indexAllocator.FreeRangeCount;
	public int DeferredAllocationCount => _retired.Count;
	public long CapacityBytes => (long)VertexCapacity * 44 + (long)IndexCapacity * sizeof( uint );

	public VoxelGpuMeshPool( int vertexCapacity, int indexCapacity )
	{
		_vertexAllocator = new VoxelGpuRangeAllocator( vertexCapacity );
		_indexAllocator = new VoxelGpuRangeAllocator( indexCapacity );
		Vertices = new GpuBuffer<SimpleVertex>( vertexCapacity, GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.Vertex, "Voxel GPU Persistent Vertices" );
		Indices = new GpuBuffer<uint>( indexCapacity, GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.Index, "Voxel GPU Persistent Indices" );
	}

	public bool TryAllocate( int vertexCount, int indexCount, uint generation, out VoxelGpuAllocationHandle handle ) =>
		TryAllocate( vertexCount, indexCount, generation, out handle, out _ );

	public bool TryAllocate( int vertexCount, int indexCount, uint generation, out VoxelGpuAllocationHandle handle, out VoxelGpuAllocationFailureReason failureReason )
	{
		if ( !_vertexAllocator.TryAllocate( vertexCount, out var vertices ) )
		{
			AllocationFailures++;
			handle = default;
			failureReason = vertexCount > VertexCapacity ? VoxelGpuAllocationFailureReason.OversizedVertex : VertexLargestFree < vertexCount ? VoxelGpuAllocationFailureReason.VertexFragmentation : VoxelGpuAllocationFailureReason.VertexCapacity;
			return false;
		}
		if ( !_indexAllocator.TryAllocate( indexCount, out var indices ) )
		{
			_vertexAllocator.Release( vertices );
			AllocationFailures++;
			handle = default;
			failureReason = indexCount > IndexCapacity ? VoxelGpuAllocationFailureReason.OversizedIndex : IndexLargestFree < indexCount ? VoxelGpuAllocationFailureReason.IndexFragmentation : VoxelGpuAllocationFailureReason.IndexCapacity;
			return false;
		}
		handle = new VoxelGpuAllocationHandle( vertices, indices, generation );
		PeakUsedVertices = System.Math.Max( PeakUsedVertices, UsedVertices );
		PeakUsedIndices = System.Math.Max( PeakUsedIndices, UsedIndices );
		failureReason = VoxelGpuAllocationFailureReason.None;
		return true;
	}

	public void Retire( VoxelGpuAllocationHandle handle, ulong releaseEpoch )
	{
		if ( handle.IsEmpty ) return;
		_retired.Add( new RetiredAllocation( handle, releaseEpoch ) );
	}

	public int Reclaim( ulong completedEpoch )
	{
		_reclaimVertices.Clear();
		_reclaimIndices.Clear();
		var writeIndex = 0;
		for ( var index = 0; index < _retired.Count; index++ )
		{
			var retired = _retired[index];
			if ( retired.ReleaseEpoch > completedEpoch )
			{
				_retired[writeIndex++] = retired;
				continue;
			}
			_reclaimVertices.Add( retired.Handle.Vertices );
			_reclaimIndices.Add( retired.Handle.Indices );
		}
		if ( writeIndex < _retired.Count ) _retired.RemoveRange( writeIndex, _retired.Count - writeIndex );
		_vertexAllocator.ReleaseBatch( _reclaimVertices );
		_indexAllocator.ReleaseBatch( _reclaimIndices );
		return _reclaimVertices.Count;
	}

	public void ReleaseImmediately( VoxelGpuAllocationHandle handle )
	{
		_vertexAllocator.Release( handle.Vertices );
		_indexAllocator.Release( handle.Indices );
	}

	public bool Validate( out string failure )
	{
		if ( !_vertexAllocator.Validate( out failure ) ) return false;
		return _indexAllocator.Validate( out failure );
	}

	public void Dispose()
	{
		Vertices.Dispose();
		Indices.Dispose();
	}

	private readonly record struct RetiredAllocation( VoxelGpuAllocationHandle Handle, ulong ReleaseEpoch );
}

internal enum VoxelGpuAllocationFailureReason
{
	None,
	VertexCapacity,
	IndexCapacity,
	VertexFragmentation,
	IndexFragmentation,
	OversizedVertex,
	OversizedIndex
}
