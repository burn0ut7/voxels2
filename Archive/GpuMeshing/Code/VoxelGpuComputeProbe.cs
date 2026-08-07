internal readonly struct VoxelGpuTimingReport
{
	public System.TimeSpan ClassifyFenceLatency { get; }
	public System.TimeSpan ScanFenceLatency { get; }
	public System.TimeSpan EmitStatisticsLatency { get; }
	public System.TimeSpan VertexReadbackLatency { get; }
	public System.TimeSpan TotalLatency { get; }

	public VoxelGpuTimingReport(
		System.TimeSpan classifyFenceLatency,
		System.TimeSpan scanFenceLatency,
		System.TimeSpan emitStatisticsLatency,
		System.TimeSpan vertexReadbackLatency,
		System.TimeSpan totalLatency )
	{
		ClassifyFenceLatency = classifyFenceLatency;
		ScanFenceLatency = scanFenceLatency;
		EmitStatisticsLatency = emitStatisticsLatency;
		VertexReadbackLatency = vertexReadbackLatency;
		TotalLatency = totalLatency;
	}
}

internal readonly struct VoxelGpuComputeResult
{
	public uint VertexCount { get; }
	public uint IndexCount { get; }
	public uint InstanceCount { get; }
	public uint CellsProcessed { get; }
	public uint ActiveCells { get; }
	public uint TrianglesEmitted { get; }
	public uint DegeneratesRejected { get; }
	public uint BufferOverflowAttempts { get; }
	public Vector3[] CollisionVertices { get; }
	public int[] CollisionIndices { get; }
	public uint SliverTriangles { get; }
	public VoxelGpuTimingReport Timing { get; }

	public VoxelGpuComputeResult(
		uint vertexCount,
		uint indexCount,
		uint instanceCount,
		uint cellsProcessed,
		uint activeCells,
		uint trianglesEmitted,
		uint degeneratesRejected,
		uint bufferOverflowAttempts,
		Vector3[] collisionVertices = null,
		int[] collisionIndices = null,
		uint sliverTriangles = 0,
		VoxelGpuTimingReport timing = default )
	{
		VertexCount = vertexCount;
		IndexCount = indexCount;
		InstanceCount = instanceCount;
		CellsProcessed = cellsProcessed;
		ActiveCells = activeCells;
		TrianglesEmitted = trianglesEmitted;
		DegeneratesRejected = degeneratesRejected;
		BufferOverflowAttempts = bufferOverflowAttempts;
		CollisionVertices = collisionVertices;
		CollisionIndices = collisionIndices;
		SliverTriangles = sliverTriangles;
		Timing = timing;
	}
}

internal sealed class VoxelGpuComputeProbe : SceneCustomObject, System.IDisposable
{
	private const string ComputeShaderName = "voxel_gpu_prototype_cs";
	private const int StatisticsCount = 8;

	private readonly ComputeShader _classifyShader;
	private readonly ComputeShader _scanShader;
	private readonly ComputeShader _emitShader;
	private readonly ComputeShader _topologyShader;
	private GpuBuffer<float> _sdfSamples;
	private readonly GpuBuffer<SimpleVertex>[] _vertexBuffers = new GpuBuffer<SimpleVertex>[2];
	private readonly GpuBuffer<uint>[] _indexBuffers = new GpuBuffer<uint>[2];
	private readonly int[] _vertexBufferCapacities = new int[2];
	private readonly int[] _indexBufferCapacities = new int[2];
	private GpuBuffer<uint> _statistics;
	private GpuBuffer<uint> _cellTriangleCounts;
	private GpuBuffer<uint> _cellVertexOffsets;
	private GpuBuffer<uint> _edgeFlags;
	private GpuBuffer<uint> _edgeVertexIds;
	private GpuBuffer<CellSelection> _cellSelections;
	private GpuBuffer<uint> _cellCenterIds;
	private GpuBuffer<uint> _lutValues;
	private GpuBuffer<uint> _lutMetadata;
	private readonly GpuBuffer<GpuBuffer.IndirectDrawArguments>[] _indirectArgumentBuffers = new GpuBuffer<GpuBuffer.IndirectDrawArguments>[2];
	private readonly List<RetiredVertexBuffer> _retiredVertexBuffers = new();
	private readonly List<RetiredIndexBuffer> _retiredIndexBuffers = new();
	private readonly List<BufferReuseFence> _bufferReuseFences = new();
	private readonly Sandbox.Rendering.CommandList[] _drawCommands = new Sandbox.Rendering.CommandList[2];
	private readonly bool[] _vertexBufferReusable = new bool[2];
	private readonly object _resultLock = new();
	private readonly int _chunkSize;
	private readonly int _maximumVertexCount;
	private readonly int _maximumIndexCount;
	private readonly long _computeWorkingSetBytes;
	private readonly float[] _sdfUpload;
	private readonly bool _readBackVertices;
	private readonly Vector3 _worldOrigin;
	private readonly float _voxelSize;
	private bool _computeWorkingSetAllocated;
	private int _activeVertexBufferIndex = -1;
	private int _generationVertexBufferIndex;
	private int _requiredVertexCount;
	private int _requiredIndexCount;
	private int _outputBufferAllocationCount;
	private int _retiredVertexCapacity;
	private int _retiredIndexCapacity;
	private int _pendingVertexReadbackCount;
	private long _pipelineStartTimestamp;
	private long _phaseStartTimestamp;
	private System.TimeSpan _classifyFenceLatency;
	private System.TimeSpan _scanFenceLatency;
	private System.TimeSpan _emitStatisticsLatency;
	private System.TimeSpan _vertexReadbackLatency;
	private CameraComponent _camera;
	private ProbeState _state;
	private VoxelGpuComputeResult _completedResult;
	private bool _hasCompletedResult;
	private bool _disposed;

