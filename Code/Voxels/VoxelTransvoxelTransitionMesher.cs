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
	bool TablesValidated,
	bool GpuCaseProofAvailable = false,
	bool GpuCaseProofPassed = false,
	string GpuCaseProofFailure = "",
	int GpuCaseVariants = 0,
	long GpuCaseBufferBytes = 0,
	double GpuCaseSubmissionMilliseconds = 0.0,
	double GpuCaseCompletionMilliseconds = 0.0,
	double GpuCaseReadbackMilliseconds = 0.0 );

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
			var samples = BuildSyntheticCaseForProof( caseCode );
			var gradients = BuildSyntheticGradientsForProof();
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

	internal static float[] BuildSyntheticCaseForProof( int caseCode )
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

	internal static Vector3[] BuildSyntheticGradientsForProof()
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
			var highFaceCoordinate = face switch
			{
				VoxelClipboxFaceDirection.NegativeX => vertex.Position.x,
				VoxelClipboxFaceDirection.PositiveX => vertex.Position.x - 2.0f * scale,
				VoxelClipboxFaceDirection.NegativeY => vertex.Position.y,
				VoxelClipboxFaceDirection.PositiveY => vertex.Position.y - 2.0f * scale,
				VoxelClipboxFaceDirection.NegativeZ => vertex.Position.z,
				VoxelClipboxFaceDirection.PositiveZ => vertex.Position.z - 2.0f * scale,
				_ => 0.0f
			};
			if ( System.MathF.Abs( highFaceCoordinate ) > tolerance ) continue;
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
		var coarseSample = sample >= 9;
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
			VoxelClipboxFaceDirection.NegativeX => new Vector3( coarseSample ? -2.0f : 0.0f, coordinate.x, coordinate.y ),
			VoxelClipboxFaceDirection.PositiveX => new Vector3( coarseSample ? 4.0f : 2.0f, coordinate.y, coordinate.x ),
			VoxelClipboxFaceDirection.NegativeY => new Vector3( coordinate.y, coarseSample ? -2.0f : 0.0f, coordinate.x ),
			VoxelClipboxFaceDirection.PositiveY => new Vector3( coordinate.x, coarseSample ? 4.0f : 2.0f, coordinate.y ),
			VoxelClipboxFaceDirection.NegativeZ => new Vector3( coordinate.x, coordinate.y, coarseSample ? -2.0f : 0.0f ),
			VoxelClipboxFaceDirection.PositiveZ => new Vector3( coordinate.y, coordinate.x, coarseSample ? 4.0f : 2.0f ),
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

internal readonly record struct VoxelGpuTransitionCaseProofResult(
	bool Passed,
	string Failure,
	int Variants,
	int MismatchedCases,
	int VertexCount,
	int TriangleCount,
	long GpuBufferBytes,
	double SubmissionMilliseconds,
	double CompletionMilliseconds,
	double ReadbackMilliseconds );

internal sealed class VoxelGpuTransitionCaseProof : SceneCustomObject, System.IDisposable
{
	private const int CaseCount = 512;
	private const int FaceCount = 6;
	private const int VariantCount = CaseCount * FaceCount * 2;
	private const int SamplesPerCase = 13;
	private const int VerticesPerCase = 12;
	private const int IndicesPerCase = 36;
	private const int GeometryOffset = CaseCount;
	private const int TriangleOffset = GeometryOffset + 56;
	private const int VertexOffset = TriangleOffset + 56 * IndicesPerCase;
	private const float PositionTolerance = 0.001f;

	private readonly ComputeShader _shader;
	private readonly GpuBuffer<float> _samples;
	private readonly GpuBuffer<uint> _lookup;
	private readonly GpuBuffer<Vector4> _outputVertices;
	private readonly GpuBuffer<uint> _outputIndices;
	private readonly GpuBuffer<uint> _statistics;
	private readonly Vector3[] _expectedVertices;
	private readonly uint[] _expectedIndices;
	private readonly Vector4[] _gpuVertices;
	private readonly uint[] _gpuIndices;
	private readonly uint[] _gpuStatistics = new uint[3];
	private readonly object _resultLock = new();
	private ProofState _state;
	private long _dispatchTimestamp;
	private double _submissionMilliseconds;
	private VoxelGpuTransitionCaseProofResult _result;
	private bool _hasResult;
	private bool _disposed;

	public bool IsRunning => !_disposed && _state is not ProofState.Idle and not ProofState.Complete and not ProofState.Failed;

