internal readonly record struct VoxelTransvoxelTransitionProofResult(
	bool Passed,
	string Failure,
	int Cases,
	int Orientations,
	int FixtureCases,
	int ValidatedTriangles,
	int BoundaryEdges,
	int GradientNormals,
	float PositionTolerance,
	bool TablesValidated );

internal static class VoxelTransvoxelTransitionMesher
{
	private const int MaximumVerticesPerCell = 12;
	private const int MaximumIndicesPerCell = 36;
	private const float SurfaceEpsilon = 0.000001f;
	private static readonly int[] CaseSampleOrder = { 0, 1, 2, 5, 8, 7, 6, 3, 4 };
	private static readonly Vector3Int[] SampleCoordinates =
	{
		new( 0, 0, 0 ), new( 1, 0, 0 ), new( 2, 0, 0 ),
		new( 0, 1, 0 ), new( 1, 1, 0 ), new( 2, 1, 0 ),
		new( 0, 2, 0 ), new( 1, 2, 0 ), new( 2, 2, 0 )
	};
	private static readonly int[][] BoundaryEdges =
	{
		new[] { 0, 1 }, new[] { 1, 2 }, new[] { 2, 5 }, new[] { 5, 8 },
		new[] { 8, 7 }, new[] { 7, 6 }, new[] { 6, 3 }, new[] { 3, 0 }
	};

	static VoxelTransvoxelTransitionMesher()
	{
		if ( VoxelTransvoxelTransitionTables.CellClass.Length != 512 ||
			VoxelTransvoxelTransitionTables.GeometryCounts.Length != 56 ||
			VoxelTransvoxelTransitionTables.TriangleIndices.Length != 56 * MaximumIndicesPerCell ||
			VoxelTransvoxelTransitionTables.CornerData.Length != 13 ||
			VoxelTransvoxelTransitionTables.VertexData.Length != 512 * MaximumVerticesPerCell )
		{
			throw new System.InvalidOperationException( "Transvoxel transition lookup data is incomplete." );
		}
	}

	public static VoxelMeshData BuildCell( System.ReadOnlySpan<float> samples, System.ReadOnlySpan<Vector3> gradients, VoxelClipboxFaceDirection face, float voxelSize, bool reverseWinding = false )
	{
		if ( samples.Length != 13 ) throw new System.ArgumentException( "A transition cell requires thirteen samples.", nameof( samples ) );
		if ( gradients.Length != 13 ) throw new System.ArgumentException( "A transition cell requires thirteen gradients.", nameof( gradients ) );
		if ( voxelSize <= 0.0f ) throw new System.ArgumentOutOfRangeException( nameof( voxelSize ) );

		var caseCode = GetCaseCode( samples );
		var cellClass = VoxelTransvoxelTransitionTables.CellClass[caseCode];
		var geometryCounts = VoxelTransvoxelTransitionTables.GeometryCounts[cellClass & 0x7F];
		var vertexCount = geometryCounts >> 4;
		var triangleCount = geometryCounts & 0x0F;
		var mesh = new VoxelMeshData( vertexCount, triangleCount * 3 );
		if ( vertexCount == 0 ) return mesh;

		var cellVertices = new int[MaximumVerticesPerCell];
		var vertexDataOffset = caseCode * MaximumVerticesPerCell;
		for ( var vertex = 0; vertex < vertexCount; vertex++ )
		{
			var edgeCode = VoxelTransvoxelTransitionTables.VertexData[vertexDataOffset + vertex] & 0xFF;
			var firstSample = (edgeCode >> 4) & 0x0F;
			var secondSample = edgeCode & 0x0F;
			if ( (uint)firstSample >= 13 || (uint)secondSample >= 13 || firstSample == secondSample )
				throw new System.InvalidOperationException( $"Transition case {caseCode} referenced an invalid edge." );

			var denominator = samples[firstSample] - samples[secondSample];
			var interpolation = System.MathF.Abs( denominator ) > SurfaceEpsilon ? samples[firstSample] / denominator : 0.5f;
			interpolation = System.Math.Clamp( interpolation, 0.0f, 1.0f );
			var firstPosition = ToFacePosition( firstSample, face, voxelSize );
			var secondPosition = ToFacePosition( secondSample, face, voxelSize );
			var position = Vector3.Lerp( firstPosition, secondPosition, interpolation );
			var normal = SafeNormal( Vector3.Lerp( gradients[firstSample], gradients[secondSample], interpolation ), FaceNormal( face ) );
			cellVertices[vertex] = AddVertex( position, normal, mesh );
		}

		var triangleOffset = (cellClass & 0x7F) * MaximumIndicesPerCell;
		var flipTriangles = (cellClass & 0x80) != 0 ^ reverseWinding;
		for ( var triangle = 0; triangle < triangleCount; triangle++ )
		{
			var indexOffset = triangleOffset + triangle * 3;
			var first = cellVertices[VoxelTransvoxelTransitionTables.TriangleIndices[indexOffset]];
			var second = cellVertices[VoxelTransvoxelTransitionTables.TriangleIndices[indexOffset + 1]];
			var third = cellVertices[VoxelTransvoxelTransitionTables.TriangleIndices[indexOffset + 2]];
			var firstPosition = mesh.Vertices[first].Position;
			var secondPosition = mesh.Vertices[second].Position;
			var thirdPosition = mesh.Vertices[third].Position;
			if ( (secondPosition - firstPosition).Length <= SurfaceEpsilon ||
				(thirdPosition - firstPosition).Length <= SurfaceEpsilon ||
				(thirdPosition - secondPosition).Length <= SurfaceEpsilon ) continue;
			if ( flipTriangles )
			{
				mesh.Indices.Add( first ); mesh.Indices.Add( second ); mesh.Indices.Add( third );
			}
			else
			{
				mesh.Indices.Add( third ); mesh.Indices.Add( second ); mesh.Indices.Add( first );
			}
		}

		return mesh;
	}