	public bool IsDrawing => !_disposed && _activeVertexBufferIndex >= 0 && _drawCommands[_activeVertexBufferIndex] is not null;
	public int VertexCapacity => _vertexBufferCapacities[0] + _vertexBufferCapacities[1];
	public int IndexCapacity => _indexBufferCapacities[0] + _indexBufferCapacities[1];
	public int RetiredVertexCapacity => _retiredVertexCapacity;
	public int RetiredIndexCapacity => _retiredIndexCapacity;
	public int OutputBufferAllocationCount => _outputBufferAllocationCount;
	public long EstimatedGpuBufferBytes => (_computeWorkingSetAllocated ? _computeWorkingSetBytes : 0) + 32 +
		(long)(VertexCapacity + RetiredVertexCapacity) * 44 +
		(long)(IndexCapacity + RetiredIndexCapacity) * sizeof( uint );

	public VoxelGpuComputeProbe(
		SceneWorld sceneWorld,
		VoxelChunk chunk,
		Vector3 worldOrigin,
		float voxelSize,
		int maximumVertexCount,
		bool readBackVertices = false )
		: this( sceneWorld, CreateClampedHalo( CopyDistances( chunk ), chunk.Size ), chunk.Size, worldOrigin, voxelSize, maximumVertexCount, readBackVertices )
	{
	}

	public VoxelGpuComputeProbe(
		SceneWorld sceneWorld,
		float[] distances,
		int chunkSize,
		Vector3 worldOrigin,
		float voxelSize,
		int maximumVertexCount,
		bool readBackVertices = false )
		: base( sceneWorld )
	{
		if ( chunkSize < 1 )
		{
			throw new System.ArgumentOutOfRangeException( nameof( chunkSize ) );
		}

		var sampleSize = chunkSize + 1;
		var haloSize = chunkSize + 3;
		var coreSampleCount = checked( sampleSize * sampleSize * sampleSize );
		if ( distances is not null && distances.Length == coreSampleCount )
		{
			distances = CreateClampedHalo( distances, chunkSize );
		}
		var expectedSampleCount = checked( haloSize * haloSize * haloSize );
		if ( distances is null || distances.Length != expectedSampleCount )
		{
			throw new System.ArgumentException( $"Expected {expectedSampleCount:N0} SDF samples for chunk size {chunkSize:N0}.", nameof( distances ) );
		}

		_chunkSize = chunkSize;
		_readBackVertices = readBackVertices;
		_worldOrigin = worldOrigin;
		_voxelSize = voxelSize;
		_sdfUpload = new float[distances.Length];
		distances.CopyTo( _sdfUpload, 0 );
		maximumVertexCount = System.Math.Max( maximumVertexCount, 3 );
		var cellCount = checked( chunkSize * chunkSize * chunkSize );
		var edgeSlotCount = checked( sampleSize * sampleSize * sampleSize * 3 );
		_maximumVertexCount = maximumVertexCount;
		_maximumIndexCount = maximumVertexCount;
		_computeWorkingSetBytes =
			(long)distances.Length * sizeof( float ) +
			(long)cellCount * (sizeof( uint ) * 2 + 16 + sizeof( uint )) +
			(long)edgeSlotCount * sizeof( uint ) * 2 +
			StatisticsCount * sizeof( uint );

		_classifyShader = new ComputeShader( ComputeShaderName );
		_scanShader = new ComputeShader( ComputeShaderName );
		_emitShader = new ComputeShader( ComputeShaderName );
		_topologyShader = new ComputeShader( ComputeShaderName );
		AllocateVertexBuffer( 0, 3 );
		AllocateIndexBuffer( 0, 3 );
		for ( var index = 0; index < _indirectArgumentBuffers.Length; index++ )
		{
			_indirectArgumentBuffers[index] = new GpuBuffer<GpuBuffer.IndirectDrawArguments>(
				1,
				GpuBuffer.UsageFlags.IndirectDrawArguments,
				$"Voxel GPU Chunk Indirect Arguments {index}"
			);
		}

		AllocateComputeWorkingSet();

		// RenderSceneObject owns the compute/readback scheduling callbacks. Keep the scheduler
		// visible independently of the generated chunk so off-camera chunks still become
		// collision-ready and buffer reuse fences can complete.
		Bounds = BBox.FromPositionAndSize( worldOrigin, Vector3.One * 1_000_000_000.0f );
	}

