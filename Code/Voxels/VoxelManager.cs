public sealed class VoxelManager : Component
{
	public const string ChunkTag = "voxel_chunk";

	private const int MinimumChunkSize = 4;
	private const int MaximumChunkSize = 128;
	private const int MaximumChunkRadius = 128;
	private const int MaximumDetailedChunkLogs = 256;

	private readonly Dictionary<Vector3Int, VoxelChunk> _chunks = new();
	private readonly Dictionary<Vector3Int, ChunkRendererState> _chunkRenderers = new();

	[Property, Group( "World" ), Range( MinimumChunkSize, MaximumChunkSize )]
	public int ChunkSize { get; set; } = 32;

	[Property, Group( "World" ), Range( 1, MaximumChunkRadius )]
	public int ChunkRadius { get; set; } = 4;

	[Property, Group( "World" ), Range( 1.0f, 128.0f )]
	public float VoxelSize { get; set; } = 16.0f;

	[Property, Group( "Rendering" )]
	public Material TerrainMaterial { get; set; }

	[Property, Group( "Diagnostics" )]
	public bool LogGeneration { get; set; }

	[Property, Group( "Diagnostics" ), Range( 0, MaximumDetailedChunkLogs )]
	public int DetailedChunkLogLimit { get; set; } = 64;

	public IReadOnlyDictionary<Vector3Int, VoxelChunk> Chunks => _chunks;
	public int LoadedChunkCount => _chunks.Count;
	public int ChunkDiameter => ChunkRadius * 2;
	public int ConfiguredChunkCount => checked( ChunkDiameter * ChunkDiameter );

	protected override void OnValidate()
	{
		ChunkSize = System.Math.Clamp( ChunkSize, MinimumChunkSize, MaximumChunkSize );
		ChunkRadius = System.Math.Clamp( ChunkRadius, 1, MaximumChunkRadius );
		VoxelSize = System.Math.Clamp( VoxelSize, 1.0f, 128.0f );
		DetailedChunkLogLimit = System.Math.Clamp( DetailedChunkLogLimit, 0, MaximumDetailedChunkLogs );
	}

	protected override void OnStart()
	{
		GenerateWorld();
	}

	public void GenerateWorld()
	{
		var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		ClearChunkRenderers();
		_chunks.Clear();
		var allAirChunkCount = 0;
		var allSolidChunkCount = 0;
		var surfaceChunkCount = 0;
		var detailedChunkCount = 0;
		long totalSampleCount = 0;

		for ( var y = -ChunkRadius; y < ChunkRadius; y++ )
		{
			for ( var x = -ChunkRadius; x < ChunkRadius; x++ )
			{
				var logDetails = LogGeneration && detailedChunkCount < DetailedChunkLogLimit;
				GenerateChunk( new Vector3Int( x, y, 0 ), logDetails, out var report );

				if ( !LogGeneration )
				{
					continue;
				}

				detailedChunkCount += logDetails ? 1 : 0;
				totalSampleCount += report.SampleCount;
				allAirChunkCount += report.IsAllAir ? 1 : 0;
				allSolidChunkCount += report.IsAllSolid ? 1 : 0;
				surfaceChunkCount += report.HasSurface ? 1 : 0;
			}
		}

		if ( LogGeneration )
		{
			var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime( startTimestamp );
			LogWorldTopology( totalSampleCount, allAirChunkCount, allSolidChunkCount, surfaceChunkCount, detailedChunkCount, elapsed );
			ReportMeshTopology();
		}
	}

	public VoxelChunk GenerateChunk( Vector3Int coordinate )
	{
		return GenerateChunk( coordinate, LogGeneration, out _ );
	}

	private VoxelChunk GenerateChunk( Vector3Int coordinate, bool logDetails, out ChunkTopologyReport report )
	{
		ValidateChunkCoordinate( coordinate );

		if ( _chunks.TryGetValue( coordinate, out var existingChunk ) )
		{
			report = default;
			return existingChunk;
		}

		var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		var chunk = new VoxelChunk( coordinate, ChunkSize );
		FillChunk( chunk );
		var meshStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		_chunks.Add( coordinate, chunk );
		CreateChunkRenderer( chunk, out var vertexCount, out var triangleCount );

		if ( LogGeneration )
		{
			var dataElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( startTimestamp, meshStartTimestamp );
			var totalElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( startTimestamp );
			report = AnalyzeChunk( chunk, vertexCount, triangleCount, dataElapsed, totalElapsed );
			if ( logDetails )
			{
				LogChunkTopology( chunk, report );
			}
		}
		else
		{
			report = default;
		}

		return chunk;
	}

	public bool TryGetChunk( Vector3Int coordinate, out VoxelChunk chunk )
	{
		return _chunks.TryGetValue( coordinate, out chunk );
	}

	[Button]
	public void ReportMeshTopology()
	{
		if ( _chunks.Count == 0 )
		{
			Log.Warning( "Mesh topology report skipped because the voxel world has no loaded chunks." );
			return;
		}

		var reportStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var aggregate = new VoxelMeshTopologyAggregate();
		var detailedCount = 0;
		var emptyChunkCount = 0;
		var slowestChunk = default( Vector3Int );
		var slowestBuild = System.TimeSpan.Zero;

		foreach ( var pair in _chunks )
		{
			var buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
			var meshData = VoxelMesher.Build( pair.Value, VoxelSize );
			var buildElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( buildStart );
			var report = VoxelMeshTopologyAnalyzer.Analyze( meshData, VoxelSize, buildElapsed );
			aggregate.Add( report );
			emptyChunkCount += report.TriangleCount == 0 ? 1 : 0;

			if ( buildElapsed > slowestBuild )
			{
				slowestBuild = buildElapsed;
				slowestChunk = pair.Key;
			}

			if ( detailedCount < DetailedChunkLogLimit )
			{
				LogChunkMeshTopology( pair.Key, report );
				detailedCount++;
			}
		}

		var reportElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( reportStart );
		LogWorldMeshTopology( aggregate, emptyChunkCount, detailedCount, slowestChunk, slowestBuild, reportElapsed );
	}

	public int DisplaceSdf( Vector3 worldPosition, float radius, float displacement )
	{
		if ( radius <= 0.0f || System.MathF.Abs( displacement ) <= 0.0001f )
		{
			return 0;
		}

		var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		var brushCenter = GameObject.WorldTransform.PointToLocal( worldPosition ) / VoxelSize;
		var brushRadius = radius / VoxelSize;
		var sdfDisplacement = displacement / VoxelSize;
		var changedChunks = new List<Vector3Int>();
		var changedSampleCount = 0;

		foreach ( var pair in _chunks )
		{
			var changedSamples = DisplaceChunkSdf( pair.Value, brushCenter, brushRadius, sdfDisplacement );
			if ( changedSamples == 0 )
			{
				continue;
			}

			changedChunks.Add( pair.Key );
			changedSampleCount += changedSamples;
		}

		foreach ( var coordinate in changedChunks )
		{
			RebuildChunkRenderer( _chunks[coordinate] );
		}

		var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime( startTimestamp );
		Log.Info(
			$"Voxel brush: center={worldPosition}, radius={radius:F1}, displacement={displacement:F1}, " +
			$"changedSamples={changedSampleCount:N0}, rebuiltChunks={changedChunks.Count:N0}, total={elapsed.TotalMilliseconds:F2} ms."
		);

		return changedChunks.Count;
	}

	private void FillChunk( VoxelChunk chunk )
	{
		var sampleSize = chunk.SampleSize;
		var chunkOrigin = GetChunkVoxelOrigin( chunk.Coordinate );
		var voxels = chunk.Voxels;
		var sampleLayer = sampleSize * sampleSize;

		for ( var z = 0; z < sampleSize; z++ )
		{
			var distance = chunkOrigin.z + z;
			var material = distance < 0.0f ? VoxelMaterial.Terrain : VoxelMaterial.Air;
			System.Array.Fill( voxels, new Voxel( distance, material ), z * sampleLayer, sampleLayer );
		}
	}

	private int DisplaceChunkSdf( VoxelChunk chunk, Vector3 brushCenter, float brushRadius, float sdfDisplacement )
	{
		var chunkOrigin = GetChunkVoxelOrigin( chunk.Coordinate );
		var minimumX = System.Math.Clamp( (int)System.MathF.Floor( brushCenter.x - brushRadius - chunkOrigin.x ), 0, chunk.Size );
		var maximumX = System.Math.Clamp( (int)System.MathF.Ceiling( brushCenter.x + brushRadius - chunkOrigin.x ), 0, chunk.Size );
		var minimumY = System.Math.Clamp( (int)System.MathF.Floor( brushCenter.y - brushRadius - chunkOrigin.y ), 0, chunk.Size );
		var maximumY = System.Math.Clamp( (int)System.MathF.Ceiling( brushCenter.y + brushRadius - chunkOrigin.y ), 0, chunk.Size );
		var minimumZ = System.Math.Clamp( (int)System.MathF.Floor( brushCenter.z - brushRadius - chunkOrigin.z ), 0, chunk.Size );
		var maximumZ = System.Math.Clamp( (int)System.MathF.Ceiling( brushCenter.z + brushRadius - chunkOrigin.z ), 0, chunk.Size );
		var radiusSquared = brushRadius * brushRadius;
		var sampleSize = chunk.SampleSize;
		var sampleLayer = sampleSize * sampleSize;
		var changedSampleCount = 0;

		for ( var z = minimumZ; z <= maximumZ; z++ )
		{
			var zDistance = chunkOrigin.z + z - brushCenter.z;
			for ( var y = minimumY; y <= maximumY; y++ )
			{
				var yDistance = chunkOrigin.y + y - brushCenter.y;
				for ( var x = minimumX; x <= maximumX; x++ )
				{
					var xDistance = chunkOrigin.x + x - brushCenter.x;
					var distanceSquared = xDistance * xDistance + yDistance * yDistance + zDistance * zDistance;
					if ( distanceSquared >= radiusSquared )
					{
						continue;
					}

					var normalizedDistance = System.MathF.Sqrt( distanceSquared ) / brushRadius;
					var falloff = 1.0f - normalizedDistance;
					falloff = falloff * falloff * (3.0f - 2.0f * falloff);
					var index = x + sampleSize * y + sampleLayer * z;
					var distance = chunk.Voxels[index].Distance + sdfDisplacement * falloff;
					var material = distance < 0.0f ? VoxelMaterial.Terrain : VoxelMaterial.Air;
					chunk.Voxels[index] = new Voxel( distance, material );
					changedSampleCount++;
				}
			}
		}

		return changedSampleCount;
	}

	private void CreateChunkRenderer( VoxelChunk chunk, out int vertexCount, out int triangleCount )
	{
		var meshData = VoxelMesher.Build( chunk, VoxelSize );
		vertexCount = meshData.Vertices.Count;
		triangleCount = meshData.Indices.Count / 3;
		if ( meshData.Indices.Count == 0 )
		{
			return;
		}

		var material = TerrainMaterial ?? Material.Load( "materials/voxel_grass.vmat" );
		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( meshData.Vertices.Count, meshData.Vertices );
		mesh.CreateIndexBuffer( meshData.Indices.Count, meshData.Indices );

		var chunkWorldSize = ChunkSize * VoxelSize;
		mesh.Bounds = BBox.FromPositionAndSize( Vector3.One * chunkWorldSize * 0.5f, Vector3.One * chunkWorldSize );
		var renderModel = new ModelBuilder().AddMesh( mesh ).Create();
		var collisionModel = BuildCollisionModel( meshData );

		var renderObject = new GameObject( true, $"Voxel Chunk {chunk.Coordinate}" );
		renderObject.Parent = GameObject;
		renderObject.Tags.Add( ChunkTag );
		var chunkOrigin = GetChunkVoxelOrigin( chunk.Coordinate );
		renderObject.LocalPosition = new Vector3( chunkOrigin.x, chunkOrigin.y, chunkOrigin.z ) * VoxelSize;
		var renderer = renderObject.AddComponent<ModelRenderer>();
		renderer.Model = renderModel;
		var collider = renderObject.AddComponent<ModelCollider>();
		collider.Model = collisionModel;
		collider.Static = true;
		_chunkRenderers.Add( chunk.Coordinate, new ChunkRendererState(
			renderObject,
			mesh,
			renderer,
			collider,
			meshData.Vertices.Count,
			meshData.Indices.Count
		) );
	}

	private void RebuildChunkRenderer( VoxelChunk chunk )
	{
		if ( !_chunkRenderers.TryGetValue( chunk.Coordinate, out var state ) )
		{
			CreateChunkRenderer( chunk, out _, out _ );
			return;
		}

		var meshData = VoxelMesher.Build( chunk, VoxelSize );
		if ( meshData.Indices.Count == 0 )
		{
			state.Mesh.SetVertexRange( 0, 0 );
			state.Mesh.SetIndexRange( 0, 0 );
			state.Renderer.Enabled = false;
			state.Collider.Enabled = false;
			return;
		}

		if ( meshData.Vertices.Count > state.VertexCapacity )
		{
			state.VertexCapacity = System.Math.Max( meshData.Vertices.Count, state.VertexCapacity * 2 );
			state.Mesh.SetVertexBufferSize( state.VertexCapacity );
		}

		if ( meshData.Indices.Count > state.IndexCapacity )
		{
			state.IndexCapacity = System.Math.Max( meshData.Indices.Count, state.IndexCapacity * 2 );
			state.Mesh.SetIndexBufferSize( state.IndexCapacity );
		}

		state.Mesh.SetVertexBufferData( meshData.Vertices );
		state.Mesh.SetVertexRange( 0, meshData.Vertices.Count );
		state.Mesh.SetIndexBufferData( meshData.Indices );
		state.Mesh.SetIndexRange( 0, meshData.Indices.Count );

		state.Collider.Enabled = false;
		state.Collider.Model = BuildCollisionModel( meshData );
		state.Collider.Enabled = true;
		state.Renderer.Enabled = true;
	}

	private static Model BuildCollisionModel( VoxelMeshData meshData )
	{
		var collisionVertices = new List<Vector3>( meshData.Vertices.Count );
		foreach ( var vertex in meshData.Vertices )
		{
			collisionVertices.Add( vertex.Position );
		}

		return new ModelBuilder()
			.AddCollisionMesh( collisionVertices, meshData.Indices )
			.AddTraceMesh( collisionVertices, meshData.Indices )
			.Create();
	}

	private Vector3Int GetChunkVoxelOrigin( Vector3Int coordinate )
	{
		return new Vector3Int(
			coordinate.x * ChunkSize,
			coordinate.y * ChunkSize,
			(coordinate.z - 1) * ChunkSize
		);
	}

	private ChunkTopologyReport AnalyzeChunk( VoxelChunk chunk, int vertexCount, int triangleCount, System.TimeSpan dataElapsed, System.TimeSpan totalElapsed )
	{
		var solidSampleCount = 0;
		var minimumDistance = float.MaxValue;
		var maximumDistance = float.MinValue;

		foreach ( var voxel in chunk.Voxels )
		{
			solidSampleCount += voxel.IsSolid ? 1 : 0;
			minimumDistance = System.MathF.Min( minimumDistance, voxel.Distance );
			maximumDistance = System.MathF.Max( maximumDistance, voxel.Distance );
		}

		return new ChunkTopologyReport(
			chunk.SampleCount,
			solidSampleCount,
			minimumDistance,
			maximumDistance,
			vertexCount,
			triangleCount,
			dataElapsed,
			totalElapsed
		);
	}

	private void LogChunkTopology( VoxelChunk chunk, ChunkTopologyReport report )
	{
		var voxelOrigin = GetChunkVoxelOrigin( chunk.Coordinate );
		var localPosition = new Vector3( voxelOrigin.x, voxelOrigin.y, voxelOrigin.z ) * VoxelSize;
		var worldPosition = GameObject.WorldTransform.PointToWorld( localPosition );
		var state = report.IsAllAir ? "all-air" : report.IsAllSolid ? "all-solid" : "surface";
		var hasRenderer = _chunkRenderers.ContainsKey( chunk.Coordinate );

		Log.Info(
			$"Voxel chunk {chunk.Coordinate}: state={state}, voxelOrigin={voxelOrigin}, localPosition={localPosition}, worldPosition={worldPosition}, " +
			$"samples={report.SampleCount:N0} (solid={report.SolidSampleCount:N0}, air={report.AirSampleCount:N0}), " +
			$"SDF=[{report.MinimumDistance:F3}, {report.MaximumDistance:F3}], mesh={report.VertexCount:N0} vertices/{report.TriangleCount:N0} triangles, " +
			$"renderer={(hasRenderer ? "yes" : "no")}, data={report.DataElapsed.TotalMilliseconds:F2} ms, total={report.TotalElapsed.TotalMilliseconds:F2} ms."
		);
	}

	private void LogWorldTopology( long totalSampleCount, int allAirChunkCount, int allSolidChunkCount, int surfaceChunkCount, int detailedChunkCount, System.TimeSpan elapsed )
	{
		var minimumCoordinate = new Vector3Int( -ChunkRadius, -ChunkRadius, 0 );
		var maximumCoordinate = new Vector3Int( ChunkRadius - 1, ChunkRadius - 1, 0 );
		var minimumVoxel = GetChunkVoxelOrigin( minimumCoordinate );
		var maximumOrigin = GetChunkVoxelOrigin( maximumCoordinate );
		var maximumVoxel = maximumOrigin + new Vector3Int( ChunkSize, ChunkSize, ChunkSize );
		var minimumLocal = new Vector3( minimumVoxel.x, minimumVoxel.y, minimumVoxel.z ) * VoxelSize;
		var maximumLocal = new Vector3( maximumVoxel.x, maximumVoxel.y, maximumVoxel.z ) * VoxelSize;
		var minimumWorld = GameObject.WorldTransform.PointToWorld( minimumLocal );
		var maximumWorld = GameObject.WorldTransform.PointToWorld( maximumLocal );

		Log.Info(
			$"Voxel topology: radius={ChunkRadius}, diameter={ChunkDiameter}, configured={ConfiguredChunkCount:N0}, loaded={LoadedChunkCount:N0}, " +
			$"surface={surfaceChunkCount:N0}, all-solid={allSolidChunkCount:N0}, all-air={allAirChunkCount:N0}, renderers={_chunkRenderers.Count:N0}, " +
			$"samples={totalSampleCount:N0}, coordinates={minimumCoordinate}..{maximumCoordinate}, voxelBounds={minimumVoxel}..{maximumVoxel}, " +
			$"worldCorners={minimumWorld}..{maximumWorld}, detailed={detailedChunkCount:N0}/{LoadedChunkCount:N0}, total={elapsed.TotalMilliseconds:F2} ms."
		);
	}

	private void LogChunkMeshTopology( Vector3Int coordinate, VoxelMeshTopologyReport report )
	{
		var voxelArea = VoxelSize * VoxelSize;
		Log.Info(
			$"Mesh topology chunk {coordinate}: build={report.BuildElapsed.TotalMilliseconds:F2} ms, analysis={report.AnalysisElapsed.TotalMilliseconds:F2} ms, " +
			$"vertices={report.VertexCount:N0} (duplicatePositions={report.DuplicatePositionVertexCount:N0}, unusedByValidFaces={report.UnusedVertexCount:N0}), indices={report.IndexCount:N0}, " +
			$"triangles={report.TriangleCount:N0} (effective={report.EffectiveTriangleCount:N0}), indices/vertex={report.IndicesPerVertex:F2}, buffers={report.EstimatedBufferBytes / 1024.0:F1} KiB; " +
			$"faceArea=[{report.MinimumFaceArea:F3}/{report.AverageFaceArea:F3}/{report.MaximumFaceArea:F3}] avg={report.AverageFaceArea / voxelArea:F4} voxel² sd={report.FaceAreaStandardDeviation:F3}; " +
			$"edgeLength=[{report.MinimumEdgeLength:F3}/{report.AverageEdgeLength:F3}/{report.MaximumEdgeLength:F3}] avg={report.AverageEdgeLength / VoxelSize:F4} voxels sd={report.EdgeLengthStandardDeviation:F3} zero={report.ZeroLengthEdgeCount:N0}; " +
			$"quality=[{report.MinimumTriangleQuality:F3}/{report.AverageTriangleQuality:F3}/{report.MaximumTriangleQuality:F3}], slivers={report.SliverTriangleCount:N0}, tiny={report.TinyTriangleCount:N0}, " +
			$"degenerate={report.DegenerateTriangleCount:N0}, invalid={report.InvalidTriangleCount:N0}, reversedNormals={report.ReversedNormalTriangleCount:N0}, " +
			$"chunkLocalEdges(boundary/manifold/nonManifold)={report.BoundaryEdgeCount:N0}/{report.ManifoldEdgeCount:N0}/{report.NonManifoldEdgeCount:N0}."
		);
	}

	private void LogWorldMeshTopology(
		VoxelMeshTopologyAggregate report,
		int emptyChunkCount,
		int detailedCount,
		Vector3Int slowestChunk,
		System.TimeSpan slowestBuild,
		System.TimeSpan reportElapsed )
	{
		var voxelArea = VoxelSize * VoxelSize;
		var averageBuildMilliseconds = report.ChunkCount > 0 ? report.BuildElapsed.TotalMilliseconds / report.ChunkCount : 0.0;
		var trianglesPerSecond = report.BuildElapsed.TotalSeconds > 0.0 ? report.TriangleCount / report.BuildElapsed.TotalSeconds : 0.0;
		var edgeCount = report.BoundaryEdgeCount + report.ManifoldEdgeCount + report.NonManifoldEdgeCount;

		Log.Info(
			$"Mesh topology world performance: chunks={report.ChunkCount:N0} (empty={emptyChunkCount:N0}), vertices={report.VertexCount:N0}, indices={report.IndexCount:N0}, triangles={report.TriangleCount:N0}, " +
			$"effectiveTriangles={report.EffectiveTriangleCount:N0}, indices/vertex={report.IndicesPerVertex:F2}, estimatedBuffers={report.EstimatedBufferBytes / (1024.0 * 1024.0):F2} MiB, meshBuild={report.BuildElapsed.TotalMilliseconds:F2} ms " +
			$"({averageBuildMilliseconds:F2} ms/chunk, {trianglesPerSecond:N0} triangles/s), analysis={report.AnalysisElapsed.TotalMilliseconds:F2} ms, snapshot={reportElapsed.TotalMilliseconds:F2} ms, " +
			$"slowest={slowestChunk} at {slowestBuild.TotalMilliseconds:F2} ms, detailed={detailedCount:N0}/{report.ChunkCount:N0}."
		);

		Log.Info(
			$"Mesh topology world geometry: faceArea=[{SafeMinimum( report.MinimumFaceArea ):F3}/{report.AverageFaceArea:F3}/{report.MaximumFaceArea:F3}] worldUnits², " +
			$"averageFace={report.AverageFaceArea / voxelArea:F4} voxel², faceAreaSd={report.FaceAreaStandardDeviation:F3}; " +
			$"edgeLength=[{SafeMinimum( report.MinimumEdgeLength ):F3}/{report.AverageEdgeLength:F3}/{report.MaximumEdgeLength:F3}] worldUnits, " +
			$"averageEdge={report.AverageEdgeLength / VoxelSize:F4} voxels, edgeLengthSd={report.EdgeLengthStandardDeviation:F3}; " +
			$"quality=[{SafeMinimum( report.MinimumTriangleQuality ):F3}/{report.AverageTriangleQuality:F3}/{report.MaximumTriangleQuality:F3}]."
		);

		Log.Info(
			$"Mesh topology world integrity: slivers(<{VoxelMeshTopologyAnalyzer.SliverQualityThreshold:F2})={report.SliverTriangleCount:N0} ({Percentage( report.SliverTriangleCount, report.EffectiveTriangleCount ):F2}% of effective), " +
			$"tinyFaces(<{VoxelMeshTopologyAnalyzer.TinyFaceAreaInVoxels:F2} voxel²)={report.TinyTriangleCount:N0} ({Percentage( report.TinyTriangleCount, report.EffectiveTriangleCount ):F2}% of effective), " +
			$"degenerate={report.DegenerateTriangleCount:N0} ({Percentage( report.DegenerateTriangleCount, report.TriangleCount ):F2}%), zeroLengthFaceEdges={report.ZeroLengthEdgeCount:N0}, " +
			$"invalid={report.InvalidTriangleCount:N0}, trailingIndices={report.TrailingIndexCount:N0}, reversedNormals={report.ReversedNormalTriangleCount:N0}, " +
			$"duplicatePositionVertices={report.DuplicatePositionVertexCount:N0} ({Percentage( report.DuplicatePositionVertexCount, report.VertexCount ):F2}%), unusedByValidFaces={report.UnusedVertexCount:N0}; " +
			$"chunkLocalEdges(boundary/manifold/nonManifold)={report.BoundaryEdgeCount:N0}/{report.ManifoldEdgeCount:N0}/{report.NonManifoldEdgeCount:N0} " +
			$"(boundary={Percentage( report.BoundaryEdgeCount, edgeCount ):F2}%; chunk boundaries are included)."
		);
	}

	private static float SafeMinimum( float minimum )
	{
		return minimum == float.MaxValue ? 0.0f : minimum;
	}

	private static double Percentage( long count, long total )
	{
		return total > 0 ? count * 100.0 / total : 0.0;
	}

	private void ClearChunkRenderers()
	{
		foreach ( var state in _chunkRenderers.Values )
		{
			state.GameObject.Destroy();
		}

		_chunkRenderers.Clear();
	}

	private sealed class ChunkRendererState
	{
		public GameObject GameObject { get; }
		public Mesh Mesh { get; }
		public ModelRenderer Renderer { get; }
		public ModelCollider Collider { get; }
		public int VertexCapacity { get; set; }
		public int IndexCapacity { get; set; }

		public ChunkRendererState( GameObject gameObject, Mesh mesh, ModelRenderer renderer, ModelCollider collider, int vertexCapacity, int indexCapacity )
		{
			GameObject = gameObject;
			Mesh = mesh;
			Renderer = renderer;
			Collider = collider;
			VertexCapacity = vertexCapacity;
			IndexCapacity = indexCapacity;
		}
	}

	private void ValidateChunkCoordinate( Vector3Int coordinate )
	{
		if ( coordinate.x < -ChunkRadius || coordinate.x >= ChunkRadius ||
			coordinate.y < -ChunkRadius || coordinate.y >= ChunkRadius ||
			coordinate.z != 0 )
		{
			throw new System.ArgumentOutOfRangeException( nameof( coordinate ), $"Chunk coordinate {coordinate} is outside the configured XY radius of {ChunkRadius} or is not on terrain layer Z=0." );
		}
	}

	private readonly struct ChunkTopologyReport
	{
		public int SampleCount { get; }
		public int SolidSampleCount { get; }
		public int AirSampleCount => SampleCount - SolidSampleCount;
		public float MinimumDistance { get; }
		public float MaximumDistance { get; }
		public int VertexCount { get; }
		public int TriangleCount { get; }
		public System.TimeSpan DataElapsed { get; }
		public System.TimeSpan TotalElapsed { get; }
		public bool IsAllAir => SolidSampleCount == 0;
		public bool IsAllSolid => SolidSampleCount == SampleCount;
		public bool HasSurface => !IsAllAir && !IsAllSolid;

		public ChunkTopologyReport( int sampleCount, int solidSampleCount, float minimumDistance, float maximumDistance, int vertexCount, int triangleCount, System.TimeSpan dataElapsed, System.TimeSpan totalElapsed )
		{
			SampleCount = sampleCount;
			SolidSampleCount = solidSampleCount;
			MinimumDistance = minimumDistance;
			MaximumDistance = maximumDistance;
			VertexCount = vertexCount;
			TriangleCount = triangleCount;
			DataElapsed = dataElapsed;
			TotalElapsed = totalElapsed;
		}
	}
}