	public static int GetCaseCode( System.ReadOnlySpan<float> samples )
	{
		if ( samples.Length != 13 ) throw new System.ArgumentException( "A transition cell requires thirteen samples.", nameof( samples ) );
		var caseCode = 0;
		for ( var bit = 0; bit < CaseSampleOrder.Length; bit++ )
			if ( samples[CaseSampleOrder[bit]] < 0.0f ) caseCode |= 1 << bit;
		return caseCode;
	}

	public static VoxelTransvoxelTransitionProofResult RunProof( float productionVoxelSize )
	{
		if ( productionVoxelSize <= 0.0f ) throw new System.ArgumentOutOfRangeException( nameof( productionVoxelSize ) );
		const float positionTolerance = 0.0001f;
		var cases = 0;
		var orientations = 0;
		var fixtureCases = 0;
		var triangles = 0;
		var boundaryEdges = 0;
		var gradientNormals = 0;

		for ( var caseCode = 0; caseCode < 512; caseCode++ )
		{
			var samples = BuildSyntheticCase( caseCode );
			var gradients = BuildSyntheticGradients();
			for ( var face = 0; face < 6; face++ )
			{
				var mesh = BuildCell( samples, gradients, (VoxelClipboxFaceDirection)face, 1.0f );
				var cellClass = VoxelTransvoxelTransitionTables.CellClass[caseCode];
				var geometryCounts = VoxelTransvoxelTransitionTables.GeometryCounts[cellClass & 0x7F];
				if ( mesh.Vertices.Count != geometryCounts >> 4 || mesh.Indices.Count != (geometryCounts & 0x0F) * 3 )
					throw new System.InvalidOperationException( $"Transition case {caseCode} changed non-degenerate golden topology." );
				ValidateMesh( mesh, caseCode, 1.0f, positionTolerance, ref triangles, ref gradientNormals );
				var reversed = BuildCell( samples, gradients, (VoxelClipboxFaceDirection)face, 1.0f, true );
				ValidateMesh( reversed, caseCode, 1.0f, positionTolerance, ref triangles, ref gradientNormals );
				orientations++;
			}
			cases++;
		}

		foreach ( var scale in new[] { 1.0f, productionVoxelSize } )
		foreach ( var fixture in System.Enum.GetValues<VoxelTransitionFixture>() )
		foreach ( var face in System.Enum.GetValues<VoxelClipboxFaceDirection>() )
		{
			var samples = BuildFixtureSamples( fixture, face, scale );
			var gradients = BuildFixtureGradients( fixture, face, scale );
			var mesh = BuildCell( samples, gradients, face, scale );
			try
			{
				ValidateMesh( mesh, -1, scale, positionTolerance, ref triangles, ref gradientNormals );
			}
			catch ( System.Exception exception )
			{
				throw new System.InvalidOperationException( $"Fixture {fixture}, scale {scale}, face {face}, case {GetCaseCode( samples )}: {exception.Message}", exception );
			}
			fixtureCases++;
		}

		boundaryEdges = ValidateBoundaryMatching( productionVoxelSize, positionTolerance );
		return new VoxelTransvoxelTransitionProofResult( true, string.Empty, cases, orientations, fixtureCases, triangles, boundaryEdges, gradientNormals, positionTolerance, true );
	}

