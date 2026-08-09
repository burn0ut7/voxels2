using System.Runtime.InteropServices;

internal readonly record struct VoxelGpuTransitionDraw( int IndexOffset, int IndexCount );

/// <summary>
/// Builds the fine/coarse seam cell inputs on the CPU and emits their vertices
/// and indices through the same persistent GPU mesh pool as regular residents.
/// Classification is deliberately small and bounded; all interpolation and
/// topology writes happen in the transition compute shader.
/// </summary>
internal sealed class VoxelGpuTransitionMesher : System.IDisposable
{
	private const int TransitionGridSize = 16;
	private const int MaximumCellsPerFace = TransitionGridSize * TransitionGridSize;
	private const int MaximumTransitionFaces = 2048;
	private const int MaximumCells = MaximumCellsPerFace * MaximumTransitionFaces;
	private readonly int _chunkSize;
	private readonly float _voxelSize;
	private readonly ComputeShader _emit;
	private readonly GpuBuffer<TransitionCell> _cells;
	private readonly GpuBuffer<uint> _cellClass;
	private readonly GpuBuffer<uint> _cellGeometry;
	private readonly GpuBuffer<uint> _cellTriangles;
	private readonly GpuBuffer<uint> _vertexData;
	private readonly List<VoxelGpuAllocationHandle> _allocations = new();
	private readonly List<VoxelGpuTransitionDraw> _draws = new();
	private readonly TransitionCell[] _cellScratch = new TransitionCell[MaximumCells];
	private bool _disposed;

	public IReadOnlyList<VoxelGpuTransitionDraw> Draws => _draws;