	public void Run()
	{
		if ( _disposed || _state != ProbeState.Idle )
		{
			return;
		}

		ResetTimings();
		_state = ProbeState.DispatchPending;
	}

	public bool TryTakeCompletedResult( out VoxelGpuComputeResult result )
	{
		lock ( _resultLock )
		{
			if ( !_hasCompletedResult )
			{
				result = default;
				return false;
			}

			result = _completedResult;
			_hasCompletedResult = false;
			return true;
		}
	}

	public int ReleaseCompletedResources()
	{
		var completedFenceCount = 0;
		lock ( _resultLock )
		{
			for ( var index = _bufferReuseFences.Count - 1; index >= 0; index-- )
			{
				var fence = _bufferReuseFences[index];
				if ( !fence.Complete )
				{
					continue;
				}

				if ( ReferenceEquals( _vertexBuffers[fence.BufferIndex], fence.Buffer ) )
				{
					_vertexBufferReusable[fence.BufferIndex] = true;
				}
				_bufferReuseFences.RemoveAt( index );
				completedFenceCount++;
			}
		}

		return completedFenceCount;
	}

	public bool TryPreparePendingEmit()
	{
		lock ( _resultLock )
		{
			if ( _disposed || _state != ProbeState.OutputCapacityPending )
			{
				return false;
			}

			_generationVertexBufferIndex = _activeVertexBufferIndex == 0 ? 1 : 0;
			if ( _vertexBuffers[_generationVertexBufferIndex] is not null && !_vertexBufferReusable[_generationVertexBufferIndex] )
			{
				return false;
			}

			EnsureVertexBufferCapacity( _generationVertexBufferIndex, _requiredVertexCount );
			EnsureIndexBufferCapacity( _generationVertexBufferIndex, _requiredIndexCount );
			_emitShader.Attributes.Set( "OutputVertices", _vertexBuffers[_generationVertexBufferIndex] );
			_emitShader.Attributes.Set( "OutputIndices", _indexBuffers[_generationVertexBufferIndex] );
			_topologyShader.Attributes.Set( "OutputVertices", _vertexBuffers[_generationVertexBufferIndex] );
			_topologyShader.Attributes.Set( "OutputIndices", _indexBuffers[_generationVertexBufferIndex] );
			_state = ProbeState.EmitPending;
			return true;
		}
	}

	public bool ActivateIndirectDraw( CameraComponent camera, Material material )
	{
		if ( _disposed || _state != ProbeState.Ready || camera is null || material is null )
		{
			return false;
		}

		var oldActiveIndex = _activeVertexBufferIndex;
		if ( oldActiveIndex >= 0 && _drawCommands[oldActiveIndex] is not null && _camera is not null )
		{
			_camera.RemoveCommandList( _drawCommands[oldActiveIndex] );
			lock ( _resultLock )
			{
				_vertexBufferReusable[oldActiveIndex] = false;
				_bufferReuseFences.Add( new BufferReuseFence( oldActiveIndex, _vertexBuffers[oldActiveIndex] ) );
			}
		}

		_camera = camera;
		var indirectArguments = new GpuBuffer.IndirectDrawArguments[1];
		indirectArguments[0].VertexCount = _completedResult.IndexCount;
		indirectArguments[0].InstanceCount = 1;
		var generatedArguments = _indirectArgumentBuffers[_generationVertexBufferIndex];
		generatedArguments.SetData( new System.ReadOnlySpan<GpuBuffer.IndirectDrawArguments>( indirectArguments ) );
		var drawCommands = _drawCommands[_generationVertexBufferIndex] ??=
			new Sandbox.Rendering.CommandList( $"Voxel GPU Chunk Indirect Draw {_generationVertexBufferIndex}" );
		drawCommands.Reset();
		var generatedVertices = _vertexBuffers[_generationVertexBufferIndex];
		drawCommands.ResourceBarrierTransition( generatedVertices, Sandbox.Rendering.ResourceState.VertexOrIndexBuffer );
		drawCommands.ResourceBarrierTransition( generatedArguments, Sandbox.Rendering.ResourceState.IndirectArgument );
		var generatedIndices = _indexBuffers[_generationVertexBufferIndex];
		drawCommands.ResourceBarrierTransition( generatedIndices, Sandbox.Rendering.ResourceState.VertexOrIndexBuffer );
		drawCommands.DrawIndexedInstancedIndirect(
			generatedVertices,
			generatedIndices,
			material,
			generatedArguments,
			0,
			null,
			Graphics.PrimitiveType.Triangles
		);
		_camera.AddCommandList( drawCommands, Sandbox.Rendering.Stage.AfterOpaque );
		_activeVertexBufferIndex = _generationVertexBufferIndex;
		_vertexBufferReusable[_activeVertexBufferIndex] = false;
		return true;
	}