	private static float[] BuildSyntheticCase( int caseCode )
	{
		var samples = new float[13];
		for ( var index = 0; index < 9; index++ )
		{
			var bit = System.Array.IndexOf( CaseSampleOrder, index );
			var magnitude = 0.65f + index * 0.071f;
			samples[index] = (caseCode & (1 << bit)) != 0 ? -magnitude : magnitude;
		}
		samples[9] = samples[0]; samples[10] = samples[2]; samples[11] = samples[6]; samples[12] = samples[8];
		return samples;
	}

	private static Vector3[] BuildSyntheticGradients()
	{
		var gradients = new Vector3[13];
		for ( var index = 0; index < gradients.Length; index++ ) gradients[index] = new Vector3( 0.6f, 0.8f, 0.25f ).Normal;
		return gradients;
	}

	private static float[] BuildFixtureSamples( VoxelTransitionFixture fixture, VoxelClipboxFaceDirection face, float scale )
	{
		var samples = new float[13];
		for ( var index = 0; index < 13; index++ ) samples[index] = FixtureValue( fixture, ToFacePosition( index, face, scale ) );
		return samples;
	}

	private static Vector3[] BuildFixtureGradients( VoxelTransitionFixture fixture, VoxelClipboxFaceDirection face, float scale )
	{
		var gradients = new Vector3[13];
		for ( var index = 0; index < gradients.Length; index++ )
		{
			var position = ToFacePosition( index, face, scale );
			var delta = System.MathF.Max( scale * 0.001f, 0.0001f );
			gradients[index] = new Vector3(
				FixtureValue( fixture, position + Vector3.Right * delta ) - FixtureValue( fixture, position - Vector3.Right * delta ),
				FixtureValue( fixture, position + Vector3.Up * delta ) - FixtureValue( fixture, position - Vector3.Up * delta ),
				FixtureValue( fixture, position + Vector3.Forward * delta ) - FixtureValue( fixture, position - Vector3.Forward * delta ) ).Normal;
		}
		return gradients;
	}

	private static float FixtureValue( VoxelTransitionFixture fixture, Vector3 position ) => fixture switch
	{
		VoxelTransitionFixture.Plane => position.x + position.y + position.z - 2.6f,
		VoxelTransitionFixture.Sphere => (position - new Vector3( 1.05f, 1.0f, 1.0f )).Length - 0.92f,
		VoxelTransitionFixture.CaveMouth => System.MathF.Max( position.x - 0.85f, System.MathF.Abs( position.y - 1.0f ) - 0.7f ) + position.z * 0.08f - 0.1f,
		VoxelTransitionFixture.Tangent => (position.x - 1.0f) * (position.x - 1.0f) + (position.y - 1.0f) * (position.y - 1.0f) - 0.62f,
		_ => 1.0f
	};

	private static int ValidateBoundaryMatching( float scale, float tolerance )
	{
		var face = VoxelClipboxFaceDirection.NegativeZ;
		var samples = BuildFixtureSamples( VoxelTransitionFixture.Plane, face, scale );
		var gradients = BuildFixtureGradients( VoxelTransitionFixture.Plane, face, scale );
		var mesh = BuildCell( samples, gradients, face, scale );
		var expected = new System.Collections.Generic.List<Vector3>();
		foreach ( var edge in BoundaryEdges )
		{
			var first = edge[0]; var second = edge[1];
			if ( (samples[first] < 0.0f) == (samples[second] < 0.0f) ) continue;
			var denominator = samples[first] - samples[second];
			var t = samples[first] / denominator;
			expected.Add( Vector3.Lerp( ToFacePosition( first, face, scale ), ToFacePosition( second, face, scale ), t ) );
		}

		var matched = 0;
		foreach ( var vertex in mesh.Vertices )
		{
			var p = vertex.Position / scale;
			if ( p.x > tolerance && p.x < 2.0f - tolerance && p.y > tolerance && p.y < 2.0f - tolerance ) continue;
			var found = false;
			foreach ( var position in expected )
				if ( (vertex.Position - position).Length <= tolerance ) { found = true; break; }
			if ( !found ) throw new System.InvalidOperationException( "Transition boundary vertex did not match the regular boundary edge reference." );
			matched++;
		}
		if ( matched == 0 && expected.Count != 0 ) throw new System.InvalidOperationException( "Transition boundary reference produced no matching vertices." );
		return matched;
	}

