internal sealed class VoxelGpuTransitionScratchArena : System.IDisposable
{
	public const int MaximumBatchSize = 128;
	private const int SampleCount = 13;
	private readonly object _stateLock = new();
	private readonly ComputeShader _count;
	private readonly ComputeShader _emit;
	private readonly GpuBuffer<VoxelGpuTransitionRequest> _requests;
	private readonly GpuBuffer<float> _samples;
	private readonly GpuBuffer<uint> _lookup;
	private readonly GpuBuffer<VoxelGpuCountResult> _countResults;
	private readonly GpuBuffer<VoxelGpuAllocationDescriptor> _allocations;
	private readonly GpuBuffer<VoxelGpuEditOp> _editOperations;
	private readonly GpuBuffer<uint> _editIndices;
	private readonly int _chunkSize;
	private readonly int _transitionCellsPerAxis;
	private readonly int _transitionCellCount;
	private readonly float _voxelSize;
	private readonly float _sdfClampDistance;
	private readonly float _simplexFrequency;
	private readonly float _simplexAmplitude;
	private readonly float _simplexBaseHeight;
	private readonly int _simplexSeed;
	private readonly int _geometryOffset;
	private readonly int _triangleOffset;
	private readonly int _vertexOffset;
	private readonly VoxelGpuCountResult[] _completedCountBuffer = new VoxelGpuCountResult[MaximumBatchSize];
	private VoxelGpuCountResult[] _completedCounts;
	private int _completedCount;
	private long _readbackTimestamp;
	private int _batchSize;
	private int _uploadedEditOperationCount;
	private ArenaState _state;
	private bool _disposed;

	public long CapacityBytes { get; }
	public bool IsIdle { get { lock ( _stateLock ) return _state == ArenaState.Idle; } }

