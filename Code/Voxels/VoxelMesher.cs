internal sealed class VoxelMeshData
{
	public List<Vertex> Vertices { get; }
	public List<int> Indices { get; }

	public VoxelMeshData( int estimatedVertexCount, int estimatedIndexCount )
	{
		Vertices = new List<Vertex>( estimatedVertexCount );
		Indices = new List<int>( estimatedIndexCount );
	}
}

internal static class VoxelMesher
{
	private static readonly sbyte[] Tetrahedra =
	{
		0, 5, 1, 6,
		0, 1, 2, 6,
		0, 2, 3, 6,
		0, 3, 7, 6,
		0, 7, 4, 6,
		0, 4, 5, 6
	};

	private static readonly sbyte[] EdgeStart = { 0, 1, 2, 0, 1, 2 };
	private static readonly sbyte[] EdgeEnd = { 1, 2, 0, 3, 3, 3 };

	private static readonly sbyte[] TriangleTable =
	{
		0, -1, -1, -1, -1, -1, -1,
		3, 0, 3, 2, -1, -1, -1,
		3, 0, 1, 4, -1, -1, -1,
		6, 1, 4, 2, 2, 4, 3,
		3, 1, 2, 5, -1, -1, -1,
		6, 0, 3, 5, 0, 5, 1,
		6, 0, 2, 5, 0, 5, 4,
		3, 5, 4, 3, -1, -1, -1,
		3, 3, 4, 5, -1, -1, -1,
		6, 4, 5, 0, 5, 2, 0,
		6, 1, 5, 0, 5, 3, 0,
		3, 5, 2, 1, -1, -1, -1,
		6, 3, 4, 2, 2, 4, 1,
		3, 4, 1, 0, -1, -1, -1,
		3, 2, 3, 0, -1, -1, -1,
		0, -1, -1, -1, -1, -1, -1
	};

	public static VoxelMeshData Build( VoxelChunk chunk, float voxelSize )
	{
		var size = chunk.Size;
		var estimatedSurfaceVertices = checked( size * size * 4 );
		var meshData = new VoxelMeshData( estimatedSurfaceVertices, estimatedSurfaceVertices * 3 );
		var edgeVertices = new Dictionary<long, int>( estimatedSurfaceVertices );
		var sampleSize = chunk.SampleSize;
		var sampleLayer = sampleSize * sampleSize;
		var voxels = chunk.Voxels;

		System.Span<int> sampleIndices = stackalloc int[8];
		System.Span<Vector3> positions = stackalloc Vector3[8];
		System.Span<float> distances = stackalloc float[8];

		for ( var z = 0; z < size; z++ )
		{
			for ( var y = 0; y < size; y++ )
			{
				for ( var x = 0; x < size; x++ )
				{
					var firstSample = x + sampleSize * (y + sampleSize * z);
					SetCubeCorners( sampleIndices, positions, firstSample, x, y, z, sampleSize, sampleLayer );

					var hasSolid = false;
					var hasAir = false;
					for ( var corner = 0; corner < 8; corner++ )
					{
						var distance = voxels[sampleIndices[corner]].Distance;
						distances[corner] = distance;
						hasSolid |= distance < 0.0f;
						hasAir |= distance >= 0.0f;
					}

					if ( !hasSolid || !hasAir )
					{
						continue;
					}

					for ( var tetrahedron = 0; tetrahedron < 6; tetrahedron++ )
					{
						PolygonizeTetrahedron( chunk, voxelSize, tetrahedron, sampleIndices, positions, distances, edgeVertices, meshData );
					}
				}
			}
		}

		return meshData;
	}

	private static void SetCubeCorners( System.Span<int> indices, System.Span<Vector3> positions, int first, int x, int y, int z, int sampleSize, int sampleLayer )
	{
		indices[0] = first;
		indices[1] = first + 1;
		indices[2] = first + sampleSize + 1;
		indices[3] = first + sampleSize;
		indices[4] = first + sampleLayer;
		indices[5] = first + sampleLayer + 1;
		indices[6] = first + sampleLayer + sampleSize + 1;
		indices[7] = first + sampleLayer + sampleSize;

		positions[0] = new Vector3( x, y, z );
		positions[1] = new Vector3( x + 1, y, z );
		positions[2] = new Vector3( x + 1, y + 1, z );
		positions[3] = new Vector3( x, y + 1, z );
		positions[4] = new Vector3( x, y, z + 1 );
		positions[5] = new Vector3( x + 1, y, z + 1 );
		positions[6] = new Vector3( x + 1, y + 1, z + 1 );
		positions[7] = new Vector3( x, y + 1, z + 1 );
	}

	private static void PolygonizeTetrahedron(
		VoxelChunk chunk,
		float voxelSize,
		int tetrahedron,
		System.Span<int> cubeSampleIndices,
		System.Span<Vector3> cubePositions,
		System.Span<float> cubeDistances,
		Dictionary<long, int> edgeVertices,
		VoxelMeshData meshData )
	{
		var tetrahedronOffset = tetrahedron * 4;
		var caseIndex = 0;
		for ( var corner = 0; corner < 4; corner++ )
		{
			var cubeCorner = Tetrahedra[tetrahedronOffset + corner];
			if ( cubeDistances[cubeCorner] < 0.0f )
			{
				caseIndex |= 1 << corner;
			}
		}

		var tableOffset = caseIndex * 7;
		var edgeCount = TriangleTable[tableOffset];
		for ( var edge = 0; edge < edgeCount; edge += 3 )
		{
			var first = GetOrCreateVertex( chunk, voxelSize, tetrahedronOffset, TriangleTable[tableOffset + edge + 1], cubeSampleIndices, cubePositions, cubeDistances, edgeVertices, meshData.Vertices );
			var second = GetOrCreateVertex( chunk, voxelSize, tetrahedronOffset, TriangleTable[tableOffset + edge + 2], cubeSampleIndices, cubePositions, cubeDistances, edgeVertices, meshData.Vertices );
			var third = GetOrCreateVertex( chunk, voxelSize, tetrahedronOffset, TriangleTable[tableOffset + edge + 3], cubeSampleIndices, cubePositions, cubeDistances, edgeVertices, meshData.Vertices );

			if ( first == second || second == third || third == first )
			{
				continue;
			}

			AddOutwardTriangle( first, second, third, meshData );
		}
	}