	public VoxelGpuTransitionMesher( int chunkSize, float voxelSize )
	{
		_chunkSize = chunkSize;
		_voxelSize = voxelSize;
		_cells = new GpuBuffer<TransitionCell>( MaximumCells, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Cells" );
		_cellClass = CreateBuffer( "Voxel GPU Transition Classes", VoxelTransvoxelTransitionTables.CellClass.Select( value => (uint)value ).ToArray() );
		_cellGeometry = CreateBuffer( "Voxel GPU Transition Geometry", VoxelTransvoxelTransitionTables.CellData.Select( value => (uint)value.Geometry ).ToArray() );
		var triangles = new uint[VoxelTransvoxelTransitionTables.CellData.Length * 36];
		for ( var index = 0; index < VoxelTransvoxelTransitionTables.CellData.Length; index++ )
			for ( var triangle = 0; triangle < VoxelTransvoxelTransitionTables.CellData[index].TriangleIndices.Length; triangle++ )
				triangles[index * 36 + triangle] = VoxelTransvoxelTransitionTables.CellData[index].TriangleIndices[triangle];
		_cellTriangles = CreateBuffer( "Voxel GPU Transition Triangles", triangles );
		_vertexData = CreateBuffer( "Voxel GPU Transition Vertices", VoxelTransvoxelTransitionTables.VertexData.Select( value => (uint)value ).ToArray() );
		_emit = new ComputeShader( "shaders/voxel_gpu_transition_emit_mesh_cs.shader" );
		_emit.Attributes.Set( "Cells", _cells );
		_emit.Attributes.Set( "CellClass", _cellClass );
		_emit.Attributes.Set( "CellGeometry", _cellGeometry );
		_emit.Attributes.Set( "CellTriangles", _cellTriangles );
		_emit.Attributes.Set( "VertexData", _vertexData );
		_emit.Attributes.Set( "VoxelSize", _voxelSize );
	}

	public bool Rebuild( IEnumerable<VoxelLodTransitionDescriptor> transitions, VoxelGpuMeshPool pool, ulong epoch, int ruleVersion )
	{
		if ( _disposed ) return false;
		if ( transitions is null ) throw new System.ArgumentNullException( nameof( transitions ) );
		var nextDraws = new List<VoxelGpuTransitionDraw>();
		var nextAllocations = new List<VoxelGpuAllocationHandle>();
		var cellCount = 0;
		foreach ( var transition in transitions )
		{
			if ( nextAllocations.Count >= MaximumTransitionFaces ) break;
			var faceCells = new List<TransitionCell>( MaximumCellsPerFace );
			BuildFaceCells( transition, ruleVersion, faceCells );
			if ( faceCells.Count == 0 ) continue;
			var vertexCount = checked( (int)faceCells.Sum( cell => (long)cell.VertexCount ) );
			var indexCount = checked( (int)faceCells.Sum( cell => (long)cell.IndexCount ) );
			if ( vertexCount == 0 || indexCount == 0 || !pool.TryAllocate( vertexCount, indexCount, (uint)epoch, out var allocation ) )
			{
				foreach ( var allocated in nextAllocations ) pool.ReleaseImmediately( allocated );
				return false;
			}
			var vertexOffset = allocation.Vertices.Offset;
			var indexOffset = allocation.Indices.Offset;
			var localVertex = 0;
			var localIndex = 0;
			for ( var index = 0; index < faceCells.Count; index++ )
			{
				var cell = faceCells[index];
				cell.VertexOffset = (uint)(vertexOffset + localVertex);
				cell.IndexOffset = (uint)(indexOffset + localIndex);
				if ( cellCount >= _cellScratch.Length ) break;
				_cellScratch[cellCount] = cell;
				cellCount++;
				localVertex += checked( (int)cell.VertexCount );
				localIndex += checked( (int)cell.IndexCount );
			}
			nextAllocations.Add( allocation );
			nextDraws.Add( new VoxelGpuTransitionDraw( indexOffset, indexCount ) );
		}

		foreach ( var allocation in _allocations ) pool.Retire( allocation, epoch + VoxelGpuCapabilities.RetirementEpochs );
		_allocations.Clear();
		_allocations.AddRange( nextAllocations );
		_draws.Clear();
		_draws.AddRange( nextDraws );
		if ( cellCount == 0 ) return true;
		var uploadCells = new TransitionCell[cellCount];
		System.Array.Copy( _cellScratch, uploadCells, cellCount );
		_cells.SetData( uploadCells );
		Graphics.ResourceBarrierTransition( _cells, Sandbox.Rendering.ResourceState.NonPixelShaderResource );
		Graphics.ResourceBarrierTransition( pool.Vertices, Sandbox.Rendering.ResourceState.UnorderedAccess );
		Graphics.ResourceBarrierTransition( pool.Indices, Sandbox.Rendering.ResourceState.UnorderedAccess );
		_emit.Attributes.Set( "OutputVertices", pool.Vertices );
		_emit.Attributes.Set( "OutputIndices", pool.Indices );
		_emit.Attributes.Set( "CellCount", (uint)cellCount );
		_emit.Dispatch( (cellCount + 63) / 64, 1, 1 );
		Graphics.UavBarrier( pool.Vertices );
		Graphics.UavBarrier( pool.Indices );
		return true;
	}

	private void BuildFaceCells( VoxelLodTransitionDescriptor transition, int ruleVersion, List<TransitionCell> destination )
	{
		var spacing = 1 << transition.Fine.Lod;
		var basis = GetBasis( transition.Fine, transition.Face, spacing );
		var densities = new float[13];
		for ( var cellV = 0; cellV < TransitionGridSize; cellV++ )
		for ( var cellU = 0; cellU < TransitionGridSize; cellU++ )
		{
			for ( var corner = 0; corner < 13; corner++ ) densities[corner] = Evaluate( SamplePosition( basis, cellU, cellV, corner, spacing ), ruleVersion );
			var caseCode = 0;
			for ( var corner = 0; corner < 9; corner++ ) if ( densities[corner] < 0.0f ) caseCode |= 1 << corner;
			caseCode &= 0x1FF;
			if ( (uint)caseCode >= (uint)VoxelTransvoxelTransitionTables.CellClass.Length ) continue;
			var transitionClass = VoxelTransvoxelTransitionTables.CellClass[caseCode % VoxelTransvoxelTransitionTables.CellClass.Length] & 0x7F;
			transitionClass %= VoxelTransvoxelTransitionTables.CellData.Length;
			var cellData = VoxelTransvoxelTransitionTables.CellData[transitionClass];
			if ( cellData.VertexCount == 0 || cellData.TriangleCount == 0 ) continue;
			var cell = new TransitionCell { CaseCode = (uint)caseCode, VertexCount = (uint)cellData.VertexCount, IndexCount = (uint)(cellData.TriangleCount * 3), Origin = new Vector4( basis.Origin, 0 ), U = new Vector4( basis.U * spacing, 0 ), V = new Vector4( basis.V * spacing, 0 ), W = new Vector4( basis.W * spacing * 2.0f, 0 ) };
			cell.SetDensities( densities );
			destination.Add( cell );
		}
	}

	private static Vector3 SamplePosition( FaceBasis basis, int cellU, int cellV, int corner, int spacing )
	{
		if ( corner < 9 )
			return basis.Origin + basis.U * ((cellU * 2 + corner % 3) * spacing) + basis.V * ((cellV * 2 + corner / 3) * spacing);
		var u = corner is 10 or 12 ? 2 : 0;
		var v = corner is 11 or 12 ? 2 : 0;
		return basis.Origin + basis.U * ((cellU * 2 + u) * spacing) + basis.V * ((cellV * 2 + v) * spacing) + basis.W * (spacing * 2);
	}

	private static float Evaluate( Vector3 world, int ruleVersion )
	{
		if ( ruleVersion == 0 ) return world.z;
		var rolling = System.MathF.Sin( world.x * 0.0031f ) * 2.5f + System.MathF.Cos( world.y * 0.0027f ) * 2.0f;
		return world.z + 16.0f - rolling;
	}

	private static FaceBasis GetBasis( VoxelVisualBlockKey fine, VoxelLodFaceDirection face, int spacing )
	{
		var origin = new Vector3( fine.Coordinate.x * 32 * spacing, fine.Coordinate.y * 32 * spacing, (fine.Coordinate.z - 1) * 32 * spacing );
		return face switch
		{
			VoxelLodFaceDirection.NegativeX => new( origin, new Vector3( 0, 0, 1 ), new Vector3( 0, 1, 0 ), new Vector3( -1, 0, 0 ) ),
			VoxelLodFaceDirection.PositiveX => new( origin + new Vector3( 32 * spacing, 0, 0 ), new Vector3( 0, 0, -1 ), new Vector3( 0, 1, 0 ), new Vector3( 1, 0, 0 ) ),
			VoxelLodFaceDirection.NegativeY => new( origin, new Vector3( 1, 0, 0 ), new Vector3( 0, 0, 1 ), new Vector3( 0, -1, 0 ) ),
			VoxelLodFaceDirection.PositiveY => new( origin + new Vector3( 0, 32 * spacing, 0 ), new Vector3( 1, 0, 0 ), new Vector3( 0, 0, -1 ), new Vector3( 0, 1, 0 ) ),
			VoxelLodFaceDirection.NegativeZ => new( origin, new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ), new Vector3( 0, 0, -1 ) ),
			_ => new( origin + new Vector3( 0, 0, 32 * spacing ), new Vector3( -1, 0, 0 ), new Vector3( 0, 1, 0 ), new Vector3( 0, 0, 1 ) )
		};
	}

