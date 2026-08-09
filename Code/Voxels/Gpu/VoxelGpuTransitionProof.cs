internal readonly record struct VoxelGpuTransitionProofResult(
	bool Passed,
	string Failure,
	int Cases,
	int EmittedVertices,
	int EmittedIndices,
	double SubmissionMilliseconds,
	double CompletionMilliseconds
);

/// <summary>
/// GPU table-driven transition-cell emission proof. It exercises every 9-bit
/// transition case and validates the emitted topology against the authoritative
/// Transvoxel tables before the runtime seam path is enabled.
/// </summary>
internal sealed class VoxelGpuTransitionProof : SceneCustomObject, System.IDisposable
{
	private const int CaseCount = 512;
	private const int VertexDataStride = 12;
	private const int OutputStride = 50;
	private readonly GpuBuffer<uint> _cases;
	private readonly GpuBuffer<uint> _cellClass;
	private readonly GpuBuffer<uint> _cellGeometry;
	private readonly GpuBuffer<uint> _cellTriangles;
	private readonly GpuBuffer<uint> _vertexData;
	private readonly GpuBuffer<uint> _output;
	private readonly ComputeShader _emit;
	private readonly object _sync = new();
	private readonly uint[] _completedOutput = new uint[CaseCount * OutputStride];
	private bool _running;
	private bool _awaitingReadback;
	private bool _hasResult;
	private bool _disposed;
	private VoxelGpuTransitionProofResult _result;
	private long _dispatchTimestamp;

	public bool IsRunning { get { lock ( _sync ) return !_disposed && _running; } }

	public VoxelGpuTransitionProof( SceneWorld world ) : base( world )
	{
		_cases = new GpuBuffer<uint>( CaseCount, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Cases" );
		_cases.SetData( Enumerable.Range( 0, CaseCount ).Select( value => (uint)value ).ToArray() );
		_cellClass = new GpuBuffer<uint>( CaseCount, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Classes" );
		_cellClass.SetData( VoxelTransvoxelTransitionTables.CellClass.Select( value => (uint)value ).ToArray() );
		_cellGeometry = new GpuBuffer<uint>( VoxelTransvoxelTransitionTables.CellData.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Cell Geometry" );
		_cellTriangles = new GpuBuffer<uint>( VoxelTransvoxelTransitionTables.CellData.Length * 36, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Cell Triangles" );
		var cellGeometry = new uint[VoxelTransvoxelTransitionTables.CellData.Length];
		var cellTriangles = new uint[VoxelTransvoxelTransitionTables.CellData.Length * 36];
		for ( var index = 0; index < VoxelTransvoxelTransitionTables.CellData.Length; index++ )
		{
			var source = VoxelTransvoxelTransitionTables.CellData[index];
			cellGeometry[index] = source.Geometry;
			for ( var triangleIndex = 0; triangleIndex < source.TriangleIndices.Length; triangleIndex++ )
				cellTriangles[index * 36 + triangleIndex] = source.TriangleIndices[triangleIndex];
		}
		_cellGeometry.SetData( cellGeometry );
		_cellTriangles.SetData( cellTriangles );
		_vertexData = new GpuBuffer<uint>( CaseCount * VertexDataStride, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Vertex Data" );
		_vertexData.SetData( VoxelTransvoxelTransitionTables.VertexData.Select( value => (uint)value ).ToArray() );
		_output = new GpuBuffer<uint>( CaseCount * OutputStride, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Emission" );
		_emit = new ComputeShader( "shaders/voxel_gpu_transition_emit_cs.shader" );
		_emit.Attributes.Set( "Cases", _cases );
		_emit.Attributes.Set( "CellClass", _cellClass );
		_emit.Attributes.Set( "CellGeometry", _cellGeometry );
		_emit.Attributes.Set( "CellTriangles", _cellTriangles );
		_emit.Attributes.Set( "VertexData", _vertexData );
		_emit.Attributes.Set( "Output", _output );
		_emit.Attributes.Set( "VertexDataStride", VertexDataStride );
		Bounds = BBox.FromPositionAndSize( Vector3.Zero, Vector3.One * 1_000_000_000.0f );
	}

	public void Run()
	{
		lock ( _sync )
		{
			if ( _disposed || _running ) return;
			_running = true;
			_hasResult = false;
		}
	}

	public bool TryTakeResult( out VoxelGpuTransitionProofResult result )
	{
		lock ( _sync )
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
		lock ( _sync )
		{
			if ( _disposed ) return;
			if ( _awaitingReadback )
			{
				_awaitingReadback = false;
				Graphics.ResourceBarrierTransition( _output, Sandbox.Rendering.ResourceState.CopySource );
				_output.GetData( _completedOutput );
				var failure = ValidateOutput( _completedOutput, out var vertices, out var indices );
				_result = new VoxelGpuTransitionProofResult( string.IsNullOrEmpty( failure ), failure ?? string.Empty, CaseCount, vertices, indices,
					0.0, System.Diagnostics.Stopwatch.GetElapsedTime( _dispatchTimestamp ).TotalMilliseconds );
				_hasResult = true;
				return;
			}
		}
		lock ( _sync )
		{
			if ( _disposed || !_running ) return;
			_running = false;
		}
		try
		{
			Graphics.ResourceBarrierTransition( _output, Sandbox.Rendering.ResourceState.UnorderedAccess );
			_emit.Dispatch( CaseCount, 1, 1 );
			Graphics.UavBarrier( _output );
			_dispatchTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
			_awaitingReadback = true;
		}
		catch ( System.Exception exception )
		{
			lock ( _sync )
			{
				_result = new VoxelGpuTransitionProofResult( false, exception.Message, CaseCount, 0, 0,
					0.0, 0.0 );
				_hasResult = true;
			}
		}
	}

	private static string ValidateOutput( uint[] output, out int vertices, out int indices )
	{
		vertices = 0;
		indices = 0;
		for ( var caseCode = 0; caseCode < CaseCount; caseCode++ )
		{
			var transitionClass = VoxelTransvoxelTransitionTables.CellClass[caseCode] & 0x7F;
			var cell = VoxelTransvoxelTransitionTables.CellData[transitionClass];
			var baseOffset = caseCode * OutputStride;
			var vertexCount = checked( (int)output[baseOffset] );
			var indexCount = checked( (int)output[baseOffset + 1] );
			vertices += vertexCount;
			indices += indexCount;
			if ( vertexCount != cell.VertexCount || indexCount != cell.TriangleCount * 3 ) return $"case {caseCode} emitted counts {vertexCount}/{indexCount}, expected {cell.VertexCount}/{cell.TriangleCount * 3}";
			for ( var vertex = 0; vertex < cell.VertexCount; vertex++ )
				if ( output[baseOffset + 2 + vertex] != VoxelTransvoxelTransitionTables.VertexData[caseCode * VertexDataStride + vertex] ) return $"case {caseCode} emitted vertex {vertex} with the wrong transition edge";
			for ( var index = 0; index < cell.TriangleIndices.Length; index++ )
				if ( output[baseOffset + 14 + index] != cell.TriangleIndices[index] ) return $"case {caseCode} emitted index {index} with the wrong transition topology";
		}
		return string.Empty;
	}

	public void Dispose()
	{
		lock ( _sync )
		{
			if ( _disposed ) return;
			_disposed = true;
			_running = false;
		}
		_cases.Dispose();
		_cellClass.Dispose();
		_cellGeometry.Dispose();
		_cellTriangles.Dispose();
		_vertexData.Dispose();
		_output.Dispose();
		Delete();
	}
}