	public VoxelGpuTransitionScratchArena( int chunkSize, float voxelSize, float sdfClampDistance, float simplexFrequency, float simplexAmplitude, float simplexBaseHeight, int simplexSeed )
	{
		_chunkSize = chunkSize;
		if ( chunkSize < 2 || (chunkSize & 1) != 0 ) throw new System.ArgumentOutOfRangeException( nameof( chunkSize ), chunkSize, "GPU transitions require an even chunk size of at least two." );
		_transitionCellsPerAxis = chunkSize / 2;
		_transitionCellCount = checked( _transitionCellsPerAxis * _transitionCellsPerAxis );
		_voxelSize = voxelSize;
		_sdfClampDistance = sdfClampDistance;
		_simplexFrequency = simplexFrequency;
		_simplexAmplitude = simplexAmplitude;
		_simplexBaseHeight = simplexBaseHeight;
		_simplexSeed = simplexSeed;
		_geometryOffset = VoxelTransvoxelTransitionTables.CellClass.Length;
		_triangleOffset = _geometryOffset + VoxelTransvoxelTransitionTables.GeometryCounts.Length;
		_vertexOffset = _triangleOffset + VoxelTransvoxelTransitionTables.TriangleIndices.Length;
		_requests = new GpuBuffer<VoxelGpuTransitionRequest>( MaximumBatchSize, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Requests" );
		_samples = new GpuBuffer<float>( MaximumBatchSize * _transitionCellCount * SampleCount, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Samples" );
		_lookup = CreateLookupBuffer();
		_countResults = new GpuBuffer<VoxelGpuCountResult>( MaximumBatchSize, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Count Results" );
		_allocations = new GpuBuffer<VoxelGpuAllocationDescriptor>( MaximumBatchSize, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Allocations" );
		_editOperations = new GpuBuffer<VoxelGpuEditOp>( VoxelEditJournal.MaximumGpuOperations, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Active Edits" );
		_editIndices = new GpuBuffer<uint>( MaximumBatchSize * VoxelEditJournal.MaximumGpuOperations, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Edit Indices" );
		_count = new ComputeShader( "shaders/voxel_gpu_transition_count_v1_cs.shader" );
		_emit = new ComputeShader( "shaders/voxel_gpu_transition_emit_v1_cs.shader" );
		BindAttributes();
		CapacityBytes = (long)MaximumBatchSize * 64 + (long)MaximumBatchSize * _transitionCellCount * SampleCount * sizeof( float ) + (long)_lookup.ElementCount * sizeof( uint ) + (long)MaximumBatchSize * (32 + 64) + (long)VoxelEditJournal.MaximumGpuOperations * 80 + (long)MaximumBatchSize * VoxelEditJournal.MaximumGpuOperations * sizeof( uint );
	}

	public void SetEditOperations( VoxelGpuEditOp[] operations, int count )
	{
		if ( operations is null || count < 0 || count > VoxelEditJournal.MaximumGpuOperations || operations.Length < System.Math.Max( 1, count ) ) throw new System.ArgumentOutOfRangeException( nameof( count ) );
		if ( count < _uploadedEditOperationCount ) throw new System.InvalidOperationException( "GPU edit journals are append-only for the lifetime of a transition scratch arena." );
		var appendCount = count - _uploadedEditOperationCount;
		if ( appendCount <= 0 ) return;
		_editOperations.SetData( new System.ReadOnlySpan<VoxelGpuEditOp>( operations, _uploadedEditOperationCount, appendCount ), _uploadedEditOperationCount );
		_uploadedEditOperationCount = count;
	}

	public bool TrySubmitCount( VoxelGpuTransitionRequest[] requests, int count, uint[] editIndices, int editIndexCount, out double submissionMilliseconds )
	{
		lock ( _stateLock )
		{
			if ( _disposed || _state != ArenaState.Idle || requests is null || count is < 1 or > MaximumBatchSize || requests.Length < count || editIndices is null || editIndexCount < 0 || editIndexCount > editIndices.Length || editIndexCount > _editIndices.ElementCount ) { submissionMilliseconds = 0.0; return false; }
			_state = ArenaState.CountSubmitted;
			_batchSize = count;
		}
		var start = System.Diagnostics.Stopwatch.GetTimestamp();
		_requests.SetData( new System.Span<VoxelGpuTransitionRequest>( requests, 0, count ) );
		if ( editIndexCount > 0 ) _editIndices.SetData( new System.Span<uint>( editIndices, 0, editIndexCount ) );
		_countResults.Clear();
		Graphics.ResourceBarrierTransition( _requests, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		Graphics.ResourceBarrierTransition( _lookup, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		Graphics.ResourceBarrierTransition( _editOperations, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		Graphics.ResourceBarrierTransition( _editIndices, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		Graphics.ResourceBarrierTransition( _samples, Sandbox.Rendering.ResourceState.UnorderedAccess );
		Graphics.ResourceBarrierTransition( _countResults, Sandbox.Rendering.ResourceState.UnorderedAccess );
		_count.Attributes.Set( "BatchSize", count );
		_emit.Attributes.Set( "BatchSize", count );
		// ComputeShader.Dispatch takes explicit thread counts and divides by the
		// shader's [numthreads] declaration internally. Passing the group count
		// here only evaluates the first one or two requests in a normal batch.
		_count.Dispatch( count, 1, 1 );
		Graphics.UavBarrier( _samples );
		Graphics.UavBarrier( _countResults );
		_readbackTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		_countResults.GetDataAsync( OnCountsRead, 0, count );
		submissionMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( start ).TotalMilliseconds;
		return true;
	}

	public bool TryTakeCounts( out VoxelGpuCountResult[] counts, out int count, out double readbackMilliseconds )
	{
		lock ( _stateLock )
		{
			if ( _state != ArenaState.CountReady ) { counts = null; count = 0; readbackMilliseconds = 0.0; return false; }
			counts = _completedCounts;
			count = _completedCount;
			_completedCounts = null;
			_completedCount = 0;
			readbackMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( _readbackTimestamp ).TotalMilliseconds;
			_state = ArenaState.EmitReady;
			return true;
		}
	}

	public bool TrySubmitEmit( VoxelGpuAllocationDescriptor[] allocations, int count, VoxelGpuMeshPool pool, out double submissionMilliseconds )
	{
		lock ( _stateLock )
		{
			if ( _disposed || _state != ArenaState.EmitReady || allocations is null || count < 1 || count > _batchSize || allocations.Length < _batchSize ) { submissionMilliseconds = 0.0; return false; }
			_state = ArenaState.Idle;
		}
		var start = System.Diagnostics.Stopwatch.GetTimestamp();
		_allocations.SetData( new System.Span<VoxelGpuAllocationDescriptor>( allocations, 0, _batchSize ) );
		Graphics.ResourceBarrierTransition( pool.Vertices, Sandbox.Rendering.ResourceState.UnorderedAccess );
		Graphics.ResourceBarrierTransition( pool.Indices, Sandbox.Rendering.ResourceState.UnorderedAccess );
		Graphics.ResourceBarrierTransition( _samples, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		Graphics.ResourceBarrierTransition( _editOperations, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		_emit.Attributes.Set( "OutputVertices", pool.Vertices );
		_emit.Attributes.Set( "OutputIndices", pool.Indices );
		_emit.Dispatch( _batchSize, 1, 1 );
		Graphics.UavBarrier( pool.Vertices );
		Graphics.UavBarrier( pool.Indices );
		submissionMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( start ).TotalMilliseconds;
		return true;
	}

	private void OnCountsRead( System.ReadOnlySpan<VoxelGpuCountResult> counts )
	{
		lock ( _stateLock )
		{
			if ( _disposed || _state != ArenaState.CountSubmitted ) return;
			counts.CopyTo( _completedCountBuffer );
			_completedCounts = _completedCountBuffer;
			_completedCount = counts.Length;
			_state = ArenaState.CountReady;
		}
	}

	private void BindAttributes()
	{
		foreach ( var shader in new[] { _count, _emit } )
		{
			shader.Attributes.Set( "Requests", _requests );
			shader.Attributes.Set( "Samples", _samples );
			shader.Attributes.Set( "Lookup", _lookup );
			shader.Attributes.Set( "CountResults", _countResults );
			shader.Attributes.Set( "Allocations", _allocations );
			shader.Attributes.Set( "ChunkSize", _chunkSize );
			shader.Attributes.Set( "SampleCount", SampleCount );
			shader.Attributes.Set( "TransitionCellCount", _transitionCellCount );
			shader.Attributes.Set( "TransitionCellsPerAxis", _transitionCellsPerAxis );
			shader.Attributes.Set( "BatchSize", 0 );
			shader.Attributes.Set( "GeometryOffset", _geometryOffset );
			shader.Attributes.Set( "TriangleOffset", _triangleOffset );
			shader.Attributes.Set( "VertexOffset", _vertexOffset );
			shader.Attributes.Set( "VoxelSize", _voxelSize );
			shader.Attributes.Set( "SdfClampDistance", _sdfClampDistance );
			shader.Attributes.Set( "SimplexFrequency", _simplexFrequency );
			shader.Attributes.Set( "SimplexAmplitude", _simplexAmplitude );
			shader.Attributes.Set( "SimplexBaseHeight", _simplexBaseHeight );
			shader.Attributes.Set( "SimplexSeed", _simplexSeed );
			shader.Attributes.Set( "VoxelEditOperations", _editOperations );
			shader.Attributes.Set( "VoxelEditIndices", _editIndices );
		}
	}

	private static GpuBuffer<uint> CreateLookupBuffer()
	{
		var values = new uint[VoxelTransvoxelTransitionTables.CellClass.Length + VoxelTransvoxelTransitionTables.GeometryCounts.Length + VoxelTransvoxelTransitionTables.TriangleIndices.Length + VoxelTransvoxelTransitionTables.VertexData.Length];
		var offset = 0;
		foreach ( var value in VoxelTransvoxelTransitionTables.CellClass ) values[offset++] = value;
		foreach ( var value in VoxelTransvoxelTransitionTables.GeometryCounts ) values[offset++] = value;
		foreach ( var value in VoxelTransvoxelTransitionTables.TriangleIndices ) values[offset++] = value;
		foreach ( var value in VoxelTransvoxelTransitionTables.VertexData ) values[offset++] = value;
		var buffer = new GpuBuffer<uint>( values.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Lookup" );
		buffer.SetData( values );
		return buffer;
	}

	public void Dispose()
	{
		lock ( _stateLock ) _disposed = true;
		_requests?.Dispose(); _samples?.Dispose(); _lookup?.Dispose(); _countResults?.Dispose(); _allocations?.Dispose(); _editOperations?.Dispose(); _editIndices?.Dispose();
	}

	private enum ArenaState { Idle, CountSubmitted, CountReady, EmitReady }
}
