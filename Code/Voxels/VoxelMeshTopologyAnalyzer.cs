internal sealed class VoxelMeshTopologyReport
{
	public int VertexCount { get; set; }
	public int IndexCount { get; set; }
	public int TriangleCount { get; set; }
	public int TrailingIndexCount { get; set; }
	public int InvalidTriangleCount { get; set; }
	public int DegenerateTriangleCount { get; set; }
	public int ZeroLengthEdgeCount { get; set; }
	public int TinyTriangleCount { get; set; }
	public int SliverTriangleCount { get; set; }
	public int ReversedNormalTriangleCount { get; set; }
	public int BoundaryEdgeCount { get; set; }
	public int ManifoldEdgeCount { get; set; }
	public int NonManifoldEdgeCount { get; set; }
	public int UnusedVertexCount { get; set; }
	public int DuplicatePositionVertexCount { get; set; }
	public int FaceSampleCount { get; set; }
	public int EdgeSampleCount { get; set; }
	public long EstimatedBufferBytes { get; set; }
	public double FaceAreaSum { get; set; }
	public double FaceAreaSquaredSum { get; set; }
	public float MinimumFaceArea { get; set; }
	public float MaximumFaceArea { get; set; }
	public double EdgeLengthSum { get; set; }
	public double EdgeLengthSquaredSum { get; set; }
	public float MinimumEdgeLength { get; set; }
	public float MaximumEdgeLength { get; set; }
	public double TriangleQualitySum { get; set; }
	public float MinimumTriangleQuality { get; set; }
	public float MaximumTriangleQuality { get; set; }
	public System.TimeSpan BuildElapsed { get; set; }
	public System.TimeSpan AnalysisElapsed { get; set; }

	public double AverageFaceArea => FaceSampleCount > 0 ? FaceAreaSum / FaceSampleCount : 0.0;
	public double FaceAreaStandardDeviation => CalculateStandardDeviation( FaceAreaSum, FaceAreaSquaredSum, FaceSampleCount );
	public double AverageEdgeLength => EdgeSampleCount > 0 ? EdgeLengthSum / EdgeSampleCount : 0.0;
	public double EdgeLengthStandardDeviation => CalculateStandardDeviation( EdgeLengthSum, EdgeLengthSquaredSum, EdgeSampleCount );
	public double AverageTriangleQuality => FaceSampleCount > 0 ? TriangleQualitySum / FaceSampleCount : 0.0;
	public double IndicesPerVertex => VertexCount > 0 ? (double)IndexCount / VertexCount : 0.0;
	public int EffectiveTriangleCount => TriangleCount - InvalidTriangleCount - DegenerateTriangleCount;

	private static double CalculateStandardDeviation( double sum, double squaredSum, int count )
	{
		if ( count <= 0 )
		{
			return 0.0;
		}

		var average = sum / count;
		return System.Math.Sqrt( System.Math.Max( 0.0, squaredSum / count - average * average ) );
	}
}

internal sealed class VoxelMeshTopologyAggregate
{
	public int ChunkCount { get; private set; }
	public long VertexCount { get; private set; }
	public long IndexCount { get; private set; }
	public long TriangleCount { get; private set; }
	public long TrailingIndexCount { get; private set; }
	public long InvalidTriangleCount { get; private set; }
	public long DegenerateTriangleCount { get; private set; }
	public long ZeroLengthEdgeCount { get; private set; }
	public long TinyTriangleCount { get; private set; }
	public long SliverTriangleCount { get; private set; }
	public long ReversedNormalTriangleCount { get; private set; }
	public long BoundaryEdgeCount { get; private set; }
	public long ManifoldEdgeCount { get; private set; }
	public long NonManifoldEdgeCount { get; private set; }
	public long UnusedVertexCount { get; private set; }
	public long DuplicatePositionVertexCount { get; private set; }
	public long FaceSampleCount { get; private set; }
	public long EdgeSampleCount { get; private set; }
	public long EstimatedBufferBytes { get; private set; }
	public double FaceAreaSum { get; private set; }
	public double FaceAreaSquaredSum { get; private set; }
	public float MinimumFaceArea { get; private set; } = float.MaxValue;
	public float MaximumFaceArea { get; private set; }
	public double EdgeLengthSum { get; private set; }
	public double EdgeLengthSquaredSum { get; private set; }
	public float MinimumEdgeLength { get; private set; } = float.MaxValue;
	public float MaximumEdgeLength { get; private set; }
	public double TriangleQualitySum { get; private set; }
	public float MinimumTriangleQuality { get; private set; } = float.MaxValue;
	public float MaximumTriangleQuality { get; private set; }
	public System.TimeSpan BuildElapsed { get; private set; }
	public System.TimeSpan AnalysisElapsed { get; private set; }