	public bool TryRegenerate( VoxelChunk chunk )
	{
		if ( _disposed || _state != ProbeState.Ready || chunk is null || chunk.Size != _chunkSize )
		{
			return false;
		}

		var halo = CreateClampedHalo( CopyDistances( chunk ), chunk.Size );
		halo.CopyTo( _sdfUpload, 0 );

		return ScheduleRegeneration();
	}

	public bool TryRegenerate( float[] distances )
	{
		if ( distances is not null && distances.Length != _sdfUpload.Length )
		{
			distances = CreateClampedHalo( distances, _chunkSize );
		}
		if ( _disposed || _state != ProbeState.Ready || distances is null || distances.Length != _sdfUpload.Length )
		{
			return false;
		}

		distances.CopyTo( _sdfUpload, 0 );
		return ScheduleRegeneration();
	}

	private bool ScheduleRegeneration()
	{
		AllocateComputeWorkingSet();
		_sdfSamples.SetData( new System.ReadOnlySpan<float>( _sdfUpload ) );
		lock ( _resultLock )
		{
			_hasCompletedResult = false;
		}

		_state = ProbeState.DispatchPending;
		ResetTimings();
		return true;
	}

	public bool TryReleaseComputeWorkingSet()
	{
		// Output draw buffers remain resident; only transient classify/scan/emit storage is released.
		lock ( _resultLock )
		{
			if ( _disposed || _state != ProbeState.Ready || _hasCompletedResult )
			{
				return false;
			}

			ReleaseComputeWorkingSet();
			return true;
		}
	}

