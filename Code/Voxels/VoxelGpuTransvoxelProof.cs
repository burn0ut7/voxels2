internal readonly record struct VoxelGpuTransvoxelProofResult(
	bool Passed,
	string Failure,
	uint VertexCount,
	uint IndexCount,
	uint ActiveCells,
	uint OverflowAttempts,
	long GpuBufferBytes,
	double SubmissionMilliseconds,
	double CompletionMilliseconds,
	double GeometryReadbackMilliseconds,
	int BatchSize = 0,
	int SurfaceBlockCount = 0,
	int DispatchCount = 0,
	bool GpuCountPublicationPassed = false,
	double GpuCountPublicationMilliseconds = 0.0,
	double CpuCountPublicationMilliseconds = 0.0
);

internal sealed class VoxelGpuTransvoxelProof : SceneCustomObject, System.IDisposable
{
	private static readonly string[] ComputeShaderNames =
	{
		"shaders/voxel_gpu_transvoxel_clear_cs.shader",
		"shaders/voxel_gpu_transvoxel_density_cs.shader",
		"shaders/voxel_gpu_transvoxel_classify_cs.shader",
		"shaders/voxel_gpu_transvoxel_scan_cs.shader",
		"shaders/voxel_gpu_transvoxel_scan_cs.shader",
		"shaders/voxel_gpu_transvoxel_scan_cs.shader",
		"shaders/voxel_gpu_transvoxel_clear_cs.shader",
		"shaders/voxel_gpu_transvoxel_vertices_cs.shader",
		"shaders/voxel_gpu_transvoxel_indices_cs.shader",
		"shaders/voxel_gpu_transvoxel_classify_cs.shader"
	};
	private const string RenderShaderName = "shaders/voxel_gpu_transvoxel.shader";
	private const int StatisticsCount = 10;
	private const int PhaseCount = 10;
	private const int MaximumBatchSize = 128;
	private const int DescriptorOffset = MaximumBatchSize * 2;
	private const int TotalsOffset = DescriptorOffset + MaximumBatchSize * 4;
	private static readonly int[] RequiredBatchSizes = { 1, 8, 32, 128 };

	private readonly ComputeShader[] _phases = new ComputeShader[PhaseCount];
	private readonly GpuBuffer<GpuBlockInput> _blocks;
	private readonly GpuBuffer<float> _densitySamples;
	private readonly GpuBuffer<uint> _regularLookup;
	private readonly int _regularGeometryCountsOffset;
	private readonly int _regularTriangleIndicesOffset;
	private readonly int _regularVertexDataOffset;
	private readonly GpuBuffer<GpuCellData> _cells;
	private readonly GpuBuffer<uint> _edgeFlags;
	private readonly GpuBuffer<uint> _edgeVertexIds;
	private readonly GpuBuffer<SimpleVertex> _vertices;
	private readonly GpuBuffer<uint> _indices;
	private readonly GpuBuffer<uint> _statistics;
	private readonly GpuBuffer<uint> _edgeGroupSums;
	private readonly GpuBuffer<uint> _cellGroupSums;
	private readonly GpuBuffer<uint> _blockCounts;
	private readonly GpuBuffer<uint> _drawIndexCounter;
	private readonly GpuBuffer<GpuBuffer.IndirectDrawIndexedArguments> _indirectArguments;
	private readonly Sandbox.Rendering.CommandList _drawCommands;
	private readonly Sandbox.Rendering.CommandList _computeCommands;
	private readonly CameraComponent _camera;
	private readonly VoxelMeshData _cpuReference;
	private readonly Vector3 _drawOrigin;
	private readonly int _chunkSize;
	private readonly int _sampleSize;
	private readonly int _haloSize;
	private readonly float _voxelSize;
	private readonly float _sdfClampDistance;
	private readonly int _haloSampleCount;
	private readonly int _cellCount;
	private readonly int _edgeSlotCount;
	private readonly int _maximumVertexCount;
	private readonly int _maximumIndexCount;
	private readonly int _edgeGroupCount;
	private readonly int _cellGroupCount;
	private readonly int _scanGroupCount;
	private readonly long _gpuBufferBytes;
	private readonly object _resultLock = new();
	private ProofState _state;
	private long _dispatchTimestamp;
	private long _readbackTimestamp;
	private double _submissionMilliseconds;
	private double _gpuCountPublicationMilliseconds;
	private double _cpuCountPublicationMilliseconds;
	private int _experimentIndex;
	private int _currentBatchSize;
	private int _currentSurfaceBlockCount;
	private uint _expectedVertexCount;
	private uint _expectedIndexCount;
	private bool _gpuCountPublicationPassed = true;
	private int _dispatchCount;
	private uint[] _completedStatistics;
	private SimpleVertex[] _completedVertices;
	private VoxelGpuTransvoxelProofResult _result;
	private bool _hasResult;
	private bool _disposed;