	public double AverageFaceArea => FaceSampleCount > 0 ? FaceAreaSum / FaceSampleCount : 0.0;
	public double FaceAreaStandardDeviation => CalculateStandardDeviation( FaceAreaSum, FaceAreaSquaredSum, FaceSampleCount );
	public double AverageEdgeLength => EdgeSampleCount > 0 ? EdgeLengthSum / EdgeSampleCount : 0.0;
	public double EdgeLengthStandardDeviation => CalculateStandardDeviation( EdgeLengthSum, EdgeLengthSquaredSum, EdgeSampleCount );
	public double AverageTriangleQuality => FaceSampleCount > 0 ? TriangleQualitySum / FaceSampleCount : 0.0;
	public double IndicesPerVertex => VertexCount > 0 ? (double)IndexCount / VertexCount : 0.0;
	public long EffectiveTriangleCount => TriangleCount - InvalidTriangleCount - DegenerateTriangleCount;

	public void Add( VoxelMeshTopologyReport report )
	{
		ChunkCount++;
		VertexCount += report.VertexCount;
		IndexCount += report.IndexCount;
		TriangleCount += report.TriangleCount;
		TrailingIndexCount += report.TrailingIndexCount;
		InvalidTriangleCount += report.InvalidTriangleCount;
		DegenerateTriangleCount += report.DegenerateTriangleCount;
		ZeroLengthEdgeCount += report.ZeroLengthEdgeCount;
		TinyTriangleCount += report.TinyTriangleCount;
		SliverTriangleCount += report.SliverTriangleCount;
		ReversedNormalTriangleCount += report.ReversedNormalTriangleCount;
		BoundaryEdgeCount += report.BoundaryEdgeCount;
		ManifoldEdgeCount += report.ManifoldEdgeCount;
		NonManifoldEdgeCount += report.NonManifoldEdgeCount;
		UnusedVertexCount += report.UnusedVertexCount;
		DuplicatePositionVertexCount += report.DuplicatePositionVertexCount;
		EstimatedBufferBytes += report.EstimatedBufferBytes;
		BuildElapsed += report.BuildElapsed;
		AnalysisElapsed += report.AnalysisElapsed;

		if ( report.FaceSampleCount > 0 )
		{
			FaceSampleCount += report.FaceSampleCount;
			FaceAreaSum += report.FaceAreaSum;
			FaceAreaSquaredSum += report.FaceAreaSquaredSum;
			TriangleQualitySum += report.TriangleQualitySum;
			MinimumFaceArea = System.MathF.Min( MinimumFaceArea, report.MinimumFaceArea );
			MaximumFaceArea = System.MathF.Max( MaximumFaceArea, report.MaximumFaceArea );
			MinimumTriangleQuality = System.MathF.Min( MinimumTriangleQuality, report.MinimumTriangleQuality );
			MaximumTriangleQuality = System.MathF.Max( MaximumTriangleQuality, report.MaximumTriangleQuality );
		}

		if ( report.EdgeSampleCount > 0 )
		{
			EdgeSampleCount += report.EdgeSampleCount;
			EdgeLengthSum += report.EdgeLengthSum;
			EdgeLengthSquaredSum += report.EdgeLengthSquaredSum;
			MinimumEdgeLength = System.MathF.Min( MinimumEdgeLength, report.MinimumEdgeLength );
			MaximumEdgeLength = System.MathF.Max( MaximumEdgeLength, report.MaximumEdgeLength );
		}
	}