	private static int GetOrCreateVertex(
		VoxelChunk chunk,
		float voxelSize,
		int tetrahedronOffset,
		int tetrahedronEdge,
		System.Span<int> cubeSampleIndices,
		System.Span<Vector3> cubePositions,
		System.Span<float> cubeDistances,
		Dictionary<long, int> edgeVertices,
		List<Vertex> vertices )
	{
		var tetrahedronCornerA = EdgeStart[tetrahedronEdge];
		var tetrahedronCornerB = EdgeEnd[tetrahedronEdge];
		var cubeCornerA = Tetrahedra[tetrahedronOffset + tetrahedronCornerA];
		var cubeCornerB = Tetrahedra[tetrahedronOffset + tetrahedronCornerB];
		var sampleA = cubeSampleIndices[cubeCornerA];
		var sampleB = cubeSampleIndices[cubeCornerB];
		var key = CreateEdgeKey( sampleA, sampleB );

		if ( edgeVertices.TryGetValue( key, out var existingVertex ) )
		{
			return existingVertex;
		}

		var distanceA = cubeDistances[cubeCornerA];
		var distanceB = cubeDistances[cubeCornerB];
		var denominator = distanceA - distanceB;
		var interpolation = System.MathF.Abs( denominator ) > 0.000001f ? distanceA / denominator : 0.5f;
		interpolation = System.Math.Clamp( interpolation, 0.0f, 1.0f );

		var position = Vector3.Lerp( cubePositions[cubeCornerA], cubePositions[cubeCornerB], interpolation ) * voxelSize;
		var gradientA = GetGradient( chunk, sampleA );
		var gradientB = GetGradient( chunk, sampleB );
		var normal = Vector3.Lerp( gradientA, gradientB, interpolation );
		normal = normal.LengthSquared > 0.000001f ? normal.Normal : Vector3.Up;
		var tangent = CreateTangent( normal );

		var vertexIndex = vertices.Count;
		vertices.Add( new Vertex
		{
			Position = position,
			Normal = normal,
			Tangent = tangent,
			TexCoord0 = new Vector4( position.x / 128.0f, position.y / 128.0f, 0.0f, 0.0f ),
			Color = Color.White
		} );
		edgeVertices.Add( key, vertexIndex );
		return vertexIndex;
	}

	private static Vector3 GetGradient( VoxelChunk chunk, int sampleIndex )
	{
		var sampleSize = chunk.SampleSize;
		var sampleLayer = sampleSize * sampleSize;
		var z = sampleIndex / sampleLayer;
		var remainder = sampleIndex - z * sampleLayer;
		var y = remainder / sampleSize;
		var x = remainder - y * sampleSize;
		var voxels = chunk.Voxels;
		var center = voxels[sampleIndex].Distance;

		var xBefore = x > 0 ? voxels[sampleIndex - 1].Distance : center;
		var xAfter = x + 1 < sampleSize ? voxels[sampleIndex + 1].Distance : center;
		var yBefore = y > 0 ? voxels[sampleIndex - sampleSize].Distance : center;
		var yAfter = y + 1 < sampleSize ? voxels[sampleIndex + sampleSize].Distance : center;
		var zBefore = z > 0 ? voxels[sampleIndex - sampleLayer].Distance : center;
		var zAfter = z + 1 < sampleSize ? voxels[sampleIndex + sampleLayer].Distance : center;

		return new Vector3( xAfter - xBefore, yAfter - yBefore, zAfter - zBefore );
	}

	private static Vector4 CreateTangent( Vector3 normal )
	{
		var tangent = System.MathF.Abs( normal.z ) < 0.999f
			? Vector3.Cross( Vector3.Up, normal ).Normal
			: new Vector3( 1.0f, 0.0f, 0.0f );

		return new Vector4( tangent.x, tangent.y, tangent.z, 1.0f );
	}

	private static long CreateEdgeKey( int sampleA, int sampleB )
	{
		var minimum = System.Math.Min( sampleA, sampleB );
		var maximum = System.Math.Max( sampleA, sampleB );
		return ((long)minimum << 32) | (uint)maximum;
	}

	private static void AddOutwardTriangle( int first, int second, int third, VoxelMeshData meshData )
	{
		var firstVertex = meshData.Vertices[first];
		var secondVertex = meshData.Vertices[second];
		var thirdVertex = meshData.Vertices[third];
		var faceNormal = Vector3.Cross( secondVertex.Position - firstVertex.Position, thirdVertex.Position - firstVertex.Position );
		var expectedNormal = firstVertex.Normal + secondVertex.Normal + thirdVertex.Normal;

		meshData.Indices.Add( first );
		if ( Vector3.Dot( faceNormal, expectedNormal ) >= 0.0f )
		{
			meshData.Indices.Add( second );
			meshData.Indices.Add( third );
		}
		else
		{
			meshData.Indices.Add( third );
			meshData.Indices.Add( second );
		}
	}
}