	private static void ValidateMesh( VoxelMeshData mesh, int caseCode, float scale, float tolerance, ref int triangles, ref int gradientNormals )
	{
		if ( mesh.Indices.Count % 3 != 0 ) throw new System.InvalidOperationException( "Transition output index count was not triangular." );
		for ( var index = 0; index < mesh.Indices.Count; index++ )
		{
			var vertex = mesh.Indices[index];
			if ( (uint)vertex >= mesh.Vertices.Count ) throw new System.InvalidOperationException( $"Transition case {caseCode} emitted an invalid index." );
			if ( mesh.Vertices[vertex].Normal.LengthSquared > 0.000001f ) gradientNormals++;
		}
		for ( var index = 0; index < mesh.Indices.Count; index += 3 )
		{
			var first = mesh.Indices[index]; var second = mesh.Indices[index + 1]; var third = mesh.Indices[index + 2];
			if ( first == second || second == third || third == first ) throw new System.InvalidOperationException( $"Transition case {caseCode} emitted a repeated triangle index." );
			var a = mesh.Vertices[first].Position; var b = mesh.Vertices[second].Position; var c = mesh.Vertices[third].Position;
			if ( (b - a).Length <= tolerance || (c - a).Length <= tolerance || (c - b).Length <= tolerance ) throw new System.InvalidOperationException( $"Transition case {caseCode} emitted a collapsed triangle." );
			if ( a.Length > scale * 4.0f || b.Length > scale * 4.0f || c.Length > scale * 4.0f ) throw new System.InvalidOperationException( $"Transition case {caseCode} emitted a world-spanning triangle." );
			triangles++;
		}
	}

	private static Vector3 ToFacePosition( int sample, VoxelClipboxFaceDirection face, float scale )
	{
		var coordinate = sample < 9 ? SampleCoordinates[sample] : sample switch
		{
			9 => new Vector3Int( 0, 0, 0 ),
			10 => new Vector3Int( 2, 0, 0 ),
			11 => new Vector3Int( 0, 2, 0 ),
			12 => new Vector3Int( 2, 2, 0 ),
			_ => throw new System.ArgumentOutOfRangeException( nameof( sample ) )
		};
		var position = face switch
		{
			VoxelClipboxFaceDirection.NegativeX => new Vector3( 0.0f, coordinate.x, coordinate.y ),
			VoxelClipboxFaceDirection.PositiveX => new Vector3( 2.0f, coordinate.y, coordinate.x ),
			VoxelClipboxFaceDirection.NegativeY => new Vector3( coordinate.y, 0.0f, coordinate.x ),
			VoxelClipboxFaceDirection.PositiveY => new Vector3( coordinate.x, 2.0f, coordinate.y ),
			VoxelClipboxFaceDirection.NegativeZ => new Vector3( coordinate.x, coordinate.y, 0.0f ),
			VoxelClipboxFaceDirection.PositiveZ => new Vector3( coordinate.y, coordinate.x, 2.0f ),
			_ => throw new System.ArgumentOutOfRangeException( nameof( face ) )
		};
		return position * scale;
	}

	private static Vector3 FaceNormal( VoxelClipboxFaceDirection face ) => face switch
	{
		VoxelClipboxFaceDirection.NegativeX => -Vector3.Right,
		VoxelClipboxFaceDirection.PositiveX => Vector3.Right,
		VoxelClipboxFaceDirection.NegativeY => -Vector3.Up,
		VoxelClipboxFaceDirection.PositiveY => Vector3.Up,
		VoxelClipboxFaceDirection.NegativeZ => -Vector3.Forward,
		VoxelClipboxFaceDirection.PositiveZ => Vector3.Forward,
		_ => Vector3.Up
	};

	private static Vector3 SafeNormal( Vector3 value, Vector3 fallback ) => value.LengthSquared > 0.000000000001f ? value.Normal : fallback;

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
}

internal enum VoxelTransitionFixture
{
	Plane,
	Sphere,
	CaveMouth,
	Tangent
}