	private static double CalculateStandardDeviation( double sum, double squaredSum, long count )
	{
		if ( count <= 0 )
		{
			return 0.0;
		}

		var average = sum / count;
		return System.Math.Sqrt( System.Math.Max( 0.0, squaredSum / count - average * average ) );
	}
}

internal static class VoxelMeshTopologyAnalyzer
{
	public const float SliverQualityThreshold = 0.2f;
	public const float TinyFaceAreaInVoxels = 0.01f;

	private const float DegenerateFaceAreaInVoxels = 0.000001f;
	private const float EquilateralTriangleFactor = 6.92820323f;
	private const int VertexStride = 76;

	public static VoxelMeshTopologyReport Analyze( VoxelMeshData meshData, float voxelSize, System.TimeSpan buildElapsed )
	{
		var analysisStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var vertices = meshData.Vertices;
		var indices = meshData.Indices;
		var report = new VoxelMeshTopologyReport
		{
			VertexCount = vertices.Count,
			IndexCount = indices.Count,
			TriangleCount = indices.Count / 3,
			TrailingIndexCount = indices.Count % 3,
			EstimatedBufferBytes = (long)vertices.Count * VertexStride + (long)indices.Count * sizeof( int ),
			MinimumFaceArea = float.MaxValue,
			MinimumEdgeLength = float.MaxValue,
			MinimumTriangleQuality = float.MaxValue,
			BuildElapsed = buildElapsed
		};
		var edgeUseCounts = new Dictionary<long, int>( indices.Count );
		var usedVertices = new bool[vertices.Count];
		var uniquePositions = new HashSet<Vector3>();
		var degenerateAreaThreshold = voxelSize * voxelSize * DegenerateFaceAreaInVoxels;
		var tinyAreaThreshold = voxelSize * voxelSize * TinyFaceAreaInVoxels;
		var zeroLengthThreshold = voxelSize * 0.000001f;

		foreach ( var vertex in vertices )
		{
			report.DuplicatePositionVertexCount += uniquePositions.Add( vertex.Position ) ? 0 : 1;
		}

		for ( var triangle = 0; triangle < report.TriangleCount; triangle++ )
		{
			var indexOffset = triangle * 3;
			var firstIndex = indices[indexOffset];
			var secondIndex = indices[indexOffset + 1];
			var thirdIndex = indices[indexOffset + 2];
			if ( !IsValidIndex( firstIndex, vertices.Count ) ||
				!IsValidIndex( secondIndex, vertices.Count ) ||
				!IsValidIndex( thirdIndex, vertices.Count ) )
			{
				report.InvalidTriangleCount++;
				continue;
			}

			var firstVertex = vertices[firstIndex];
			var secondVertex = vertices[secondIndex];
			var thirdVertex = vertices[thirdIndex];
			var firstEdge = secondVertex.Position - firstVertex.Position;
			var secondEdge = thirdVertex.Position - secondVertex.Position;
			var thirdEdge = firstVertex.Position - thirdVertex.Position;
			var firstEdgeSquared = firstEdge.LengthSquared;
			var secondEdgeSquared = secondEdge.LengthSquared;
			var thirdEdgeSquared = thirdEdge.LengthSquared;
			AddEdgeLength( report, System.MathF.Sqrt( firstEdgeSquared ), zeroLengthThreshold );
			AddEdgeLength( report, System.MathF.Sqrt( secondEdgeSquared ), zeroLengthThreshold );
			AddEdgeLength( report, System.MathF.Sqrt( thirdEdgeSquared ), zeroLengthThreshold );

			var cross = Vector3.Cross( firstEdge, thirdVertex.Position - firstVertex.Position );
			var doubledArea = System.MathF.Sqrt( cross.LengthSquared );
			var area = doubledArea * 0.5f;
			if ( firstIndex == secondIndex || secondIndex == thirdIndex || thirdIndex == firstIndex || area <= degenerateAreaThreshold )
			{
				report.DegenerateTriangleCount++;
				continue;
			}

			usedVertices[firstIndex] = true;
			usedVertices[secondIndex] = true;
			usedVertices[thirdIndex] = true;
			AddEdge( edgeUseCounts, firstIndex, secondIndex );
			AddEdge( edgeUseCounts, secondIndex, thirdIndex );
			AddEdge( edgeUseCounts, thirdIndex, firstIndex );

			report.FaceSampleCount++;
			report.FaceAreaSum += area;
			report.FaceAreaSquaredSum += (double)area * area;
			report.MinimumFaceArea = System.MathF.Min( report.MinimumFaceArea, area );
			report.MaximumFaceArea = System.MathF.Max( report.MaximumFaceArea, area );
			report.TinyTriangleCount += area < tinyAreaThreshold ? 1 : 0;

			var squaredEdgeSum = firstEdgeSquared + secondEdgeSquared + thirdEdgeSquared;
			var quality = squaredEdgeSum > 0.0f ? EquilateralTriangleFactor * area / squaredEdgeSum : 0.0f;
			report.TriangleQualitySum += quality;
			report.MinimumTriangleQuality = System.MathF.Min( report.MinimumTriangleQuality, quality );
			report.MaximumTriangleQuality = System.MathF.Max( report.MaximumTriangleQuality, quality );
			report.SliverTriangleCount += quality < SliverQualityThreshold ? 1 : 0;

			var averagedNormal = firstVertex.Normal + secondVertex.Normal + thirdVertex.Normal;
			if ( averagedNormal.LengthSquared > 0.000001f && Vector3.Dot( cross, averagedNormal ) < 0.0f )
			{
				report.ReversedNormalTriangleCount++;
			}
		}

		foreach ( var useCount in edgeUseCounts.Values )
		{
			report.BoundaryEdgeCount += useCount == 1 ? 1 : 0;
			report.ManifoldEdgeCount += useCount == 2 ? 1 : 0;
			report.NonManifoldEdgeCount += useCount > 2 ? 1 : 0;
		}

		foreach ( var used in usedVertices )
		{
			report.UnusedVertexCount += used ? 0 : 1;
		}

		if ( report.FaceSampleCount == 0 )
		{
			report.MinimumFaceArea = 0.0f;
			report.MinimumTriangleQuality = 0.0f;
		}

		if ( report.EdgeSampleCount == 0 )
		{
			report.MinimumEdgeLength = 0.0f;
		}

		report.AnalysisElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( analysisStart );
		return report;
	}

	private static bool IsValidIndex( int index, int vertexCount )
	{
		return index >= 0 && index < vertexCount;
	}

	private static void AddEdge( Dictionary<long, int> edgeUseCounts, int first, int second )
	{
		if ( first == second )
		{
			return;
		}

		var minimum = System.Math.Min( first, second );
		var maximum = System.Math.Max( first, second );
		var key = ((long)minimum << 32) | (uint)maximum;
		edgeUseCounts.TryGetValue( key, out var useCount );
		edgeUseCounts[key] = useCount + 1;
	}

	private static void AddEdgeLength( VoxelMeshTopologyReport report, float length, float zeroLengthThreshold )
	{
		report.EdgeSampleCount++;
		report.ZeroLengthEdgeCount += length <= zeroLengthThreshold ? 1 : 0;
		report.EdgeLengthSum += length;
		report.EdgeLengthSquaredSum += (double)length * length;
		report.MinimumEdgeLength = System.MathF.Min( report.MinimumEdgeLength, length );
		report.MaximumEdgeLength = System.MathF.Max( report.MaximumEdgeLength, length );
	}
}
