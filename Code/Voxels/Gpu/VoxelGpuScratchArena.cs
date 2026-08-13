internal sealed class VoxelGpuScratchArena : System.IDisposable
{
	public const int MaximumBatchSize = 128;
	public const int RingSize = 2;
	private const int StatisticsCount = 10;
	private readonly object _stateLock = new();
	private readonly ComputeShader _clear;
	private readonly ComputeShader _density;
	private readonly ComputeShader _classify;
	private readonly ComputeShader _scan;
	private readonly ComputeShader _totals;
	private readonly ComputeShader _emitVertices;
	private readonly ComputeShader _emitIndices;
	private readonly GpuBuffer<VoxelGpuBlockRequest> _requests;
	private readonly GpuBuffer<float> _densitySamples;
	private readonly GpuBuffer<uint> _regularLookup;
	private readonly GpuBuffer<GpuCellData> _cells;
	private readonly GpuBuffer<uint> _edgeFlags;
	private readonly GpuBuffer<uint> _edgeVertexIds;
	private readonly GpuBuffer<uint> _statistics;
	private readonly GpuBuffer<uint> _edgeGroupSums;
	private readonly GpuBuffer<uint> _cellGroupSums;
	private readonly GpuBuffer<uint> _blockCounts;
	private readonly GpuBuffer<uint> _unusedDrawCounter;
	private readonly GpuBuffer<VoxelGpuCountResult> _countResults;
	private readonly GpuBuffer<VoxelGpuAllocationDescriptor> _allocations;
	private readonly GpuBuffer<VoxelGpuEditOp> _editOperations;
	private readonly GpuBuffer<uint> _editIndices;
	private readonly GpuBuffer<VoxelGpuEditBrick> _bakedEditBricks;
	private readonly GpuBuffer<float> _bakedEditDensities;
	private readonly int _chunkSize;
	private readonly int _sampleSize;
	private readonly int _haloSize;
	private readonly int _haloSampleCount;
	private readonly int _cellCount;
	private readonly int _edgeSlotCount;
	private readonly int _edgeGroupCount;
	private readonly int _cellGroupCount;
	private readonly int _regularGeometryCountsOffset;
	private readonly int _regularTriangleIndicesOffset;
	private readonly int _regularVertexDataOffset;
	private readonly float _voxelSize;
	private readonly float _sdfClampDistance;
	private readonly float _simplexFrequency;
	private readonly float _simplexAmplitude;
	private readonly float _simplexBaseHeight;
	private readonly int _simplexSeed;
	private VoxelGpuCountResult[] _completedCounts;
	private readonly VoxelGpuCountResult[] _completedCountBuffer = new VoxelGpuCountResult[MaximumBatchSize];
	private int _completedCount;
	private long _readbackTimestamp;
	private int _batchSize;
	private int _bakedEditBrickCount;
	private ArenaState _state;
	private bool _disposed;

	public long CapacityBytes { get; }
	public bool IsIdle { get { lock ( _stateLock ) return _state == ArenaState.Idle; } }

	public VoxelGpuScratchArena( int chunkSize, float voxelSize, float sdfClampDistance, float simplexFrequency, float simplexAmplitude, float simplexBaseHeight, int simplexSeed )
	{
		_chunkSize = chunkSize;
		_voxelSize = voxelSize;
		_sdfClampDistance = sdfClampDistance;
		_simplexFrequency = simplexFrequency;
		_simplexAmplitude = simplexAmplitude;
		_simplexBaseHeight = simplexBaseHeight;
		_simplexSeed = simplexSeed;
		_sampleSize = checked( chunkSize + 1 );
		_haloSize = checked( chunkSize + 3 );
		_haloSampleCount = checked( _haloSize * _haloSize * _haloSize );
		_cellCount = checked( chunkSize * chunkSize * chunkSize );
		_edgeSlotCount = checked( _sampleSize * _sampleSize * _sampleSize * 3 );
		_edgeGroupCount = (_edgeSlotCount + 255) / 256;
		_cellGroupCount = (_cellCount + 255) / 256;
		_regularGeometryCountsOffset = VoxelTransvoxelTables.RegularCellClass.Length;
		_regularTriangleIndicesOffset = _regularGeometryCountsOffset + VoxelTransvoxelTables.RegularGeometryCounts.Length;
		_regularVertexDataOffset = _regularTriangleIndicesOffset + VoxelTransvoxelTables.RegularTriangleIndices.Length;

		_requests = new GpuBuffer<VoxelGpuBlockRequest>( MaximumBatchSize, GpuBuffer.UsageFlags.Structured, "Voxel GPU Batch Requests" );
		_densitySamples = new GpuBuffer<float>( checked( _haloSampleCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Batch Density" );
		_regularLookup = CreateRegularLookupBuffer();
		_cells = new GpuBuffer<GpuCellData>( checked( _cellCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Batch Cells" );
		_edgeFlags = new GpuBuffer<uint>( checked( _edgeSlotCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Batch Edge Flags" );
		_edgeVertexIds = new GpuBuffer<uint>( checked( _edgeSlotCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Batch Edge IDs" );
		_statistics = new GpuBuffer<uint>( StatisticsCount, GpuBuffer.UsageFlags.Structured, "Voxel GPU Batch Statistics" );
		_edgeGroupSums = new GpuBuffer<uint>( checked( _edgeGroupCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Batch Edge Groups" );
		_cellGroupSums = new GpuBuffer<uint>( checked( _cellGroupCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Batch Cell Groups" );
		_blockCounts = new GpuBuffer<uint>( MaximumBatchSize * 2, GpuBuffer.UsageFlags.Structured, "Voxel GPU Batch Counts" );
		_unusedDrawCounter = new GpuBuffer<uint>( 1, GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.Append, "Voxel GPU Unused Publication Counter" );
		_countResults = new GpuBuffer<VoxelGpuCountResult>( MaximumBatchSize, GpuBuffer.UsageFlags.Structured, "Voxel GPU Count Results" );
		_allocations = new GpuBuffer<VoxelGpuAllocationDescriptor>( MaximumBatchSize, GpuBuffer.UsageFlags.Structured, "Voxel GPU Batch Allocations" );
		_editOperations = new GpuBuffer<VoxelGpuEditOp>( VoxelEditJournal.MaximumGpuOperations, GpuBuffer.UsageFlags.Structured, "Voxel GPU Active Edits" );
		_editIndices = new GpuBuffer<uint>( MaximumBatchSize * VoxelEditJournal.MaximumGpuOperations, GpuBuffer.UsageFlags.Structured, "Voxel GPU Batch Edit Indices" );
		_bakedEditBricks = new GpuBuffer<VoxelGpuEditBrick>( VoxelEditBrickStore.MaximumBakedBricks, GpuBuffer.UsageFlags.Structured, "Voxel GPU Baked Edit Bricks" );
		_bakedEditDensities = new GpuBuffer<float>( checked( VoxelEditBrickStore.MaximumBakedBricks * VoxelEditBrick.SampleCount ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Baked Edit Densities" );

		_clear = new ComputeShader( "shaders/voxel_gpu_count_clear_cs.shader" );
		_density = new ComputeShader( "shaders/voxel_gpu_density_material_v1_cs.shader" );
		_classify = new ComputeShader( "shaders/voxel_gpu_classify_regular_cs.shader" );
		_scan = new ComputeShader( "shaders/voxel_gpu_scan_regular_cs.shader" );
		_totals = new ComputeShader( "shaders/voxel_gpu_block_totals_v1_cs.shader" );
		_emitVertices = new ComputeShader( "shaders/voxel_gpu_emit_vertices_v1_cs.shader" );
		_emitIndices = new ComputeShader( "shaders/voxel_gpu_emit_indices_v1_cs.shader" );
		BindCommonAttributes();

		CapacityBytes =
			(long)MaximumBatchSize * 64 +
			(long)_haloSampleCount * MaximumBatchSize * sizeof( float ) +
			(long)_cellCount * MaximumBatchSize * 12 +
			(long)_edgeSlotCount * MaximumBatchSize * sizeof( uint ) * 2 +
			(long)(_edgeGroupCount + _cellGroupCount) * MaximumBatchSize * sizeof( uint ) +
			(long)MaximumBatchSize * (sizeof( uint ) * 2 + 32 + 64) +
			(long)VoxelEditJournal.MaximumGpuOperations * 80 +
			(long)MaximumBatchSize * VoxelEditJournal.MaximumGpuOperations * sizeof( uint ) +
			(long)VoxelEditBrickStore.MaximumBakedBricks * 32 +
			(long)VoxelEditBrickStore.MaximumBakedBricks * VoxelEditBrick.SampleCount * sizeof( float ) +
			(long)_regularLookup.ElementCount * sizeof( uint );
	}

	public void SetEditOperations( VoxelGpuEditOp[] operations, int count )
	{
		if ( operations is null || count < 0 || count > VoxelEditJournal.MaximumGpuOperations || operations.Length < System.Math.Max( 1, count ) ) throw new System.ArgumentOutOfRangeException( nameof( count ) );
		if ( count > 0 ) _editOperations.SetData( new System.Span<VoxelGpuEditOp>( operations, 0, count ) );
	}

	public void SetBakedEditBricks( VoxelGpuEditBrick[] bricks, int count, float[] densities )
	{
		if ( bricks is null || count < 0 || count > VoxelEditBrickStore.MaximumBakedBricks || bricks.Length < System.Math.Max( 1, count ) ) throw new System.ArgumentOutOfRangeException( nameof( count ) );
		var requiredSamples = checked( System.Math.Max( 1, count * VoxelEditBrick.SampleCount ) );
		if ( densities is null || densities.Length < requiredSamples ) throw new System.ArgumentException( "Baked edit density data is smaller than the submitted brick set.", nameof( densities ) );
		if ( count > 0 ) _bakedEditBricks.SetData( new System.Span<VoxelGpuEditBrick>( bricks, 0, count ) );
		if ( count > 0 ) _bakedEditDensities.SetData( new System.Span<float>( densities, 0, requiredSamples ) );
		_bakedEditBrickCount = count;
		_density.Attributes.Set( "VoxelEditBrickCount", count );
	}

	public bool TrySubmitCount( VoxelGpuBlockRequest[] requests, int count, uint[] editIndices, int editIndexCount, out double submissionMilliseconds )
	{
		lock ( _stateLock )
		{
			if ( _disposed || _state != ArenaState.Idle || requests is null || count is < 1 or > MaximumBatchSize || requests.Length < count || editIndices is null || editIndexCount < 0 || editIndexCount > editIndices.Length || editIndexCount > _editIndices.ElementCount )
			{
				submissionMilliseconds = 0.0;
				return false;
			}
			_state = ArenaState.CountSubmitted;
			_batchSize = count;
		}

		var start = System.Diagnostics.Stopwatch.GetTimestamp();
		_requests.SetData( new System.Span<VoxelGpuBlockRequest>( requests, 0, count ) );
		if ( editIndexCount > 0 ) _editIndices.SetData( new System.Span<uint>( editIndices, 0, editIndexCount ) );
		SetBatchSize( count );
		_statistics.Clear();
		Graphics.ResourceBarrierTransition( _editOperations, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		Graphics.ResourceBarrierTransition( _editIndices, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		Graphics.ResourceBarrierTransition( _bakedEditBricks, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		Graphics.ResourceBarrierTransition( _bakedEditDensities, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		Graphics.ResourceBarrierTransition( _densitySamples, Sandbox.Rendering.ResourceState.UnorderedAccess );
		Graphics.ResourceBarrierTransition( _regularLookup, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		foreach ( var buffer in new GpuBuffer[] { _cells, _edgeFlags, _edgeVertexIds, _statistics, _edgeGroupSums, _cellGroupSums, _blockCounts, _countResults } )
			Graphics.ResourceBarrierTransition( buffer, Sandbox.Rendering.ResourceState.UnorderedAccess );
		_clear.Attributes.Set( "AllocationPass", 0 );
		_clear.Dispatch( System.Math.Max( _cellCount, _edgeSlotCount ) * count, 1, 1 );
		Barrier( _cells, _edgeFlags, _edgeVertexIds );
		_density.Dispatch( _haloSize * _haloSize * count, 1, 1 );
		Barrier( _densitySamples );
		_classify.Attributes.Set( "PublicationPass", 0 );
		_classify.Dispatch( _cellCount * count, 1, 1 );
		Barrier( _cells, _edgeFlags, _statistics );
		_scan.Attributes.Set( "ScanPass", 0 );
		_scan.Dispatch( _edgeGroupCount * 256 * count, 1, 1 );
		Barrier( _edgeVertexIds, _edgeGroupSums );
		_scan.Attributes.Set( "ScanPass", 1 );
		_scan.Dispatch( _cellGroupCount * 256 * count, 1, 1 );
		Barrier( _cells, _cellGroupSums );
		_scan.Attributes.Set( "ScanPass", 2 );
		_scan.Dispatch( 256 * count, 1, 1 );
		Barrier( _edgeGroupSums, _cellGroupSums, _blockCounts );
		_totals.Dispatch( count, 1, 1 );
		Barrier( _countResults );
		_readbackTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		_countResults.GetDataAsync( OnCountsRead, 0, count );
		submissionMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( start ).TotalMilliseconds;
		return true;
	}

	public bool TryTakeCounts( out VoxelGpuCountResult[] counts, out int count, out double readbackMilliseconds )
	{
		lock ( _stateLock )
		{
			if ( _state != ArenaState.CountReady )
			{
				counts = null;
				count = 0;
				readbackMilliseconds = 0.0;
				return false;
			}
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
			if ( _disposed || _state != ArenaState.EmitReady || allocations is null || count < 1 || count > _batchSize || allocations.Length < _batchSize )
			{
				submissionMilliseconds = 0.0;
				return false;
			}
			_state = ArenaState.Idle;
		}

		var start = System.Diagnostics.Stopwatch.GetTimestamp();
		_allocations.SetData( new System.Span<VoxelGpuAllocationDescriptor>( allocations, 0, _batchSize ) );
		Graphics.ResourceBarrierTransition( pool.Vertices, Sandbox.Rendering.ResourceState.UnorderedAccess );
		Graphics.ResourceBarrierTransition( pool.Indices, Sandbox.Rendering.ResourceState.UnorderedAccess );
		_emitVertices.Attributes.Set( "OutputVertices", pool.Vertices );
		_emitIndices.Attributes.Set( "OutputVertices", pool.Vertices );
		_emitIndices.Attributes.Set( "OutputIndices", pool.Indices );
		_emitVertices.Dispatch( _edgeSlotCount * _batchSize, 1, 1 );
		Barrier( pool.Vertices );
		_emitIndices.Dispatch( _cellCount * _batchSize, 1, 1 );
		Barrier( pool.Indices );
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

	private void BindCommonAttributes()
	{
		foreach ( var shader in new[] { _clear, _density, _classify, _scan, _totals, _emitVertices, _emitIndices } )
		{
			shader.Attributes.Set( "BlockRequests", _requests );
			shader.Attributes.Set( "DensitySamples", _densitySamples );
			shader.Attributes.Set( "RegularLookup", _regularLookup );
			shader.Attributes.Set( "Cells", _cells );
			shader.Attributes.Set( "EdgeFlags", _edgeFlags );
			shader.Attributes.Set( "EdgeVertexIds", _edgeVertexIds );
			shader.Attributes.Set( "Statistics", _statistics );
			shader.Attributes.Set( "EdgeGroupSums", _edgeGroupSums );
			shader.Attributes.Set( "CellGroupSums", _cellGroupSums );
			shader.Attributes.Set( "BlockCounts", _blockCounts );
			shader.Attributes.Set( "CountResults", _countResults );
			shader.Attributes.Set( "DrawIndexCounter", _unusedDrawCounter );
			shader.Attributes.Set( "Allocations", _allocations );
			shader.Attributes.Set( "VoxelEditBricks", _bakedEditBricks );
			shader.Attributes.Set( "VoxelEditBrickDensities", _bakedEditDensities );
			shader.Attributes.Set( "VoxelEditBrickCount", _bakedEditBrickCount );
			shader.Attributes.Set( "VoxelEditBrickSize", VoxelEditBrickStore.BrickSize );
			shader.Attributes.Set( "ChunkSize", _chunkSize );
			shader.Attributes.Set( "SampleSize", _sampleSize );
			shader.Attributes.Set( "HaloSize", _haloSize );
			shader.Attributes.Set( "HaloSampleCount", _haloSampleCount );
			shader.Attributes.Set( "CellCount", _cellCount );
			shader.Attributes.Set( "EdgeSlotCount", _edgeSlotCount );
			shader.Attributes.Set( "EdgeGroupCount", _edgeGroupCount );
			shader.Attributes.Set( "CellGroupCount", _cellGroupCount );
			shader.Attributes.Set( "RegularGeometryCountsOffset", _regularGeometryCountsOffset );
			shader.Attributes.Set( "RegularTriangleIndicesOffset", _regularTriangleIndicesOffset );
			shader.Attributes.Set( "RegularVertexDataOffset", _regularVertexDataOffset );
			shader.Attributes.Set( "VoxelSize", _voxelSize );
			shader.Attributes.Set( "SdfClampDistance", GetGpuInterpolationClampDistance() );
			shader.Attributes.Set( "DescriptorOffset", MaximumBatchSize * 2 );
			shader.Attributes.Set( "TotalsOffset", MaximumBatchSize * 6 );
		}
		_density.Attributes.Set( "SimplexFrequency", _simplexFrequency );
		_density.Attributes.Set( "SimplexAmplitude", _simplexAmplitude );
		_density.Attributes.Set( "SimplexBaseHeight", _simplexBaseHeight );
		_density.Attributes.Set( "SimplexSeed", _simplexSeed );
		_density.Attributes.Set( "VoxelEditOperations", _editOperations );
		_density.Attributes.Set( "VoxelEditIndices", _editIndices );
	}

	private float GetGpuInterpolationClampDistance() =>
		_sdfClampDistance * (1 << VoxelClipboxConfig.MaximumLevelCount);

	private void SetBatchSize( int batchSize )
	{
		foreach ( var shader in new[] { _clear, _density, _classify, _scan, _totals, _emitVertices, _emitIndices } ) shader.Attributes.Set( "BatchSize", batchSize );
	}

	private static void Barrier( params GpuBuffer[] buffers )
	{
		foreach ( var buffer in buffers ) Graphics.UavBarrier( buffer );
	}

	private static GpuBuffer<uint> CreateRegularLookupBuffer()
	{
		var expanded = new uint[VoxelTransvoxelTables.RegularCellClass.Length + VoxelTransvoxelTables.RegularGeometryCounts.Length +
			VoxelTransvoxelTables.RegularTriangleIndices.Length + VoxelTransvoxelTables.RegularVertexData.Length];
		var offset = 0;
		foreach ( var value in VoxelTransvoxelTables.RegularCellClass ) expanded[offset++] = value;
		foreach ( var value in VoxelTransvoxelTables.RegularGeometryCounts ) expanded[offset++] = value;
		foreach ( var value in VoxelTransvoxelTables.RegularTriangleIndices ) expanded[offset++] = value;
		foreach ( var value in VoxelTransvoxelTables.RegularVertexData ) expanded[offset++] = value;
		var buffer = new GpuBuffer<uint>( expanded.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU Regular Lookup" );
		buffer.SetData( expanded );
		return buffer;
	}

	public void Dispose()
	{
		lock ( _stateLock )
		{
			if ( _disposed ) return;
			_disposed = true;
		}
		_requests?.Dispose(); _densitySamples?.Dispose(); _regularLookup?.Dispose(); _cells?.Dispose(); _edgeFlags?.Dispose();
		_edgeVertexIds?.Dispose(); _statistics?.Dispose(); _edgeGroupSums?.Dispose(); _cellGroupSums?.Dispose(); _blockCounts?.Dispose();
		_unusedDrawCounter?.Dispose(); _countResults?.Dispose(); _allocations?.Dispose(); _editOperations?.Dispose(); _editIndices?.Dispose(); _bakedEditBricks?.Dispose(); _bakedEditDensities?.Dispose();
	}

	private enum ArenaState { Idle, CountSubmitted, CountReady, EmitReady }
	private struct GpuCellData
	{
		public uint Case;
		public uint IndexCount;
		public uint IndexOffset;

		public GpuCellData( uint caseCode, uint indexCount, uint indexOffset )
		{
			Case = caseCode;
			IndexCount = indexCount;
			IndexOffset = indexOffset;
		}
	}
}
