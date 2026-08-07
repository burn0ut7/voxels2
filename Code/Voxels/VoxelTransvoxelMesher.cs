internal static class VoxelTransvoxelMesher
{
	private const float SurfaceEpsilon = 0.000001f;
	private const int MaximumRegularVertices = 12;
	private const int MaximumRegularTriangleIndices = 15;

	// Transvoxel's regular-cell corner numbering is binary XYZ: bit 0 = X,
	// bit 1 = Y, bit 2 = Z. This differs from the classic MC ring ordering.
	private static readonly Vector3Int[] Corners =
	{
		new( 0, 0, 0 ), new( 1, 0, 0 ), new( 0, 1, 0 ), new( 1, 1, 0 ),
		new( 0, 0, 1 ), new( 1, 0, 1 ), new( 0, 1, 1 ), new( 1, 1, 1 )
	};

	static VoxelTransvoxelMesher()
	{
		if ( VoxelTransvoxelTables.RegularCellClass.Length != 256 ||
			VoxelTransvoxelTables.RegularGeometryCounts.Length != 16 ||
			VoxelTransvoxelTables.RegularTriangleIndices.Length != 16 * MaximumRegularTriangleIndices ||
			VoxelTransvoxelTables.RegularVertexData.Length != 256 * MaximumRegularVertices )
		{
			throw new System.InvalidOperationException( "Transvoxel regular-cell lookup data is incomplete." );
		}
	}

	public static VoxelMeshData Build( float[] halo, int chunkSize, float voxelSize )
	{
		var haloSize = chunkSize + 3;
		if ( halo is null || halo.Length != checked( haloSize * haloSize * haloSize ) )
		{
			throw new System.ArgumentException( "Transvoxel regular cells require a one-sample SDF halo.", nameof( halo ) );
		}

		var sampleSize = chunkSize + 1;
		var edgeSlots = new int[checked( sampleSize * sampleSize * sampleSize * 3 )];
		System.Array.Fill( edgeSlots, -1 );
		var estimatedVertices = checked( chunkSize * chunkSize * 2 );
		var mesh = new VoxelMeshData( estimatedVertices, estimatedVertices * 6 );
		System.Span<float> values = stackalloc float[8];
		System.Span<int> cellVertices = stackalloc int[MaximumRegularVertices];

		for ( var z = 0; z < chunkSize; z++ )
		for ( var y = 0; y < chunkSize; y++ )
		for ( var x = 0; x < chunkSize; x++ )
		{
			var caseCode = 0;
			for ( var corner = 0; corner < Corners.Length; corner++ )
			{
				var offset = Corners[corner];
				var value = Distance( halo, haloSize, x + offset.x, y + offset.y, z + offset.z );
				values[corner] = value;
				if ( value < 0.0f ) caseCode |= 1 << corner;
			}

			if ( caseCode is 0 or 255 ) continue;

			var cellClass = VoxelTransvoxelTables.RegularCellClass[caseCode];
			var geometryCounts = VoxelTransvoxelTables.RegularGeometryCounts[cellClass];
			var vertexCount = geometryCounts >> 4;
			var triangleCount = geometryCounts & 0x0F;
			var vertexDataOffset = caseCode * MaximumRegularVertices;

			for ( var vertex = 0; vertex < vertexCount; vertex++ )
			{
				var vertexData = VoxelTransvoxelTables.RegularVertexData[vertexDataOffset + vertex];
				var edgeCode = vertexData & 0xFF;
				var firstCorner = (edgeCode >> 4) & 0x0F;
				var secondCorner = edgeCode & 0x0F;
				cellVertices[vertex] = GetOrCreateEdgeVertex(
					halo,
					haloSize,
					sampleSize,
					voxelSize,
					x,
					y,
					z,
					firstCorner,
					secondCorner,
					values,
					edgeSlots,
					mesh
				);
			}

			var triangleOffset = cellClass * MaximumRegularTriangleIndices;
			for ( var triangle = 0; triangle < triangleCount; triangle++ )
			{
				var indexOffset = triangleOffset + triangle * 3;
				var first = cellVertices[VoxelTransvoxelTables.RegularTriangleIndices[indexOffset]];
				var second = cellVertices[VoxelTransvoxelTables.RegularTriangleIndices[indexOffset + 1]];
				var third = cellVertices[VoxelTransvoxelTables.RegularTriangleIndices[indexOffset + 2]];
				if ( first == second || second == third || third == first ) continue;
				AddOutwardTriangle( first, second, third, mesh );
			}
		}

		return mesh;
	}

	private static int GetOrCreateEdgeVertex(
		float[] halo,
		int haloSize,
		int sampleSize,
		float voxelSize,
		int x,
		int y,
		int z,
		int firstCorner,
		int secondCorner,
		System.ReadOnlySpan<float> values,
		int[] edgeSlots,
		VoxelMeshData mesh )
	{
		if ( (uint)firstCorner >= Corners.Length || (uint)secondCorner >= Corners.Length )
		{
			throw new System.InvalidOperationException( "Transvoxel regular-cell data referenced an invalid corner." );
		}

		var firstOffset = Corners[firstCorner];
		var secondOffset = Corners[secondCorner];
		var firstX = x + firstOffset.x;
		var firstY = y + firstOffset.y;
		var firstZ = z + firstOffset.z;
		var secondX = x + secondOffset.x;
		var secondY = y + secondOffset.y;
		var secondZ = z + secondOffset.z;

		var deltaX = System.Math.Abs( secondX - firstX );
		var deltaY = System.Math.Abs( secondY - firstY );
		var deltaZ = System.Math.Abs( secondZ - firstZ );
		if ( deltaX + deltaY + deltaZ != 1 )
		{
			throw new System.InvalidOperationException( "Transvoxel regular-cell data referenced a non-edge vertex." );
		}

		var baseX = System.Math.Min( firstX, secondX );
		var baseY = System.Math.Min( firstY, secondY );
		var baseZ = System.Math.Min( firstZ, secondZ );
		var axis = deltaX != 0 ? 0 : deltaY != 0 ? 1 : 2;
		var slot = ((baseX + sampleSize * (baseY + sampleSize * baseZ)) * 3) + axis;
		if ( edgeSlots[slot] >= 0 ) return edgeSlots[slot];

		var firstDistance = values[firstCorner];
		var secondDistance = values[secondCorner];
		var denominator = firstDistance - secondDistance;
		var interpolation = System.MathF.Abs( denominator ) > SurfaceEpsilon ? firstDistance / denominator : 0.5f;
		interpolation = System.Math.Clamp( interpolation, 0.0f, 1.0f );

		var firstPosition = new Vector3( firstX, firstY, firstZ );
		var secondPosition = new Vector3( secondX, secondY, secondZ );
		var position = Vector3.Lerp( firstPosition, secondPosition, interpolation ) * voxelSize;
		var firstGradient = Gradient( halo, haloSize, firstX, firstY, firstZ );
		var secondGradient = Gradient( halo, haloSize, secondX, secondY, secondZ );
		var normal = SafeNormal( Vector3.Lerp( firstGradient, secondGradient, interpolation ), Vector3.Up );
		var vertexIndex = AddVertex( position, normal, mesh );
		edgeSlots[slot] = vertexIndex;
		return vertexIndex;
	}

	private static float Distance( float[] halo, int size, int x, int y, int z )
	{
		var value = Raw( halo, size, x, y, z );
		return System.MathF.Abs( value ) < SurfaceEpsilon ? SurfaceEpsilon : value;
	}

	private static Vector3 Gradient( float[] halo, int size, int x, int y, int z ) => new(
		Raw( halo, size, x + 1, y, z ) - Raw( halo, size, x - 1, y, z ),
		Raw( halo, size, x, y + 1, z ) - Raw( halo, size, x, y - 1, z ),
		Raw( halo, size, x, y, z + 1 ) - Raw( halo, size, x, y, z - 1 )
	);

	private static float Raw( float[] halo, int size, int x, int y, int z ) =>
		halo[(x + 1) + size * ((y + 1) + size * (z + 1))];

	private static Vector3 SafeNormal( Vector3 value, Vector3 fallback ) =>
		value.LengthSquared > 0.000000000001f ? value.Normal : fallback;

	private static int AddVertex( Vector3 position, Vector3 normal, VoxelMeshData mesh )
	{
		var tangent = System.MathF.Abs( normal.z ) < 0.999f ? Vector3.Cross( Vector3.Up, normal ).Normal : Vector3.Right;
		var vertexIndex = mesh.Vertices.Count;
		mesh.Vertices.Add( new Vertex
		{
			Position = position,
			Normal = normal,
			Tangent = new Vector4( tangent.x, tangent.y, tangent.z, 1.0f ),
			TexCoord0 = new Vector4( position.x / 128.0f, position.y / 128.0f, 0.0f, 0.0f ),
			Color = Color.White
		} );
		return vertexIndex;
	}

	private static void AddOutwardTriangle( int first, int second, int third, VoxelMeshData mesh )
	{
		var a = mesh.Vertices[first];
		var b = mesh.Vertices[second];
		var c = mesh.Vertices[third];
		var faceNormal = Vector3.Cross( b.Position - a.Position, c.Position - a.Position );
		if ( faceNormal.LengthSquared <= 0.000000000001f ) return;

		mesh.Indices.Add( first );
		if ( Vector3.Dot( faceNormal, a.Normal + b.Normal + c.Normal ) >= 0.0f )
		{
			mesh.Indices.Add( second );
			mesh.Indices.Add( third );
		}
		else
		{
			mesh.Indices.Add( third );
			mesh.Indices.Add( second );
		}
	}
}