	private void AllocateComputeWorkingSet()
	{
		if ( _computeWorkingSetAllocated )
		{
			return;
		}

		_computeWorkingSetAllocated = true;
		try
		{
			var sampleSize = _chunkSize + 1;
			var haloSize = _chunkSize + 3;
			var cellCount = checked( _chunkSize * _chunkSize * _chunkSize );
			var edgeSlotCount = checked( sampleSize * sampleSize * sampleSize * 3 );
			_sdfSamples = new GpuBuffer<float>( _sdfUpload.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU Chunk SDF" );
			_statistics = new GpuBuffer<uint>( StatisticsCount, GpuBuffer.UsageFlags.Structured, "Voxel GPU Chunk Statistics" );
			_cellTriangleCounts = new GpuBuffer<uint>( cellCount, GpuBuffer.UsageFlags.Structured, "Voxel GPU Cell Triangle Counts" );
			_cellVertexOffsets = new GpuBuffer<uint>( cellCount, GpuBuffer.UsageFlags.Structured, "Voxel GPU Cell Vertex Offsets" );
			_edgeFlags = new GpuBuffer<uint>( edgeSlotCount, GpuBuffer.UsageFlags.Structured, "Voxel GPU Edge Flags" );
			_edgeVertexIds = new GpuBuffer<uint>( edgeSlotCount, GpuBuffer.UsageFlags.Structured, "Voxel GPU Edge Vertex IDs" );
			_cellSelections = new GpuBuffer<CellSelection>( cellCount, GpuBuffer.UsageFlags.Structured, "Voxel GPU MC33 Cell Selections" );
			_cellCenterIds = new GpuBuffer<uint>( cellCount, GpuBuffer.UsageFlags.Structured, "Voxel GPU Cell Center IDs" );
			var lutValues = VoxelMc33Tables.DecodeValues();
			var lutMetadata = VoxelMc33Tables.DecodeMetadata();
			_lutValues = new GpuBuffer<uint>( lutValues.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU MC33 LUT Values" );
			_lutMetadata = new GpuBuffer<uint>( lutMetadata.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU MC33 LUT Metadata" );
			_lutValues.SetData( lutValues );
			_lutMetadata.SetData( lutMetadata );
			_sdfSamples.SetData( new System.ReadOnlySpan<float>( _sdfUpload ) );
			BindShader( _classifyShader, 0, _worldOrigin, _chunkSize, sampleSize, haloSize, _voxelSize, _maximumVertexCount, cellCount, edgeSlotCount );
			BindShader( _scanShader, 1, _worldOrigin, _chunkSize, sampleSize, haloSize, _voxelSize, _maximumVertexCount, cellCount, edgeSlotCount );
			BindShader( _emitShader, 2, _worldOrigin, _chunkSize, sampleSize, haloSize, _voxelSize, _maximumVertexCount, cellCount, edgeSlotCount );
			BindShader( _topologyShader, 3, _worldOrigin, _chunkSize, sampleSize, haloSize, _voxelSize, _maximumVertexCount, cellCount, edgeSlotCount );
		}
		catch
		{
			ReleaseComputeWorkingSet();
			throw;
		}
	}

	private void ReleaseComputeWorkingSet()
	{
		if ( !_computeWorkingSetAllocated )
		{
			return;
		}

		_sdfSamples?.Dispose();
		_statistics?.Dispose();
		_cellTriangleCounts?.Dispose();
		_cellVertexOffsets?.Dispose();
		_edgeFlags?.Dispose();
		_edgeVertexIds?.Dispose();
		_cellSelections?.Dispose();
		_cellCenterIds?.Dispose();
		_lutValues?.Dispose();
		_lutMetadata?.Dispose();
		_sdfSamples = null;
		_statistics = null;
		_cellTriangleCounts = null;
		_cellVertexOffsets = null;
		_edgeFlags = null;
		_edgeVertexIds = null;
		_cellSelections = null;
		_cellCenterIds = null;
		_lutValues = null;
		_lutMetadata = null;
		_computeWorkingSetAllocated = false;
	}

	public override void RenderSceneObject()
	{
		if ( _disposed )
		{
			return;
		}

		ScheduleBufferReuseFence();

		if ( _state == ProbeState.VertexReadbackPending )
		{
			_state = ProbeState.VertexReadbackInFlight;
			_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
			_vertexBuffers[_generationVertexBufferIndex].GetDataAsync<SimpleVertex>( OnVerticesRead, 0, _pendingVertexReadbackCount );
			return;
		}
		if ( _state == ProbeState.IndexReadbackPending )
		{
			if ( _indexBuffers[_generationVertexBufferIndex] is null )
			{
				_state = ProbeState.Failed;
				SetFailedResult();
				return;
			}
			_state = ProbeState.IndexReadbackInFlight;
			_indexBuffers[_generationVertexBufferIndex].GetDataAsync<uint>( OnIndicesRead, 0, _requiredIndexCount );
			return;
		}

		try
		{
			switch ( _state )
			{
				case ProbeState.DispatchPending:
					_pipelineStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
					_phaseStartTimestamp = _pipelineStartTimestamp;
					_statistics.SetData( new uint[StatisticsCount] );
					_classifyShader.Dispatch( _chunkSize + 1, _chunkSize + 1, _chunkSize + 1 );
					_state = ProbeState.ClassifyFenceInFlight;
					_statistics.GetDataAsync<uint>( OnClassifyFence );
					break;
				case ProbeState.ScanPending:
					_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
					_scanShader.Dispatch( 1, 1, 1 );
					_state = ProbeState.ScanFenceInFlight;
					_statistics.GetDataAsync<uint>( OnScanFence );
					break;
				case ProbeState.EmitPending:
					_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
					_emitShader.Dispatch( _chunkSize + 1, _chunkSize + 1, _chunkSize + 1 );
					_state = ProbeState.EmitFenceInFlight;
					_statistics.GetDataAsync<uint>( OnEmitFence );
					break;
				case ProbeState.TopologyPending:
					_topologyShader.Dispatch( _chunkSize, _chunkSize, _chunkSize );
					_state = ProbeState.ReadbackInFlight;
					_statistics.GetDataAsync<uint>( OnStatisticsRead );
					break;
			}
		}
		catch ( System.Exception exception )
		{
			_state = ProbeState.Failed;
			Log.Error( $"Voxel GPU flat chunk render stage failed: {exception.Message}" );
			SetFailedResult();
		}
	}

	private void OnClassifyFence( System.ReadOnlySpan<uint> statistics )
	{
		lock ( _resultLock )
		{
			if ( !_disposed && _state == ProbeState.ClassifyFenceInFlight )
			{
				_classifyFenceLatency = System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp );
				_state = ProbeState.ScanPending;
			}
		}
	}

	private void OnScanFence( System.ReadOnlySpan<uint> statistics )
	{
		lock ( _resultLock )
		{
			if ( !_disposed && _state == ProbeState.ScanFenceInFlight )
			{
				_scanFenceLatency = System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp );
				_requiredVertexCount = statistics.Length > 5 ? checked( (int)statistics[5] ) : 0;
				_requiredIndexCount = statistics.Length > 6 ? checked( (int)statistics[6] ) : 0;
				_state = ProbeState.OutputCapacityPending;
			}
		}
	}

	private void OnEmitFence( System.ReadOnlySpan<uint> statistics )
	{
		lock ( _resultLock )
		{
			if ( !_disposed && _state == ProbeState.EmitFenceInFlight )
			{
				_state = ProbeState.TopologyPending;
			}
		}
	}

	public void Dispose()
	{
		if ( _disposed )
		{
			return;
		}

		_disposed = true;
		if ( _activeVertexBufferIndex >= 0 && _drawCommands[_activeVertexBufferIndex] is not null && _camera is not null )
		{
			_camera.RemoveCommandList( _drawCommands[_activeVertexBufferIndex] );
		}

		_camera = null;
		Delete();
		ReleaseComputeWorkingSet();
		foreach ( var vertexBuffer in _vertexBuffers )
		{
			vertexBuffer?.Dispose();
		}
		foreach ( var indexBuffer in _indexBuffers ) indexBuffer?.Dispose();
		foreach ( var indirectArguments in _indirectArgumentBuffers )
		{
			indirectArguments?.Dispose();
		}
		foreach ( var retiredVertexBuffer in _retiredVertexBuffers )
		{
			retiredVertexBuffer.Buffer.Dispose();
		}
		foreach ( var retiredIndexBuffer in _retiredIndexBuffers )
		{
			retiredIndexBuffer.Buffer.Dispose();
		}
		_retiredVertexBuffers.Clear();
		_retiredIndexBuffers.Clear();
		_bufferReuseFences.Clear();
	}

	private void OnStatisticsRead( System.ReadOnlySpan<uint> statistics )
	{
		lock ( _resultLock )
		{
			if ( _disposed )
			{
				return;
			}

			var values = statistics.ToArray();
			_emitStatisticsLatency = System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp );
			var trianglesEmitted = GetStatistic( values, 2 );
			_completedResult = new VoxelGpuComputeResult(
				GetStatistic( values, 5 ),
				GetStatistic( values, 6 ),
				1,
				GetStatistic( values, 0 ),
				GetStatistic( values, 1 ),
				trianglesEmitted,
				GetStatistic( values, 3 ),
				GetStatistic( values, 4 ),
				null,
				null,
				GetStatistic( values, 7 ),
				CreateTimingReport()
			);
			if ( _completedResult.BufferOverflowAttempts != 0 )
			{
				_state = ProbeState.Failed;
				_hasCompletedResult = true;
				return;
			}

			if ( _readBackVertices && _completedResult.VertexCount > 0 )
			{
				_pendingVertexReadbackCount = checked( (int)_completedResult.VertexCount );
				_state = ProbeState.VertexReadbackPending;
				return;
			}

			_state = ProbeState.Ready;
			_hasCompletedResult = true;
		}
	}

