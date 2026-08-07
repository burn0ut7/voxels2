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

internal static class VoxelCollisionMesher
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

	public static VoxelMeshData Build( VoxelChunk chunk, float voxelSize, int resolutionDivisor )
	{
		var distanceSamples = CreateDistanceSnapshot( chunk, resolutionDivisor );
		return Build( distanceSamples, chunk.Size, voxelSize, resolutionDivisor );
	}

	public static float[] CreateDistanceSnapshot( VoxelChunk chunk, int resolutionDivisor )
	{
		resolutionDivisor = System.Math.Clamp( resolutionDivisor, 1, chunk.Size );
		var size = (chunk.Size + resolutionDivisor - 1) / resolutionDivisor;
		var sampleSize = size + 1;
		var distanceSamples = new float[checked( sampleSize * sampleSize * sampleSize )];
		for ( var z = 0; z < sampleSize; z++ )
		for ( var y = 0; y < sampleSize; y++ )
		for ( var x = 0; x < sampleSize; x++ )
		{
			distanceSamples[x + sampleSize * (y + sampleSize * z)] = chunk.GetVoxel(
				System.Math.Min( x * resolutionDivisor, chunk.Size ),
				System.Math.Min( y * resolutionDivisor, chunk.Size ),
				System.Math.Min( z * resolutionDivisor, chunk.Size )
			).Distance;
		}
		return distanceSamples;
	}

	public static VoxelMeshData Build( float[] distanceSamples, int chunkSize, float voxelSize, int resolutionDivisor )
	{
		resolutionDivisor = System.Math.Clamp( resolutionDivisor, 1, chunkSize );
		var size = (chunkSize + resolutionDivisor - 1) / resolutionDivisor;
		var estimatedSurfaceVertices = checked( size * size * 4 );
		var meshData = new VoxelMeshData( estimatedSurfaceVertices, estimatedSurfaceVertices * 3 );
		var edgeVertices = new Dictionary<long, int>( estimatedSurfaceVertices );
		var sampleSize = size + 1;
		var sampleLayer = sampleSize * sampleSize;
		var collisionSampleCount = checked( sampleSize * sampleSize * sampleSize );
		if ( distanceSamples is null || distanceSamples.Length < collisionSampleCount )
		{
			throw new System.ArgumentException( "Collision distance snapshot is smaller than the requested resolution.", nameof( distanceSamples ) );
		}
		var sampleCoordinates = System.Buffers.ArrayPool<int>.Shared.Rent( sampleSize );
		for ( var index = 0; index < sampleSize; index++ )
		{
			sampleCoordinates[index] = System.Math.Min( index * resolutionDivisor, chunkSize );
		}

		System.Span<int> sampleIndices = stackalloc int[8];
		System.Span<Vector3> positions = stackalloc Vector3[8];
		System.Span<float> distances = stackalloc float[8];

		try
		{
			for ( var z = 0; z < size; z++ )
			{
				for ( var y = 0; y < size; y++ )
				{
					for ( var x = 0; x < size; x++ )
					{
						var firstSample = x + sampleSize * (y + sampleSize * z);
						SetCubeCorners( sampleIndices, positions, firstSample, x, y, z, sampleSize, sampleLayer, sampleCoordinates );

						var hasSolid = false;
						var hasAir = false;
						for ( var corner = 0; corner < 8; corner++ )
						{
							var distance = distanceSamples[sampleIndices[corner]];
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
							PolygonizeTetrahedron( distanceSamples, sampleSize, voxelSize, tetrahedron, sampleIndices, positions, distances, edgeVertices, meshData );
						}
					}
				}
			}
		}
		finally
		{
			System.Buffers.ArrayPool<int>.Shared.Return( sampleCoordinates );
		}

		return meshData;
	}

	private static void SetCubeCorners( System.Span<int> indices, System.Span<Vector3> positions, int first, int x, int y, int z, int sampleSize, int sampleLayer, int[] sampleCoordinates )
	{
		indices[0] = first;
		indices[1] = first + 1;
		indices[2] = first + sampleSize + 1;
		indices[3] = first + sampleSize;
		indices[4] = first + sampleLayer;
		indices[5] = first + sampleLayer + 1;
		indices[6] = first + sampleLayer + sampleSize + 1;
		indices[7] = first + sampleLayer + sampleSize;

		var x0 = sampleCoordinates[x];
		var x1 = sampleCoordinates[x + 1];
		var y0 = sampleCoordinates[y];
		var y1 = sampleCoordinates[y + 1];
		var z0 = sampleCoordinates[z];
		var z1 = sampleCoordinates[z + 1];
		positions[0] = new Vector3( x0, y0, z0 );
		positions[1] = new Vector3( x1, y0, z0 );
		positions[2] = new Vector3( x1, y1, z0 );
		positions[3] = new Vector3( x0, y1, z0 );
		positions[4] = new Vector3( x0, y0, z1 );
		positions[5] = new Vector3( x1, y0, z1 );
		positions[6] = new Vector3( x1, y1, z1 );
		positions[7] = new Vector3( x0, y1, z1 );
	}

	private static void PolygonizeTetrahedron(
		float[] distanceSamples,
		int sampleSize,
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
			var first = GetOrCreateVertex( distanceSamples, sampleSize, voxelSize, tetrahedronOffset, TriangleTable[tableOffset + edge + 1], cubeSampleIndices, cubePositions, cubeDistances, edgeVertices, meshData.Vertices );
			var second = GetOrCreateVertex( distanceSamples, sampleSize, voxelSize, tetrahedronOffset, TriangleTable[tableOffset + edge + 2], cubeSampleIndices, cubePositions, cubeDistances, edgeVertices, meshData.Vertices );
			var third = GetOrCreateVertex( distanceSamples, sampleSize, voxelSize, tetrahedronOffset, TriangleTable[tableOffset + edge + 3], cubeSampleIndices, cubePositions, cubeDistances, edgeVertices, meshData.Vertices );

			if ( first == second || second == third || third == first )
			{
				continue;
			}

			AddOutwardTriangle( first, second, third, meshData );
		}
	}

	private static int GetOrCreateVertex(
		float[] distanceSamples,
		int sampleSize,
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
		var gradientA = GetGradient( sampleSize, distanceSamples, sampleA );
		var gradientB = GetGradient( sampleSize, distanceSamples, sampleB );
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

	private static Vector3 GetGradient( int sampleSize, float[] distances, int sampleIndex )
	{
		var sampleLayer = sampleSize * sampleSize;
		var z = sampleIndex / sampleLayer;
		var remainder = sampleIndex - z * sampleLayer;
		var y = remainder / sampleSize;
		var x = remainder - y * sampleSize;
		var center = distances[sampleIndex];

		var xBefore = x > 0 ? distances[sampleIndex - 1] : center;
		var xAfter = x + 1 < sampleSize ? distances[sampleIndex + 1] : center;
		var yBefore = y > 0 ? distances[sampleIndex - sampleSize] : center;
		var yAfter = y + 1 < sampleSize ? distances[sampleIndex + sampleSize] : center;
		var zBefore = z > 0 ? distances[sampleIndex - sampleLayer] : center;
		var zAfter = z + 1 < sampleSize ? distances[sampleIndex + sampleLayer] : center;

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
