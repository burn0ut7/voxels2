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
	private readonly Dictionary<VoxelLodTransitionDescriptor, List<TransitionCell>> _faceCache = new();
	private readonly TransitionCell[] _cellScratch = new TransitionCell[MaximumCells];
	private int _cachedRuleVersion = int.MinValue;
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
		var nextCells = new List<TransitionCell>();
		var transitionCount = 0;
		if ( ruleVersion != _cachedRuleVersion )
		{
			_faceCache.Clear();
			_cachedRuleVersion = ruleVersion;
		}
		var activeTransitions = new HashSet<VoxelLodTransitionDescriptor>();
		foreach ( var transition in transitions )
		{
			if ( ++transitionCount > MaximumTransitionFaces )
			{
				Log.Error( $"Voxel GPU transition mesh rejected {transitionCount:N0} faces; capacity is {MaximumTransitionFaces:N0}." );
				return false;
			}
			activeTransitions.Add( transition );
			if ( !_faceCache.TryGetValue( transition, out var faceCells ) )
			{
				faceCells = new List<TransitionCell>( MaximumCellsPerFace );
				BuildFaceCells( transition, ruleVersion, faceCells );
				_faceCache.Add( transition, faceCells );
			}
			if ( faceCells.Count == 0 ) continue;
			if ( nextCells.Count > _cellScratch.Length - faceCells.Count )
			{
				Log.Error( $"Voxel GPU transition mesh rejected {nextCells.Count + faceCells.Count:N0} cells; capacity is {_cellScratch.Length:N0}." );
				return false;
			}
			nextCells.AddRange( faceCells );
		}
		foreach ( var cached in _faceCache.Keys.Where( key => !activeTransitions.Contains( key ) ).ToArray() ) _faceCache.Remove( cached );

		var nextAllocations = new List<VoxelGpuAllocationHandle>( nextCells.Count > 0 ? 1 : 0 );
		var cellCount = 0;
		if ( nextCells.Count > 0 )
		{
			var vertexCount = checked( (int)nextCells.Sum( cell => (long)cell.VertexCount ) );
			var indexCount = checked( (int)nextCells.Sum( cell => (long)cell.IndexCount ) );
			if ( vertexCount == 0 || indexCount == 0 || !pool.TryAllocate( vertexCount, indexCount, (uint)epoch, out var allocation ) )
			{
				return false;
			}
			var vertexOffset = allocation.Vertices.Offset;
			var indexOffset = allocation.Indices.Offset;
			var localVertex = 0;
			var localIndex = 0;
			for ( var index = 0; index < nextCells.Count; index++ )
			{
				var cell = nextCells[index];
				cell.VertexOffset = (uint)(vertexOffset + localVertex);
				cell.IndexOffset = (uint)(indexOffset + localIndex);
				_cellScratch[cellCount] = cell;
				cellCount++;
				localVertex += checked( (int)cell.VertexCount );
				localIndex += checked( (int)cell.IndexCount );
			}
			nextAllocations.Add( allocation );
		}

		foreach ( var allocation in _allocations ) pool.Retire( allocation, epoch + VoxelGpuCapabilities.RetirementEpochs );
		_allocations.Clear();
		_allocations.AddRange( nextAllocations );
		_draws.Clear();
		if ( nextAllocations.Count > 0 )
		{
			var allocation = nextAllocations[0];
			_draws.Add( new VoxelGpuTransitionDraw( allocation.Indices.Offset, allocation.Indices.Count ) );
		}
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
		var sampleDimension = TransitionGridSize * 2 + 1;
		var frontDensities = new float[sampleDimension * sampleDimension];
		var backDensities = new float[sampleDimension * sampleDimension];
		for ( var sampleV = 0; sampleV < sampleDimension; sampleV++ )
		for ( var sampleU = 0; sampleU < sampleDimension; sampleU++ )
		{
			var offset = basis.U * (sampleU * spacing) + basis.V * (sampleV * spacing);
			var index = sampleV * sampleDimension + sampleU;
			frontDensities[index] = Evaluate( basis.Origin + offset, ruleVersion );
			backDensities[index] = Evaluate( basis.Origin + offset + basis.W * (spacing * 2), ruleVersion );
		}
		var densities = new float[13];
		for ( var cellV = 0; cellV < TransitionGridSize; cellV++ )
		for ( var cellU = 0; cellU < TransitionGridSize; cellU++ )
		{
			var baseU = cellU * 2;
			var baseV = cellV * 2;
			for ( var corner = 0; corner < 9; corner++ )
				densities[corner] = frontDensities[(baseV + corner / 3) * sampleDimension + baseU + corner % 3];
			densities[9] = backDensities[baseV * sampleDimension + baseU];
			densities[10] = backDensities[baseV * sampleDimension + baseU + 2];
			densities[11] = backDensities[(baseV + 2) * sampleDimension + baseU];
			densities[12] = backDensities[(baseV + 2) * sampleDimension + baseU + 2];
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

	private static float Evaluate( Vector3 world, int ruleVersion )
	{
		if ( ruleVersion == 0 ) return world.z;
		var rolling = System.MathF.Sin( world.x * 0.0031f ) * 2.5f + System.MathF.Cos( world.y * 0.0027f ) * 2.0f;
		return world.z + 16.0f - rolling;
	}

	private FaceBasis GetBasis( VoxelVisualBlockKey fine, VoxelLodFaceDirection face, int spacing )
	{
		var origin = new Vector3( fine.Coordinate.x * _chunkSize * spacing * _voxelSize, fine.Coordinate.y * _chunkSize * spacing * _voxelSize, (fine.Coordinate.z - 1) * _chunkSize * spacing * _voxelSize );
		var scale = Vector3.One * _voxelSize;
		return face switch
		{
			VoxelLodFaceDirection.NegativeX => new( origin, new Vector3( 0, 0, 1 ) * scale, new Vector3( 0, 1, 0 ) * scale, new Vector3( -1, 0, 0 ) * scale ),
			VoxelLodFaceDirection.PositiveX => new( origin + new Vector3( _chunkSize * spacing * _voxelSize, 0, 0 ), new Vector3( 0, 0, -1 ) * scale, new Vector3( 0, 1, 0 ) * scale, new Vector3( 1, 0, 0 ) * scale ),
			VoxelLodFaceDirection.NegativeY => new( origin, new Vector3( 1, 0, 0 ) * scale, new Vector3( 0, 0, 1 ) * scale, new Vector3( 0, -1, 0 ) * scale ),
			VoxelLodFaceDirection.PositiveY => new( origin + new Vector3( 0, _chunkSize * spacing * _voxelSize, 0 ), new Vector3( 1, 0, 0 ) * scale, new Vector3( 0, 0, -1 ) * scale, new Vector3( 0, 1, 0 ) * scale ),
			VoxelLodFaceDirection.NegativeZ => new( origin, new Vector3( 1, 0, 0 ) * scale, new Vector3( 0, 1, 0 ) * scale, new Vector3( 0, 0, -1 ) * scale ),
			_ => new( origin + new Vector3( 0, 0, _chunkSize * spacing * _voxelSize ), new Vector3( -1, 0, 0 ) * scale, new Vector3( 0, 1, 0 ) * scale, new Vector3( 0, 0, 1 ) * scale )
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