	private static GpuBuffer<uint> CreateBuffer( string name, uint[] values )
	{
		var buffer = new GpuBuffer<uint>( values.Length, GpuBuffer.UsageFlags.Structured, name );
		buffer.SetData( values );
		return buffer;
	}

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		_cells.Dispose(); _cellClass.Dispose(); _cellGeometry.Dispose(); _cellTriangles.Dispose(); _vertexData.Dispose();
	}

	private readonly record struct FaceBasis( Vector3 Origin, Vector3 U, Vector3 V, Vector3 W );

	[StructLayout( LayoutKind.Sequential, Pack = 4 )]
	private struct TransitionCell
	{
		public Vector4 Origin;
		public Vector4 U;
		public Vector4 V;
		public Vector4 W;
		public Vector4 Density0;
		public Vector4 Density1;
		public Vector4 Density2;
		public Vector4 Density3;
		public uint CaseCode;
		public uint VertexCount;
		public uint IndexCount;
		public uint VertexOffset;
		public uint IndexOffset;

		public void SetDensities( float[] values )
		{
			Density0 = new Vector4( values[0], values[1], values[2], values[3] );
			Density1 = new Vector4( values[4], values[5], values[6], values[7] );
			Density2 = new Vector4( values[8], values[9], values[10], values[11] );
			Density3 = new Vector4( values[12], 0, 0, 0 );
		}
	}
}