	public bool IsRunning => !_disposed && _state is not ProofState.Idle and not ProofState.Complete and not ProofState.Failed;

	public VoxelGpuTransvoxelProof(
		SceneWorld sceneWorld,
		CameraComponent camera,
		VoxelMeshData cpuReference,
		Vector3 sampleOrigin,
		Vector3 drawOrigin,
		int chunkSize,
		float voxelSize,
		float sdfClampDistance )
		: base( sceneWorld )
	{
		if ( camera is null ) throw new System.ArgumentNullException( nameof( camera ) );
		if ( cpuReference is null ) throw new System.ArgumentNullException( nameof( cpuReference ) );
		if ( chunkSize < 1 ) throw new System.ArgumentOutOfRangeException( nameof( chunkSize ) );

		_camera = camera;
		_cpuReference = cpuReference;
		_drawOrigin = drawOrigin;
		_chunkSize = chunkSize;
		var sampleSize = checked( chunkSize + 1 );
		var haloSize = checked( chunkSize + 3 );
		_sampleSize = sampleSize;
		_haloSize = haloSize;
		_voxelSize = voxelSize;
		_sdfClampDistance = sdfClampDistance;
		_haloSampleCount = checked( haloSize * haloSize * haloSize );
		_cellCount = checked( chunkSize * chunkSize * chunkSize );
		_edgeSlotCount = checked( sampleSize * sampleSize * sampleSize * 3 );
		_edgeGroupCount = (_edgeSlotCount + 255) / 256;
		_cellGroupCount = (_cellCount + 255) / 256;
		_scanGroupCount = System.Math.Max( _edgeGroupCount, _cellGroupCount );
		_maximumVertexCount = System.Math.Max( 65_536, checked( cpuReference.Vertices.Count * MaximumBatchSize * 2 ) );
		_maximumIndexCount = System.Math.Max( 393_216, checked( cpuReference.Indices.Count * MaximumBatchSize * 2 ) );

		var blockInputs = new GpuBlockInput[MaximumBatchSize];
		for ( var block = 0; block < blockInputs.Length; block++ )
		{
			var gridX = block % 16;
			var gridY = block / 16;
			var topologyOffset = block > 0 && block % 4 == 1 ? 10_000.0f : block > 0 && block % 4 == 2 ? -10_000.0f : 0.0f;
			var blockOffset = new Vector3( gridX * chunkSize * voxelSize, gridY * chunkSize * voxelSize, topologyOffset );
			blockInputs[block] = new GpuBlockInput(
				new Vector4( sampleOrigin + blockOffset, 0.0f ),
				new Vector4( drawOrigin + new Vector3( blockOffset.x, blockOffset.y, 0.0f ), 0.0f )
			);
		}
		_blocks = new GpuBuffer<GpuBlockInput>( MaximumBatchSize, GpuBuffer.UsageFlags.Structured, "Voxel GPU Proof Blocks" );
		_blocks.SetData( blockInputs );
		_densitySamples = new GpuBuffer<float>( checked( _haloSampleCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Proof Density" );
		_regularGeometryCountsOffset = VoxelTransvoxelTables.RegularCellClass.Length;
		_regularTriangleIndicesOffset = _regularGeometryCountsOffset + VoxelTransvoxelTables.RegularGeometryCounts.Length;
		_regularVertexDataOffset = _regularTriangleIndicesOffset + VoxelTransvoxelTables.RegularTriangleIndices.Length;
		_regularLookup = CreateRegularLookupBuffer();
		_cells = new GpuBuffer<GpuCellData>( checked( _cellCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Proof Cells" );
		_edgeFlags = new GpuBuffer<uint>( checked( _edgeSlotCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Proof Edge Flags" );
		_edgeVertexIds = new GpuBuffer<uint>( checked( _edgeSlotCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Proof Edge Vertex IDs" );
		_vertices = new GpuBuffer<SimpleVertex>( _maximumVertexCount,
			GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.Vertex, "Voxel GPU Proof Vertices" );
		_indices = new GpuBuffer<uint>( _maximumIndexCount,
			GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.Index, "Voxel GPU Proof Indices" );
		_statistics = new GpuBuffer<uint>( StatisticsCount + MaximumBatchSize * 4, GpuBuffer.UsageFlags.Structured, "Voxel GPU Proof Statistics And Descriptors" );
		_statistics.SetData( new uint[StatisticsCount + MaximumBatchSize * 4] );
		_edgeGroupSums = new GpuBuffer<uint>( checked( _edgeGroupCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Proof Edge Group Sums" );
		_cellGroupSums = new GpuBuffer<uint>( checked( _cellGroupCount * MaximumBatchSize ), GpuBuffer.UsageFlags.Structured, "Voxel GPU Proof Cell Group Sums" );
		_blockCounts = new GpuBuffer<uint>( TotalsOffset + 2, GpuBuffer.UsageFlags.Structured, "Voxel GPU Proof Block Allocation" );
		_drawIndexCounter = new GpuBuffer<uint>( _maximumIndexCount, GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.Append, "Voxel GPU Proof Publication Counter" );
		_indirectArguments = new GpuBuffer<GpuBuffer.IndirectDrawIndexedArguments>( 1,
			GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.IndirectDrawArguments, "Voxel GPU Proof Indirect Arguments" );
		_indirectArguments.SetData( new[] { new GpuBuffer.IndirectDrawIndexedArguments { InstanceCount = 1 } } );

		for ( var phase = 0; phase < PhaseCount; phase++ )
		{
			_phases[phase] = new ComputeShader( ComputeShaderNames[phase] );
			BindShader( _phases[phase], sampleSize, haloSize, voxelSize, sdfClampDistance );
		}
		_phases[0].Attributes.Set( "AllocationPass", 0 );
		_phases[2].Attributes.Set( "PublicationPass", 0 );

		var material = Material.FromShader( RenderShaderName );
		_drawCommands = new Sandbox.Rendering.CommandList( "Voxel GPU Transvoxel Proof Draw" );
		_computeCommands = new Sandbox.Rendering.CommandList( "Voxel GPU Transvoxel Batch Compute" );
		_drawCommands.ResourceBarrierTransition( _vertices, Sandbox.Rendering.ResourceState.VertexOrIndexBuffer );
		_drawCommands.ResourceBarrierTransition( _indices, Sandbox.Rendering.ResourceState.VertexOrIndexBuffer );
		_drawCommands.ResourceBarrierTransition( _indirectArguments, Sandbox.Rendering.ResourceState.IndirectArgument );
		_drawCommands.DrawIndexedInstancedIndirect(
			_vertices,
			_indices,
			material,
			_indirectArguments,
			0,
			null,
			Graphics.PrimitiveType.Triangles
		);

		_gpuBufferBytes =
			(long)MaximumBatchSize * 32 +
			(long)_haloSampleCount * MaximumBatchSize * sizeof( float ) +
			(long)(VoxelTransvoxelTables.RegularCellClass.Length + VoxelTransvoxelTables.RegularGeometryCounts.Length +
				VoxelTransvoxelTables.RegularTriangleIndices.Length + VoxelTransvoxelTables.RegularVertexData.Length) * sizeof( uint ) +
			(long)_cellCount * MaximumBatchSize * sizeof( uint ) * 3 +
			(long)_edgeSlotCount * MaximumBatchSize * sizeof( uint ) * 2 +
			(long)_maximumVertexCount * 44 +
			(long)_maximumIndexCount * sizeof( uint ) +
			(long)(_edgeGroupCount + _cellGroupCount) * MaximumBatchSize * sizeof( uint ) +
			(long)MaximumBatchSize * (8 + 16) +
			(long)_maximumIndexCount * sizeof( uint ) + StatisticsCount * sizeof( uint ) + 20;

		// RenderSceneObject is the render-thread submission boundary. Keep the proof object
		// globally eligible so an off-camera validation mesh cannot prevent its dispatch.
		Bounds = BBox.FromPositionAndSize( drawOrigin, Vector3.One * 1_000_000_000.0f );
	}

	public void Run()
	{
		if ( _disposed || _state != ProofState.Idle ) return;
		_experimentIndex = 0;
		_submissionMilliseconds = 0.0;
		_gpuCountPublicationMilliseconds = 0.0;
		_cpuCountPublicationMilliseconds = 0.0;
		_dispatchCount = 0;
		_state = ProofState.DispatchPending;
	}

	public bool TryTakeResult( out VoxelGpuTransvoxelProofResult result )
	{
		lock ( _resultLock )
		{
			if ( !_hasResult )
			{
				result = default;
				return false;
			}

			result = _result;
			_hasResult = false;
			return true;
		}
	}

	public override void RenderSceneObject()
	{
		if ( _disposed ) return;
		try
		{
			if ( _state == ProofState.ComputeExecutionPending )
			{
				_state = ProofState.StatisticsReadbackPending;
				return;
			}
			if ( _state == ProofState.StatisticsReadbackPending )
			{
				_camera.RemoveCommandList( _computeCommands );
				_state = ProofState.StatisticsReadback;
				var statistics = new uint[StatisticsCount];
				_statistics.GetData( statistics );
				OnStatisticsRead( statistics );
				return;
			}
			if ( _state != ProofState.DispatchPending ) return;
			_currentBatchSize = _experimentIndex < RequiredBatchSizes.Length ? RequiredBatchSizes[_experimentIndex] : MaximumBatchSize;
			_currentSurfaceBlockCount = CountSurfaceBlocks( _currentBatchSize );
			_expectedVertexCount = checked( (uint)(_cpuReference.Vertices.Count * _currentSurfaceBlockCount) );
			_expectedIndexCount = checked( (uint)(_cpuReference.Indices.Count * _currentSurfaceBlockCount) );
			var gpuPublication = _experimentIndex < RequiredBatchSizes.Length;
			SetBatchSize( _currentBatchSize );
			var submissionStart = System.Diagnostics.Stopwatch.GetTimestamp();
			_dispatchTimestamp = submissionStart;
			DispatchScanPhases();
			DispatchGeometryPhases( gpuPublication );
			BuildComputeCommands( gpuPublication );
			_dispatchCount += 9;
			if ( gpuPublication )
			{
				var publicationStart = System.Diagnostics.Stopwatch.GetTimestamp();
				_gpuCountPublicationMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime( publicationStart ).TotalMilliseconds;
				_dispatchCount++;
			}
			_camera.AddCommandList( _computeCommands, Sandbox.Rendering.Stage.AfterOpaque );
			_submissionMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime( submissionStart ).TotalMilliseconds;
			_state = ProofState.ComputeExecutionPending;
		}
		catch ( System.Exception exception )
		{
			CompleteFailure( $"dispatch failed: {exception.Message}" );
		}
	}

	private void BuildComputeCommands( bool gpuPublication )
	{
		_computeCommands.Reset();
		if ( gpuPublication )
		{
			_computeCommands.ResourceBarrierTransition( _indirectArguments, Sandbox.Rendering.ResourceState.CopyDestination ); _computeCommands.CopyStructureCount( _drawIndexCounter, _indirectArguments, 0 ); _computeCommands.ResourceBarrierTransition( _indirectArguments, Sandbox.Rendering.ResourceState.IndirectArgument );
		}
		_computeCommands.ResourceBarrierTransition( _vertices, Sandbox.Rendering.ResourceState.UnorderedAccess, Sandbox.Rendering.ResourceState.VertexOrIndexBuffer );
		_computeCommands.ResourceBarrierTransition( _indices, Sandbox.Rendering.ResourceState.UnorderedAccess, Sandbox.Rendering.ResourceState.VertexOrIndexBuffer );
	}

	private void DispatchScanPhases()
	{
		_statistics.Clear();
		Graphics.ResourceBarrierTransition( _densitySamples, Sandbox.Rendering.ResourceState.UnorderedAccess );
		Graphics.ResourceBarrierTransition( _regularLookup, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		foreach ( var buffer in new GpuBuffer[] { _cells, _edgeFlags, _edgeVertexIds, _statistics, _edgeGroupSums, _cellGroupSums, _blockCounts } ) Graphics.ResourceBarrierTransition( buffer, Sandbox.Rendering.ResourceState.UnorderedAccess );
		_phases[0].Dispatch( System.Math.Max( _cellCount, _edgeSlotCount ) * _currentBatchSize, 1, 1 ); ImmediateBarrier( _cells, _edgeFlags, _edgeVertexIds );
		_phases[1].Dispatch( _haloSampleCount * _currentBatchSize, 1, 1 ); ImmediateBarrier( _densitySamples );
		_phases[2].Attributes.Set( "PublicationPass", 0 );
		_phases[2].Dispatch( _cellCount * _currentBatchSize, 1, 1 ); ImmediateBarrier( _cells, _edgeFlags, _statistics );
		_phases[3].Attributes.Set( "ScanPass", 0 );
		_phases[3].Dispatch( _edgeGroupCount * 256 * _currentBatchSize, 1, 1 ); ImmediateBarrier( _edgeVertexIds, _edgeGroupSums );
		_phases[3].Attributes.Set( "ScanPass", 1 );
		_phases[3].Dispatch( _cellGroupCount * 256 * _currentBatchSize, 1, 1 ); ImmediateBarrier( _cells, _cellGroupSums );
		_phases[3].Attributes.Set( "ScanPass", 2 );
		_phases[3].Dispatch( 256 * _currentBatchSize, 1, 1 ); ImmediateBarrier( _edgeGroupSums, _cellGroupSums, _blockCounts );
	}

	private void DispatchGeometryPhases( bool gpuPublication )
	{
		_phases[0].Attributes.Set( "AllocationPass", 1 );
		_phases[0].Dispatch( 64 * _currentBatchSize, 1, 1 );
		ImmediateBarrier( _blockCounts );
		_phases[0].Attributes.Set( "AllocationPass", 0 );
		_phases[7].Dispatch( _edgeSlotCount * _currentBatchSize, 1, 1 );
		ImmediateBarrier( _vertices );
		_phases[8].Dispatch( _cellCount * _currentBatchSize, 1, 1 );
		ImmediateBarrier( _indices );
		if ( !gpuPublication ) return;
		_drawIndexCounter.SetCounterValue( 0 );
		_phases[2].Attributes.Set( "PublicationPass", 1 );
		_phases[2].Dispatch( _cellCount * _currentBatchSize, 1, 1 );
		ImmediateBarrier( _drawIndexCounter );
		_phases[2].Attributes.Set( "PublicationPass", 0 );
	}

	private void BindCommandAttributes( Sandbox.Rendering.CommandList commands, int allocationPass )
	{
		var attributes = commands.Attributes;
		attributes.Set( "Blocks", _blocks ); attributes.Set( "DensitySamples", _densitySamples ); attributes.Set( "RegularLookup", _regularLookup );
		attributes.Set( "Cells", _cells ); attributes.Set( "EdgeFlags", _edgeFlags ); attributes.Set( "EdgeVertexIds", _edgeVertexIds );
		attributes.Set( "OutputVertices", _vertices ); attributes.Set( "OutputIndices", _indices ); attributes.Set( "Statistics", _statistics );
		attributes.Set( "EdgeGroupSums", _edgeGroupSums ); attributes.Set( "CellGroupSums", _cellGroupSums ); attributes.Set( "BlockCounts", _blockCounts );
		attributes.Set( "DrawIndexCounter", _drawIndexCounter ); attributes.Set( "BatchSize", _currentBatchSize ); attributes.Set( "DescriptorOffset", DescriptorOffset ); attributes.Set( "TotalsOffset", TotalsOffset );
		attributes.Set( "RegularGeometryCountsOffset", _regularGeometryCountsOffset ); attributes.Set( "RegularTriangleIndicesOffset", _regularTriangleIndicesOffset ); attributes.Set( "RegularVertexDataOffset", _regularVertexDataOffset );
		attributes.Set( "ChunkSize", _chunkSize ); attributes.Set( "SampleSize", _sampleSize ); attributes.Set( "HaloSize", _haloSize ); attributes.Set( "HaloSampleCount", _haloSampleCount );
		attributes.Set( "CellCount", _cellCount ); attributes.Set( "EdgeSlotCount", _edgeSlotCount ); attributes.Set( "EdgeGroupCount", _edgeGroupCount ); attributes.Set( "CellGroupCount", _cellGroupCount ); attributes.Set( "ScanGroupCount", _scanGroupCount );
		attributes.Set( "MaxVertices", _maximumVertexCount ); attributes.Set( "MaxIndices", _maximumIndexCount ); attributes.Set( "VoxelSize", _voxelSize ); attributes.Set( "SdfClampDistance", _sdfClampDistance );
		attributes.Set( "AllocationPass", allocationPass );
		attributes.Set( "PublicationPass", 1 );
		attributes.Set( "ScanPass", 0 );
	}

	private static void CommandBarrier( Sandbox.Rendering.CommandList commands, params GpuBuffer[] buffers )
	{
		foreach ( var buffer in buffers ) commands.UavBarrier( buffer );
	}

	private static void ImmediateBarrier( params GpuBuffer[] buffers )
	{
		foreach ( var buffer in buffers ) Graphics.UavBarrier( buffer );
	}

	private void OnStatisticsRead( System.ReadOnlySpan<uint> statistics )
	{
		lock ( _resultLock )
		{
			if ( _disposed || _state != ProofState.StatisticsReadback ) return;
			_completedStatistics = statistics.ToArray();
			var totals = new uint[2];
			_blockCounts.GetData( totals, TotalsOffset, totals.Length );
			var firstBlockCounts = new uint[2];
			_blockCounts.GetData( firstBlockCounts, 0, firstBlockCounts.Length );
			_completedStatistics[5] = totals[0];
			_completedStatistics[6] = totals[1];
			_completedStatistics[9] = totals[0] > _maximumVertexCount || totals[1] > _maximumIndexCount ? 1u : 0u;
			var vertexCount = GetStatistic( 5 );
			var indexCount = GetStatistic( 6 );
			var overflow = GetStatistic( 9 );
			if ( overflow != 0 || vertexCount > _maximumVertexCount || indexCount > _maximumIndexCount )
			{
				CompleteFailureLocked( $"GPU output overflowed (vertices={vertexCount:N0}, indices={indexCount:N0})" );
				return;
			}
			if ( vertexCount != _expectedVertexCount || indexCount != _expectedIndexCount )
			{
				CompleteFailureLocked( $"batch {_currentBatchSize} pooled counts differ: expected={_expectedVertexCount:N0}/{_expectedIndexCount:N0}, GPU={vertexCount:N0}/{indexCount:N0}, firstBlockScan={firstBlockCounts[0]:N0}/{firstBlockCounts[1]:N0}" );
				return;
			}

			if ( _experimentIndex < RequiredBatchSizes.Length )
			{
				var published = new GpuBuffer.IndirectDrawIndexedArguments[1];
				_indirectArguments.GetData( published );
				_gpuCountPublicationPassed &= published[0].IndexCount == indexCount && published[0].InstanceCount == 1;
			}
			else
			{
				var cpuPublicationStart = System.Diagnostics.Stopwatch.GetTimestamp();
				_indirectArguments.SetData( new[]
				{
					new GpuBuffer.IndirectDrawIndexedArguments { IndexCount = indexCount, InstanceCount = 1 }
				} );
				_cpuCountPublicationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( cpuPublicationStart ).TotalMilliseconds;
			}

			if ( _experimentIndex < RequiredBatchSizes.Length )
			{
				_experimentIndex++;
				_state = ProofState.DispatchPending;
				return;
			}
			if ( !_gpuCountPublicationPassed )
			{
				CompleteFailureLocked( "CopyStructureCount did not publish the generated index count and persistent instance count" );
				return;
			}
			_camera.AddCommandList( _drawCommands, Sandbox.Rendering.Stage.AfterOpaque );

			_readbackTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
			_state = ProofState.VertexReadback;
			if ( _cpuReference.Vertices.Count == 0 )
			{
				_completedVertices = System.Array.Empty<SimpleVertex>();
				BeginIndexReadbackLocked( indexCount );
				return;
			}
			_completedVertices = new SimpleVertex[_cpuReference.Vertices.Count];
			_vertices.GetData( _completedVertices, 0, _completedVertices.Length );
			BeginIndexReadbackLocked( indexCount );
		}
	}

	private void BeginIndexReadbackLocked( uint indexCount )
	{
		_state = ProofState.IndexReadback;
		if ( _cpuReference.Indices.Count == 0 )
		{
			CompleteValidationLocked( System.Array.Empty<uint>() );
			return;
		}
		var indices = new uint[_cpuReference.Indices.Count];
		_indices.GetData( indices, 0, indices.Length );
		CompleteValidationLocked( indices );
	}

	private void CompleteValidationLocked( uint[] indices )
	{
		var passed = ValidateGeometry( _cpuReference, _completedVertices, indices, _drawOrigin, out var failure );
		var completionMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( _dispatchTimestamp ).TotalMilliseconds;
		var readbackMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( _readbackTimestamp ).TotalMilliseconds;
		_result = new VoxelGpuTransvoxelProofResult(
			passed,
			failure,
			GetStatistic( 5 ),
			GetStatistic( 6 ),
			GetStatistic( 7 ),
			GetStatistic( 9 ),
			_gpuBufferBytes,
			_submissionMilliseconds,
			completionMilliseconds,
			readbackMilliseconds,
			_currentBatchSize,
			_currentSurfaceBlockCount,
			_dispatchCount,
			_gpuCountPublicationPassed,
			_gpuCountPublicationMilliseconds,
			_cpuCountPublicationMilliseconds
		);
		_state = passed ? ProofState.Complete : ProofState.Failed;
		_hasResult = true;
	}

	private void CompleteFailure( string failure )
	{
		lock ( _resultLock ) CompleteFailureLocked( failure );
	}

	private void CompleteFailureLocked( string failure )
	{
		_result = new VoxelGpuTransvoxelProofResult(
			false,
			failure,
			GetStatistic( 5 ),
			GetStatistic( 6 ),
			GetStatistic( 7 ),
			GetStatistic( 9 ),
			_gpuBufferBytes,
			_submissionMilliseconds,
			_dispatchTimestamp == 0 ? 0.0 : System.Diagnostics.Stopwatch.GetElapsedTime( _dispatchTimestamp ).TotalMilliseconds,
			_readbackTimestamp == 0 ? 0.0 : System.Diagnostics.Stopwatch.GetElapsedTime( _readbackTimestamp ).TotalMilliseconds
		);
		_state = ProofState.Failed;
		_hasResult = true;
	}

	private uint GetStatistic( int index ) =>
		_completedStatistics is not null && index >= 0 && index < _completedStatistics.Length ? _completedStatistics[index] : 0;

	private void BindShader(
		ComputeShader shader,
		int sampleSize,
		int haloSize,
		float voxelSize,
		float sdfClampDistance )
	{
		var attributes = shader.Attributes;
		attributes.Set( "Blocks", _blocks );
		attributes.Set( "DensitySamples", _densitySamples );
		attributes.Set( "RegularLookup", _regularLookup );
		attributes.Set( "RegularGeometryCountsOffset", _regularGeometryCountsOffset );
		attributes.Set( "RegularTriangleIndicesOffset", _regularTriangleIndicesOffset );
		attributes.Set( "RegularVertexDataOffset", _regularVertexDataOffset );
		attributes.Set( "Cells", _cells );
		attributes.Set( "EdgeFlags", _edgeFlags );
		attributes.Set( "EdgeVertexIds", _edgeVertexIds );
		attributes.Set( "OutputVertices", _vertices );
		attributes.Set( "OutputIndices", _indices );
		attributes.Set( "Statistics", _statistics );
		attributes.Set( "EdgeGroupSums", _edgeGroupSums );
		attributes.Set( "CellGroupSums", _cellGroupSums );
		attributes.Set( "BlockCounts", _blockCounts );
		attributes.Set( "DescriptorOffset", DescriptorOffset );
		attributes.Set( "TotalsOffset", TotalsOffset );
		attributes.Set( "DrawIndexCounter", _drawIndexCounter );
		attributes.Set( "ChunkSize", _chunkSize );
		attributes.Set( "SampleSize", sampleSize );
		attributes.Set( "HaloSize", haloSize );
		attributes.Set( "CellCount", _cellCount );
		attributes.Set( "EdgeSlotCount", _edgeSlotCount );
		attributes.Set( "HaloSampleCount", _haloSampleCount );
		attributes.Set( "EdgeGroupCount", _edgeGroupCount );
		attributes.Set( "CellGroupCount", _cellGroupCount );
		attributes.Set( "ScanGroupCount", _scanGroupCount );
		attributes.Set( "MaxVertices", _maximumVertexCount );
		attributes.Set( "MaxIndices", _maximumIndexCount );
		attributes.Set( "VoxelSize", voxelSize );
		attributes.Set( "SdfClampDistance", sdfClampDistance );
	}

	private void SetBatchSize( int batchSize )
	{
		foreach ( var phase in _phases ) phase.Attributes.Set( "BatchSize", batchSize );
	}

	private static int CountSurfaceBlocks( int batchSize )
	{
		var count = 0;
		for ( var block = 0; block < batchSize; block++ )
		{
			if ( block == 0 || block % 4 is 0 or 3 ) count++;
		}
		return count;
	}

	private static GpuBuffer<uint> CreateRegularLookupBuffer()
	{
		var expanded = new uint[
			VoxelTransvoxelTables.RegularCellClass.Length +
			VoxelTransvoxelTables.RegularGeometryCounts.Length +
			VoxelTransvoxelTables.RegularTriangleIndices.Length +
			VoxelTransvoxelTables.RegularVertexData.Length ];
		var offset = 0;
		foreach ( var value in VoxelTransvoxelTables.RegularCellClass ) expanded[offset++] = value;
		foreach ( var value in VoxelTransvoxelTables.RegularGeometryCounts ) expanded[offset++] = value;
		foreach ( var value in VoxelTransvoxelTables.RegularTriangleIndices ) expanded[offset++] = value;
		foreach ( var value in VoxelTransvoxelTables.RegularVertexData ) expanded[offset++] = value;
		var buffer = new GpuBuffer<uint>( expanded.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU Regular Lookup" );
		buffer.SetData( expanded );
		return buffer;
	}

	private static bool ValidateGeometry(
		VoxelMeshData cpu,
		SimpleVertex[] gpuVertices,
		uint[] gpuIndices,
		Vector3 drawOrigin,
		out string failure )
	{
		if ( cpu.Vertices.Count != gpuVertices.Length )
		{
			failure = $"vertex count differs: CPU={cpu.Vertices.Count:N0}, GPU={gpuVertices.Length:N0}";
			return false;
		}
		if ( cpu.Indices.Count != gpuIndices.Length )
		{
			failure = $"index count differs: CPU={cpu.Indices.Count:N0}, GPU={gpuIndices.Length:N0}";
			return false;
		}

		var tolerance = 0.001f;
		var cpuNormals = new Dictionary<string, Vector3>( cpu.Vertices.Count );
		foreach ( var vertex in cpu.Vertices ) cpuNormals[PositionKey( vertex.Position, tolerance )] = vertex.Normal;
		for ( var index = 0; index < gpuVertices.Length; index++ )
		{
			var localPosition = gpuVertices[index].position - drawOrigin;
			var key = PositionKey( localPosition, tolerance );
			if ( !cpuNormals.TryGetValue( key, out var cpuNormal ) )
			{
				failure = $"GPU vertex {index:N0} has no CPU position match at {localPosition}";
				return false;
			}
			if ( Vector3.Dot( cpuNormal, gpuVertices[index].normal ) < 0.999f )
			{
				failure = $"normal mismatch at {localPosition}: CPU={cpuNormal}, GPU={gpuVertices[index].normal}";
				return false;
			}
		}

		var cpuTriangles = BuildCpuTriangleMultiset( cpu, tolerance );
		var gpuTriangles = BuildGpuTriangleMultiset( gpuVertices, gpuIndices, drawOrigin, tolerance, out failure );
		if ( gpuTriangles is null ) return false;
		if ( cpuTriangles.Count != gpuTriangles.Count )
		{
			failure = $"unique triangle set differs: CPU={cpuTriangles.Count:N0}, GPU={gpuTriangles.Count:N0}";
			return false;
		}
		foreach ( var pair in cpuTriangles )
		{
			if ( gpuTriangles.TryGetValue( pair.Key, out var count ) && count == pair.Value ) continue;
			failure = $"triangle topology mismatch at {pair.Key}";
			return false;
		}

		failure = "regular-cell positions, normals, winding, and triangle topology match";
		return true;
	}

	private static Dictionary<string, int> BuildCpuTriangleMultiset( VoxelMeshData mesh, float tolerance )
	{
		var triangles = new Dictionary<string, int>();
		for ( var index = 0; index < mesh.Indices.Count; index += 3 )
		{
			var key = TriangleKey(
				mesh.Vertices[mesh.Indices[index]].Position,
				mesh.Vertices[mesh.Indices[index + 1]].Position,
				mesh.Vertices[mesh.Indices[index + 2]].Position,
				tolerance
			);
			triangles[key] = triangles.TryGetValue( key, out var count ) ? count + 1 : 1;
		}
		return triangles;
	}

	private static Dictionary<string, int> BuildGpuTriangleMultiset(
		SimpleVertex[] vertices,
		uint[] indices,
		Vector3 drawOrigin,
		float tolerance,
		out string failure )
	{
		var triangles = new Dictionary<string, int>();
		for ( var index = 0; index < indices.Length; index += 3 )
		{
			var first = indices[index];
			var second = indices[index + 1];
			var third = indices[index + 2];
			if ( first >= vertices.Length || second >= vertices.Length || third >= vertices.Length )
			{
				failure = $"GPU triangle {index / 3:N0} contains an out-of-range index";
				return null;
			}
			var a = vertices[first].position - drawOrigin;
			var b = vertices[second].position - drawOrigin;
			var c = vertices[third].position - drawOrigin;
			var faceNormal = Vector3.Cross( b - a, c - a );
			if ( faceNormal.LengthSquared <= 0.000000000001f )
			{
				failure = $"GPU triangle {index / 3:N0} is degenerate";
				return null;
			}
			var normalSum = vertices[first].normal + vertices[second].normal + vertices[third].normal;
			if ( Vector3.Dot( faceNormal, normalSum ) < 0.0f )
			{
				failure = $"GPU triangle {index / 3:N0} has reversed winding";
				return null;
			}
			var key = TriangleKey( a, b, c, tolerance );
			triangles[key] = triangles.TryGetValue( key, out var count ) ? count + 1 : 1;
		}
		failure = string.Empty;
		return triangles;
	}

	private static string TriangleKey( Vector3 a, Vector3 b, Vector3 c, float tolerance )
	{
		var keys = new[] { PositionKey( a, tolerance ), PositionKey( b, tolerance ), PositionKey( c, tolerance ) };
		System.Array.Sort( keys, System.StringComparer.Ordinal );
		return string.Join( ";", keys );
	}

	private static string PositionKey( Vector3 position, float tolerance )
	{
		var x = (long)System.Math.Round( position.x / tolerance );
		var y = (long)System.Math.Round( position.y / tolerance );
		var z = (long)System.Math.Round( position.z / tolerance );
		return $"{x}:{y}:{z}";
	}

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		_camera.RemoveCommandList( _drawCommands );
		_camera.RemoveCommandList( _computeCommands );
		_drawCommands.Reset();
		_computeCommands.Reset();
		_densitySamples.Dispose();
		_blocks.Dispose();
		_regularLookup.Dispose();
		_cells.Dispose();
		_edgeFlags.Dispose();
		_edgeVertexIds.Dispose();
		_vertices.Dispose();
		_indices.Dispose();
		_statistics.Dispose();
		_edgeGroupSums.Dispose();
		_cellGroupSums.Dispose();
		_blockCounts.Dispose();
		_drawIndexCounter.Dispose();
		_indirectArguments.Dispose();
		Delete();
	}

	private enum ProofState
	{
		Idle,
		DispatchPending,
		ComputeExecutionPending,
		StatisticsReadbackPending,
		StatisticsReadback,
		VertexReadback,
		IndexReadback,
		Complete,
		Failed
	}

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

	private struct GpuBlockInput
	{
		public Vector4 SampleOrigin;
		public Vector4 DrawOrigin;

		public GpuBlockInput( Vector4 sampleOrigin, Vector4 drawOrigin )
		{
			SampleOrigin = sampleOrigin;
			DrawOrigin = drawOrigin;
		}
	}

}