	private void OnVerticesRead( System.ReadOnlySpan<SimpleVertex> vertices )
	{
		lock ( _resultLock )
		{
			if ( _disposed )
			{
				return;
			}

			var collisionVertices = new Vector3[vertices.Length];
			_vertexReadbackLatency = System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp );
			for ( var index = 0; index < vertices.Length; index++ )
			{
				collisionVertices[index] = vertices[index].position;
			}

			_completedResult = new VoxelGpuComputeResult(
				_completedResult.VertexCount,
				_completedResult.IndexCount,
				_completedResult.InstanceCount,
				_completedResult.CellsProcessed,
				_completedResult.ActiveCells,
				_completedResult.TrianglesEmitted,
				_completedResult.DegeneratesRejected,
				_completedResult.BufferOverflowAttempts,
				collisionVertices,
				null,
				_completedResult.SliverTriangles,
				CreateTimingReport()
			);
			_state = ProbeState.IndexReadbackPending;
		}
	}

	private void OnIndicesRead( System.ReadOnlySpan<uint> indices )
	{
		lock ( _resultLock )
		{
			if ( _disposed ) return;
			var collisionIndices = new int[indices.Length];
			for ( var index = 0; index < indices.Length; index++ ) collisionIndices[index] = checked( (int)indices[index] );
			_completedResult = new VoxelGpuComputeResult(
				_completedResult.VertexCount, _completedResult.IndexCount, _completedResult.InstanceCount,
				_completedResult.CellsProcessed, _completedResult.ActiveCells, _completedResult.TrianglesEmitted,
				_completedResult.DegeneratesRejected, _completedResult.BufferOverflowAttempts,
				_completedResult.CollisionVertices, collisionIndices, _completedResult.SliverTriangles, CreateTimingReport() );
			_state = ProbeState.Ready;
			_hasCompletedResult = true;
		}
	}

	private void SetFailedResult()
	{
		lock ( _resultLock )
		{
			_completedResult = default;
			_hasCompletedResult = true;
		}
	}

	private static uint GetStatistic( uint[] statistics, int index )
	{
		return statistics is not null && index < statistics.Length ? statistics[index] : 0;
	}

	private void ResetTimings()
	{
		_pipelineStartTimestamp = 0;
		_phaseStartTimestamp = 0;
		_classifyFenceLatency = System.TimeSpan.Zero;
		_scanFenceLatency = System.TimeSpan.Zero;
		_emitStatisticsLatency = System.TimeSpan.Zero;
		_vertexReadbackLatency = System.TimeSpan.Zero;
	}

	private VoxelGpuTimingReport CreateTimingReport()
	{
		var totalLatency = _pipelineStartTimestamp != 0
			? System.Diagnostics.Stopwatch.GetElapsedTime( _pipelineStartTimestamp )
			: System.TimeSpan.Zero;
		return new VoxelGpuTimingReport(
			_classifyFenceLatency,
			_scanFenceLatency,
			_emitStatisticsLatency,
			_vertexReadbackLatency,
			totalLatency
		);
	}

	private static float[] CopyDistances( VoxelChunk chunk )
	{
		var distances = new float[chunk.SampleCount];
		chunk.CopyDistancesTo( distances );
		return distances;
	}

	internal static float[] CreateClampedHalo( float[] distances, int chunkSize )
	{
		var sampleSize = chunkSize + 1;
		var haloSize = chunkSize + 3;
		var halo = new float[checked( haloSize * haloSize * haloSize )];
		for ( var z = -1; z <= chunkSize + 1; z++ )
		for ( var y = -1; y <= chunkSize + 1; y++ )
		for ( var x = -1; x <= chunkSize + 1; x++ )
		{
			var sourceX = System.Math.Clamp( x, 0, chunkSize );
			var sourceY = System.Math.Clamp( y, 0, chunkSize );
			var sourceZ = System.Math.Clamp( z, 0, chunkSize );
			halo[(x + 1) + haloSize * ((y + 1) + haloSize * (z + 1))] =
				distances[sourceX + sampleSize * (sourceY + sampleSize * sourceZ)];
		}
		return halo;
	}

	private void EnsureVertexBufferCapacity( int bufferIndex, int requiredVertexCount )
	{
		requiredVertexCount = System.Math.Clamp( requiredVertexCount, 0, _maximumVertexCount );
		if ( _vertexBuffers[bufferIndex] is not null && _vertexBufferCapacities[bufferIndex] >= requiredVertexCount )
		{
			return;
		}

		var headroom = System.Math.Max( requiredVertexCount / 4, 256 );
		var requestedCapacity = System.Math.Min( (long)requiredVertexCount + headroom, _maximumVertexCount );
		long sizeClass = 256;
		while ( sizeClass < requestedCapacity && sizeClass < _maximumVertexCount )
		{
			sizeClass *= 2;
		}

		var capacity = (int)System.Math.Clamp( sizeClass, 3L, _maximumVertexCount );
		AllocateVertexBuffer( bufferIndex, capacity );
	}

	private void AllocateVertexBuffer( int bufferIndex, int capacity )
	{
		if ( _vertexBuffers[bufferIndex] is not null )
		{
			_drawCommands[bufferIndex]?.Reset();
			_retiredVertexBuffers.Add( new RetiredVertexBuffer(
				_vertexBuffers[bufferIndex],
				_vertexBufferCapacities[bufferIndex]
			) );
			_retiredVertexCapacity += _vertexBufferCapacities[bufferIndex];
		}
		_vertexBuffers[bufferIndex] = new GpuBuffer<SimpleVertex>(
			capacity,
			GpuBuffer.UsageFlags.Vertex | GpuBuffer.UsageFlags.Structured,
			$"Voxel GPU Chunk Vertices {bufferIndex}"
		);
		_vertexBufferCapacities[bufferIndex] = capacity;
		_vertexBufferReusable[bufferIndex] = true;
		_outputBufferAllocationCount++;
	}

	private void EnsureIndexBufferCapacity( int bufferIndex, int requiredIndexCount )
	{
		requiredIndexCount = System.Math.Clamp( requiredIndexCount, 0, _maximumIndexCount );
		if ( _indexBuffers[bufferIndex] is not null && _indexBufferCapacities[bufferIndex] >= requiredIndexCount ) return;
		var capacity = 256L;
		var wanted = System.Math.Min( (long)requiredIndexCount + System.Math.Max( requiredIndexCount / 4, 256 ), _maximumIndexCount );
		while ( capacity < wanted && capacity < _maximumIndexCount ) capacity *= 2;
		AllocateIndexBuffer( bufferIndex, (int)System.Math.Clamp( capacity, 3L, _maximumIndexCount ) );
	}

	private void AllocateIndexBuffer( int bufferIndex, int capacity )
	{
		if ( _indexBuffers[bufferIndex] is not null )
		{
			// CommandList retains native resource references after removal from the camera.
			// Keep replaced index buffers alive for the probe's lifetime, matching vertices.
			_retiredIndexBuffers.Add( new RetiredIndexBuffer(
				_indexBuffers[bufferIndex],
				_indexBufferCapacities[bufferIndex]
			) );
			_retiredIndexCapacity += _indexBufferCapacities[bufferIndex];
		}
		_indexBuffers[bufferIndex] = new GpuBuffer<uint>( capacity,
			GpuBuffer.UsageFlags.Index | GpuBuffer.UsageFlags.Structured, $"Voxel GPU Chunk Indices {bufferIndex}" );
		_indexBufferCapacities[bufferIndex] = capacity;
	}

	private void ScheduleBufferReuseFence()
	{
		BufferReuseFence pendingFence = null;
		lock ( _resultLock )
		{
			for ( var index = 0; index < _bufferReuseFences.Count; index++ )
			{
				if ( _bufferReuseFences[index].Scheduled )
				{
					continue;
				}

				pendingFence = _bufferReuseFences[index];
				pendingFence.Scheduled = true;
				break;
			}
		}

		if ( pendingFence is not null )
		{
			pendingFence.Buffer.GetDataAsync<SimpleVertex>(
				vertices => OnBufferReuseFenceComplete( pendingFence ),
				0,
				1
			);
		}
	}

	private void OnBufferReuseFenceComplete( BufferReuseFence fence )
	{
		lock ( _resultLock )
		{
			if ( !_disposed )
			{
				fence.Complete = true;
			}
		}
	}

	private void BindShader(
		ComputeShader shader,
		int phase,
		Vector3 worldOrigin,
		int chunkSize,
		int sampleSize,
		int haloSize,
		float voxelSize,
		int maximumVertexCount,
		int cellCount,
		int edgeSlotCount )
	{
		shader.Attributes.Set( "SdfSamples", _sdfSamples );
		shader.Attributes.Set( "OutputVertices", _vertexBuffers[0] );
		shader.Attributes.Set( "OutputIndices", _indexBuffers[0] );
		shader.Attributes.Set( "Statistics", _statistics );
		shader.Attributes.Set( "CellTriangleCounts", _cellTriangleCounts );
		shader.Attributes.Set( "CellVertexOffsets", _cellVertexOffsets );
		shader.Attributes.Set( "EdgeFlags", _edgeFlags );
		shader.Attributes.Set( "EdgeVertexIds", _edgeVertexIds );
		shader.Attributes.Set( "CellSelections", _cellSelections );
		shader.Attributes.Set( "CellIndexOffsets", _cellVertexOffsets );
		shader.Attributes.Set( "CellCenterIds", _cellCenterIds );
		shader.Attributes.Set( "LutValues", _lutValues );
		shader.Attributes.Set( "LutMetadata", _lutMetadata );
		shader.Attributes.Set( "ChunkWorldOrigin", worldOrigin );
		shader.Attributes.Set( "ChunkSize", chunkSize );
		shader.Attributes.Set( "SampleSize", sampleSize );
		shader.Attributes.Set( "HaloSize", haloSize );
		shader.Attributes.Set( "VoxelSize", voxelSize );
		shader.Attributes.Set( "MaxVertices", maximumVertexCount );
		shader.Attributes.Set( "MaxIndices", maximumVertexCount );
		shader.Attributes.Set( "CellCount", cellCount );
		shader.Attributes.Set( "EdgeSlotCount", edgeSlotCount );
		shader.Attributes.Set( "Phase", phase );
	}

	#pragma warning disable CS0649 // Layout-only type written by the compute shader.
	private struct CellSelection
	{
		public uint Table;
		public uint Row;
		public uint Subrow;
		public uint TriangleAndCenter;
	}
	#pragma warning restore CS0649

	private enum ProbeState
	{
		Idle,
		DispatchPending,
		ClassifyFenceInFlight,
		ScanPending,
		ScanFenceInFlight,
		OutputCapacityPending,
		EmitPending,
		EmitFenceInFlight,
		TopologyPending,
		ReadbackInFlight,
		VertexReadbackPending,
		VertexReadbackInFlight,
		IndexReadbackPending,
		IndexReadbackInFlight,
		Ready,
		Failed
	}

	private sealed class RetiredVertexBuffer
	{
		public GpuBuffer<SimpleVertex> Buffer { get; }
		public int Capacity { get; }

		public RetiredVertexBuffer( GpuBuffer<SimpleVertex> buffer, int capacity )
		{
			Buffer = buffer;
			Capacity = capacity;
		}
	}

	private sealed class RetiredIndexBuffer
	{
		public GpuBuffer<uint> Buffer { get; }
		public int Capacity { get; }

		public RetiredIndexBuffer( GpuBuffer<uint> buffer, int capacity )
		{
			Buffer = buffer;
			Capacity = capacity;
		}
	}

	private sealed class BufferReuseFence
	{
		public int BufferIndex { get; }
		public GpuBuffer<SimpleVertex> Buffer { get; }
		public bool Scheduled { get; set; }
		public bool Complete { get; set; }

		public BufferReuseFence( int bufferIndex, GpuBuffer<SimpleVertex> buffer )
		{
			BufferIndex = bufferIndex;
			Buffer = buffer;
		}
	}
}