	public VoxelGpuTransitionCaseProof( SceneWorld sceneWorld ) : base( sceneWorld )
	{
		var sampleData = new float[CaseCount * SamplesPerCase];
		_expectedVertices = new Vector3[VariantCount * VerticesPerCase];
		_expectedIndices = new uint[VariantCount * IndicesPerCase];
		_gpuVertices = new Vector4[_expectedVertices.Length];
		_gpuIndices = new uint[_expectedIndices.Length];
		var lookup = CreateLookup();
		for ( var caseCode = 0; caseCode < CaseCount; caseCode++ )
		{
			var samples = VoxelTransvoxelTransitionMesher.BuildSyntheticCaseForProof( caseCode );
			System.Array.Copy( samples, 0, sampleData, caseCode * SamplesPerCase, SamplesPerCase );
			var gradients = VoxelTransvoxelTransitionMesher.BuildSyntheticGradientsForProof();
			for ( var reverse = 0; reverse < 2; reverse++ )
			for ( var face = 0; face < FaceCount; face++ )
			{
				var variant = (reverse * FaceCount + face) * CaseCount + caseCode;
				var mesh = VoxelTransvoxelTransitionMesher.BuildCell( samples, gradients, (VoxelClipboxFaceDirection)face, 1.0f, reverse != 0 );
				for ( var vertex = 0; vertex < mesh.Vertices.Count; vertex++ ) _expectedVertices[variant * VerticesPerCase + vertex] = mesh.Vertices[vertex].Position;
				for ( var index = 0; index < mesh.Indices.Count; index++ ) _expectedIndices[variant * IndicesPerCase + index] = (uint)mesh.Indices[index];
			}
		}

		_samples = new GpuBuffer<float>( sampleData.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Proof Samples" );
		_lookup = new GpuBuffer<uint>( lookup.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Proof Lookup" );
		_outputVertices = new GpuBuffer<Vector4>( _gpuVertices.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Proof Vertices" );
		_outputIndices = new GpuBuffer<uint>( _gpuIndices.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Proof Indices" );
		_statistics = new GpuBuffer<uint>( _gpuStatistics.Length, GpuBuffer.UsageFlags.Structured, "Voxel GPU Transition Proof Statistics" );
		_samples.SetData( sampleData );
		_lookup.SetData( lookup );
		_shader = new ComputeShader( "shaders/voxel_gpu_transition_case_proof_cs.shader" );
		_shader.Attributes.Set( "Samples", _samples );
		_shader.Attributes.Set( "Lookup", _lookup );
		_shader.Attributes.Set( "OutputVertices", _outputVertices );
		_shader.Attributes.Set( "OutputIndices", _outputIndices );
		_shader.Attributes.Set( "Statistics", _statistics );
		_shader.Attributes.Set( "VariantCount", VariantCount );
		_shader.Attributes.Set( "GeometryOffset", GeometryOffset );
		_shader.Attributes.Set( "TriangleOffset", TriangleOffset );
		_shader.Attributes.Set( "VertexOffset", VertexOffset );
		Bounds = BBox.FromPositionAndSize( Vector3.Zero, Vector3.One * 1_000_000_000.0f );
	}

	public void Run()
	{
		if ( _disposed || _state != ProofState.Idle ) return;
		_hasResult = false;
		_state = ProofState.DispatchPending;
	}

	public bool TryTakeResult( out VoxelGpuTransitionCaseProofResult result )
	{
		lock ( _resultLock )
		{
			if ( !_hasResult ) { result = default; return false; }
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
			if ( _state == ProofState.ComputeExecutionPending ) { _state = ProofState.ReadbackPending; return; }
			if ( _state == ProofState.ReadbackPending )
			{
				var readbackStart = System.Diagnostics.Stopwatch.GetTimestamp();
				_statistics.GetData( _gpuStatistics );
				_outputVertices.GetData( _gpuVertices );
				_outputIndices.GetData( _gpuIndices );
				CompleteValidation( System.Diagnostics.Stopwatch.GetElapsedTime( readbackStart ).TotalMilliseconds );
				return;
			}
			if ( _state != ProofState.DispatchPending ) return;
			var start = System.Diagnostics.Stopwatch.GetTimestamp();
			_statistics.Clear();
			Graphics.ResourceBarrierTransition( _outputVertices, Sandbox.Rendering.ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( _outputIndices, Sandbox.Rendering.ResourceState.UnorderedAccess );
			Graphics.ResourceBarrierTransition( _statistics, Sandbox.Rendering.ResourceState.UnorderedAccess );
			_shader.Dispatch( VariantCount, 1, 1 );
			Graphics.UavBarrier( _outputVertices );
			Graphics.UavBarrier( _outputIndices );
			Graphics.UavBarrier( _statistics );
			_dispatchTimestamp = start;
			_submissionMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( start ).TotalMilliseconds;
			_state = ProofState.ComputeExecutionPending;
		}
		catch ( System.Exception exception )
		{
			CompleteFailure( exception.Message );
		}
	}

	private void CompleteValidation( double readbackMilliseconds )
	{
		var mismatches = (int)_gpuStatistics[0];
		var vertexCount = 0;
		var triangleCount = 0;
		var failure = string.Empty;
		for ( var variant = 0; variant < VariantCount && string.IsNullOrEmpty( failure ); variant++ )
		{
			var caseCode = variant % CaseCount;
			var cellClass = VoxelTransvoxelTransitionTables.CellClass[caseCode];
			var geometryCounts = VoxelTransvoxelTransitionTables.GeometryCounts[cellClass & 0x7F];
			var expectedVertexCount = geometryCounts >> 4;
			var expectedIndexCount = (geometryCounts & 0x0F) * 3;
			vertexCount += expectedVertexCount;
			triangleCount += expectedIndexCount / 3;
			for ( var vertex = 0; vertex < expectedVertexCount; vertex++ )
			{
				var expected = _expectedVertices[variant * VerticesPerCase + vertex];
				var actual = _gpuVertices[variant * VerticesPerCase + vertex];
				if ( (new Vector3( actual.x, actual.y, actual.z ) - expected).Length > PositionTolerance ) { mismatches++; failure = $"GPU transition vertex mismatch at variant {variant}, vertex {vertex}."; break; }
			}
			for ( var index = 0; string.IsNullOrEmpty( failure ) && index < expectedIndexCount; index++ )
			{
				var expectedIndex = (uint)(variant * VerticesPerCase) + _expectedIndices[variant * IndicesPerCase + index];
				if ( _gpuIndices[variant * IndicesPerCase + index] != expectedIndex ) { mismatches++; failure = $"GPU transition index mismatch at variant {variant}, index {index}: GPU={_gpuIndices[variant * IndicesPerCase + index]}, CPU={expectedIndex}."; }
			}
		}
		if ( string.IsNullOrEmpty( failure ) && _gpuStatistics[0] != 0 ) failure = $"GPU transition case classification mismatches={_gpuStatistics[0]}";
		if ( string.IsNullOrEmpty( failure ) && _gpuStatistics[1] != (uint)vertexCount ) failure = $"GPU transition vertex total mismatch: GPU={_gpuStatistics[1]}, CPU={vertexCount}.";
		if ( string.IsNullOrEmpty( failure ) && _gpuStatistics[2] != (uint)triangleCount ) failure = $"GPU transition triangle total mismatch: GPU={_gpuStatistics[2]}, CPU={triangleCount}.";
		var passed = string.IsNullOrEmpty( failure );
		_result = new VoxelGpuTransitionCaseProofResult( passed, failure, VariantCount, mismatches, vertexCount, triangleCount, GetBufferBytes(), _submissionMilliseconds, System.Diagnostics.Stopwatch.GetElapsedTime( _dispatchTimestamp ).TotalMilliseconds, readbackMilliseconds );
		_state = passed ? ProofState.Complete : ProofState.Failed;
		_hasResult = true;
	}

	private void CompleteFailure( string failure )
	{
		lock ( _resultLock )
		{
			_result = new VoxelGpuTransitionCaseProofResult( false, failure, VariantCount, 0, 0, 0, GetBufferBytes(), _submissionMilliseconds, _dispatchTimestamp == 0 ? 0.0 : System.Diagnostics.Stopwatch.GetElapsedTime( _dispatchTimestamp ).TotalMilliseconds, 0.0 );
			_state = ProofState.Failed;
			_hasResult = true;
		}
	}

	private long GetBufferBytes() => (long)_samples.ElementCount * sizeof( float ) + (long)_lookup.ElementCount * sizeof( uint ) + (long)_outputVertices.ElementCount * 16 + (long)_outputIndices.ElementCount * sizeof( uint ) + (long)_statistics.ElementCount * sizeof( uint );

	private static uint[] CreateLookup()
	{
		var values = new uint[VertexOffset + CaseCount * VerticesPerCase];
		var offset = 0;
		foreach ( var value in VoxelTransvoxelTransitionTables.CellClass ) values[offset++] = value;
		foreach ( var value in VoxelTransvoxelTransitionTables.GeometryCounts ) values[offset++] = value;
		foreach ( var value in VoxelTransvoxelTransitionTables.TriangleIndices ) values[offset++] = value;
		foreach ( var value in VoxelTransvoxelTransitionTables.VertexData ) values[offset++] = value;
		return values;
	}

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		_samples.Dispose(); _lookup.Dispose(); _outputVertices.Dispose(); _outputIndices.Dispose(); _statistics.Dispose();
		Delete();
	}

	private enum ProofState { Idle, DispatchPending, ComputeExecutionPending, ReadbackPending, Complete, Failed }
}
