public sealed class VoxelManager : Component
{
	public const string ChunkTag = "voxel_chunk";

	private const int MinimumChunkSize = 4;
	private const int MaximumChunkSize = 128;
	private const int MaximumChunkRadius = 128;
	private const int MaximumDetailedChunkLogs = 256;
	private const int MaximumGpuVertices = 2_000_000;
	private const int MaximumConcurrentGpuChunkBuilds = 8;
	private const double GpuColliderEditSettleSeconds = 0.20;

	private readonly Dictionary<Vector3Int, VoxelChunk> _chunks = new();
	private readonly Dictionary<Vector3Int, ChunkRendererState> _chunkRenderers = new();
	private readonly Queue<GpuFixtureCase> _gpuFixtureQueue = new();
	private readonly Dictionary<Vector3Int, GpuChunkRuntime> _gpuChunkStates = new();
	private readonly Queue<Vector3Int> _gpuChunkBuildQueue = new();
	private readonly HashSet<Vector3Int> _gpuQueuedChunks = new();
	private readonly HashSet<Vector3Int> _gpuBatchDirtyChunks = new();
	private VoxelGpuComputeProbe _gpuComputeProbe;
	private GpuFixtureCase _activeGpuFixture;
	private int _gpuFixturePassCount;
	private int _gpuFixtureFailCount;
	private int _gpuFixtureBufferAllocationCount;
	private long _gpuBatchStartTimestamp;
	private bool _gpuBatchSummaryPending;
	private bool _gpuWorldHasCompletedInitialBuild;
	private long _lastGpuBrushEditTimestamp;

	[Property, Group( "World" ), Range( MinimumChunkSize, MaximumChunkSize )]
	public int ChunkSize { get; set; } = 32;

	[Property, Group( "World" ), Range( 1, MaximumChunkRadius )]
	public int ChunkRadius { get; set; } = 4;

	[Property, Group( "World" ), Range( 1.0f, 128.0f )]
	public float VoxelSize { get; set; } = 16.0f;

	[Property, Group( "Rendering" )]
	public Material TerrainMaterial { get; set; }

	[Property, Group( "GPU Rendering" )]
	public Material GpuTerrainMaterial { get; set; }

	[Property, Group( "GPU Rendering" ), Range( 1, MaximumConcurrentGpuChunkBuilds )]
	public int GpuChunkBuildConcurrency { get; set; } = 2;

	[Property, Group( "Diagnostics" )]
	public bool LogGeneration { get; set; }

	[Property, Group( "Diagnostics" )]
	public bool RunGpuCorrectnessOnStart { get; set; }

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
		GpuChunkBuildConcurrency = System.Math.Clamp( GpuChunkBuildConcurrency, 1, MaximumConcurrentGpuChunkBuilds );
		DetailedChunkLogLimit = System.Math.Clamp( DetailedChunkLogLimit, 0, MaximumDetailedChunkLogs );
	}

	protected override void OnStart()
	{
		if ( RunGpuCorrectnessOnStart )
			RunGpuCorrectnessSuite();
		else
			GenerateWorld();
	}

	protected override void OnUpdate()
	{
		if ( _gpuComputeProbe is not null )
		{
			_gpuComputeProbe.ReleaseCompletedResources();
			_gpuComputeProbe.TryPreparePendingEmit();
			if ( _gpuComputeProbe.TryTakeCompletedResult( out var fixtureResult ) && _activeGpuFixture is not null )
			{
				CompleteGpuFixture( fixtureResult );
			}
		}

		UpdateGpuChunkWorld();
		FlushSettledGpuColliders();
	}

	protected override void OnDisabled()
	{
		DisposeGpuSystems( false );
	}

	protected override void OnDestroy()
	{
		DisposeGpuSystems( false );
	}

	[Button]
	public void RebuildGpuWorld()
	{
		_gpuFixtureQueue.Clear();
		_activeGpuFixture = null;
		DisposeGpuFixtureProbe();
		StartGpuChunkWorld();
	}

	[Button]
	public void RunGpuCorrectnessSuite()
	{
		DisposeGpuSystems( true );
		_gpuFixtureQueue.Clear();
		_activeGpuFixture = null;
		_gpuFixturePassCount = 0;
		_gpuFixtureFailCount = 0;
		_gpuFixtureBufferAllocationCount = 0;

		var fixtureSize = System.Math.Min( ChunkSize, 16 );
		_gpuFixtureQueue.Enqueue( CreateGpuFixture( GpuFixtureType.AllAir, fixtureSize ) );
		_gpuFixtureQueue.Enqueue( CreateGpuFixture( GpuFixtureType.AllSolid, fixtureSize ) );
		_gpuFixtureQueue.Enqueue( CreateGpuFixture( GpuFixtureType.OffsetPlane, fixtureSize ) );
		_gpuFixtureQueue.Enqueue( CreateGpuFixture( GpuFixtureType.Sphere, fixtureSize ) );
		_gpuFixtureQueue.Enqueue( CreateGpuFixture( GpuFixtureType.Saddle, fixtureSize ) );
		_gpuFixtureQueue.Enqueue( CreateGpuFixture( GpuFixtureType.ExactZeroPlane, fixtureSize ) );
		_gpuFixtureQueue.Enqueue( CreateGpuFixture( GpuFixtureType.BoundarySphere, fixtureSize ) );

		Log.Info( $"Voxel GPU correctness suite scheduled: fixtures={_gpuFixtureQueue.Count:N0}, size={fixtureSize:N0}." );
		StartNextGpuFixture();
	}

	private void StartNextGpuFixture()
	{
		if ( !_gpuFixtureQueue.TryDequeue( out _activeGpuFixture ) )
		{
			var buffersReused = _gpuFixtureBufferAllocationCount == 1;
			_gpuFixtureFailCount += buffersReused ? 0 : 1;
			Log.Info(
				$"Voxel GPU correctness suite: result={(_gpuFixtureFailCount == 0 ? "PASS" : "FAIL")}, " +
				$"passed={_gpuFixturePassCount:N0}, failed={_gpuFixtureFailCount:N0}, " +
				$"bufferAllocations={_gpuFixtureBufferAllocationCount:N0}, buffersReused={(buffersReused ? "yes" : "no")}."
			);
			_activeGpuFixture = null;
			DisposeGpuFixtureProbe();
			StartGpuChunkWorld();
			return;
		}

		try
		{
			if ( _gpuComputeProbe is not null && _gpuComputeProbe.TryRegenerate( _activeGpuFixture.Distances ) )
			{
				return;
			}

			DisposeGpuFixtureProbe();
			_gpuComputeProbe = new VoxelGpuComputeProbe(
				Scene.SceneWorld,
				_activeGpuFixture.Distances,
				_activeGpuFixture.Size,
				GameObject.WorldPosition,
				VoxelSize,
				CalculateGpuVertexCapacity( _activeGpuFixture.Size )
			);
			_gpuFixtureBufferAllocationCount++;
			_gpuComputeProbe.Run();
		}
		catch ( System.Exception exception )
		{
			Log.Error( $"Voxel GPU fixture {_activeGpuFixture.Name} failed to start: {exception.Message}" );
			_gpuFixtureFailCount++;
			StartNextGpuFixture();
		}
	}

	private void CompleteGpuFixture( VoxelGpuComputeResult result )
	{
		var fixture = _activeGpuFixture;
		var passed = ValidateGpuFixture( fixture, result, out var reason );
		_gpuFixturePassCount += passed ? 1 : 0;
		_gpuFixtureFailCount += passed ? 0 : 1;
		Log.Info(
			$"Voxel GPU fixture {fixture.Name}: result={(passed ? "PASS" : "FAIL")}, cells={result.CellsProcessed:N0}, " +
			$"activeCells={result.ActiveCells:N0}, vertices={result.VertexCount:N0}, triangles={result.TrianglesEmitted:N0}, " +
			$"degeneratesRejected={result.DegeneratesRejected:N0}, overflowAttempts={result.BufferOverflowAttempts:N0}, " +
			$"pooledVertexCapacity={_gpuComputeProbe.VertexCapacity:N0}, retiredVertexCapacity={_gpuComputeProbe.RetiredVertexCapacity:N0}, " +
			$"retiredIndexCapacity={_gpuComputeProbe.RetiredIndexCapacity:N0}, " +
			$"outputBufferAllocations={_gpuComputeProbe.OutputBufferAllocationCount:N0}, " +
			$"asyncLatency=[{FormatGpuTimings( result.Timing )}], validation={reason}."
		);
		_activeGpuFixture = null;
		StartNextGpuFixture();
	}

	private static string FormatGpuTimings( VoxelGpuTimingReport timing )
	{
		return $"classify={timing.ClassifyFenceLatency.TotalMilliseconds:F2}ms, " +
			$"scan={timing.ScanFenceLatency.TotalMilliseconds:F2}ms, " +
			$"emit={timing.EmitStatisticsLatency.TotalMilliseconds:F2}ms, " +
			$"vertices={timing.VertexReadbackLatency.TotalMilliseconds:F2}ms, " +
			$"total={timing.TotalLatency.TotalMilliseconds:F2}ms";
	}

	private static string FormatBytes( long byteCount )
	{
		const double mebibyte = 1024.0 * 1024.0;
		return $"{byteCount / mebibyte:F2}MiB";
	}

	private static bool ValidateGpuFixture( GpuFixtureCase fixture, VoxelGpuComputeResult result, out string reason )
	{
		var expectedCells = checked( (uint)(fixture.Size * fixture.Size * fixture.Size) );
		if ( result.CellsProcessed != expectedCells || result.IndexCount != result.TrianglesEmitted * 3 ||
			result.InstanceCount != 1 || result.BufferOverflowAttempts != 0 )
		{
			reason = "base-counters";
			return false;
		}

		var expectedPlaneCells = checked( (uint)(fixture.Size * fixture.Size) );
		var passed = fixture.Type switch
		{
			GpuFixtureType.AllAir or GpuFixtureType.AllSolid =>
				result.ActiveCells == 0 && result.TrianglesEmitted == 0 && result.DegeneratesRejected == 0,
			GpuFixtureType.OffsetPlane =>
				result.ActiveCells == expectedPlaneCells && result.TrianglesEmitted > 0 && result.DegeneratesRejected == 0,
			GpuFixtureType.ExactZeroPlane =>
				result.ActiveCells == expectedPlaneCells && result.TrianglesEmitted > 0 && result.DegeneratesRejected == 0,
			GpuFixtureType.Sphere or GpuFixtureType.Saddle or GpuFixtureType.BoundarySphere =>
				result.ActiveCells > 0 && result.TrianglesEmitted > 0 && result.DegeneratesRejected == 0,
			_ => false
		};

		reason = passed ? "expected-topology" : "fixture-expectation";
		return passed;
	}

	private static GpuFixtureCase CreateGpuFixture( GpuFixtureType type, int size )
	{
		var sampleSize = size + 1;
		var distances = new float[checked( sampleSize * sampleSize * sampleSize )];
		var center = size * 0.5f;
		var sphereRadius = size * 0.31f + 0.137f;

		for ( var z = 0; z < sampleSize; z++ )
		{
			for ( var y = 0; y < sampleSize; y++ )
			{
				for ( var x = 0; x < sampleSize; x++ )
				{
					var distance = type switch
					{
						GpuFixtureType.AllAir => 1.0f,
						GpuFixtureType.AllSolid => -1.0f,
						GpuFixtureType.OffsetPlane => z - (center + 0.37f),
						GpuFixtureType.Sphere => new Vector3( x - center, y - center, z - center ).Length - sphereRadius,
						GpuFixtureType.Saddle => z - center - 0.18f * (x - center) * (y - center) / size - 0.173f,
						GpuFixtureType.ExactZeroPlane => z - System.MathF.Floor( center ),
						GpuFixtureType.BoundarySphere => new Vector3( x - size, y - center, z - center ).Length - sphereRadius,
						_ => 1.0f
					};
					distances[x + sampleSize * (y + sampleSize * z)] = distance;
				}
			}
		}

		return new GpuFixtureCase( type, type.ToString(), size, distances );
	}

	private static int CalculateGpuVertexCapacity( int size )
	{
		var worstCaseVertexCount = (long)size * size * size * 36;
		return (int)System.Math.Clamp( worstCaseVertexCount, 3, MaximumGpuVertices );
	}

	public void GenerateWorld()
	{
		var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		DisposeGpuSystems( false );
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

		StartGpuChunkWorld();
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
		if ( _gpuChunkStates.Count == 0 )
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

		foreach ( var pair in _gpuChunkStates )
		{
			if ( !pair.Value.Active || pair.Value.LastVertices is null || pair.Value.LastIndices is null )
			{
				emptyChunkCount++;
				continue;
			}
			var meshData = new VoxelMeshData( pair.Value.LastVertices.Length, pair.Value.LastIndices.Length );
			foreach ( var position in pair.Value.LastVertices ) meshData.Vertices.Add( new Vertex { Position = position } );
			meshData.Indices.AddRange( pair.Value.LastIndices );
			var buildElapsed = pair.Value.LastTiming.TotalLatency;
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

		var cpuColliderRebuiltChunkCount = 0;
		foreach ( var coordinate in changedChunks )
		{
			if ( _gpuChunkStates.TryGetValue( coordinate, out var gpuState ) )
			{
				MarkGpuChunkDirty( gpuState );
				if ( gpuState.Active )
				{
					continue;
				}
			}

			RebuildChunkRenderer( _chunks[coordinate] );
			cpuColliderRebuiltChunkCount++;
		}

		if ( changedChunks.Count > 0 )
		{
			_lastGpuBrushEditTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		}

		PumpGpuChunkBuildQueue();

		var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime( startTimestamp );
		Log.Info(
			$"Voxel brush: center={worldPosition}, radius={radius:F1}, displacement={displacement:F1}, " +
			$"changedSamples={changedSampleCount:N0}, dirtyChunks={changedChunks.Count:N0}, " +
			$"cpuColliderRebuiltChunks={cpuColliderRebuiltChunkCount:N0}, total={elapsed.TotalMilliseconds:F2} ms."
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

	private float[] CreateGpuSdfHalo( VoxelChunk chunk )
	{
		var haloSize = chunk.Size + 3;
		var halo = new float[checked( haloSize * haloSize * haloSize )];
		var origin = GetChunkVoxelOrigin( chunk.Coordinate );
		for ( var z = -1; z <= chunk.Size + 1; z++ )
		for ( var y = -1; y <= chunk.Size + 1; y++ )
		for ( var x = -1; x <= chunk.Size + 1; x++ )
		{
			var worldSample = origin + new Vector3Int( x, y, z );
			halo[(x + 1) + haloSize * ((y + 1) + haloSize * (z + 1))] = GetWorldSdfSample( worldSample, chunk );
		}
		return halo;
	}

	private float GetWorldSdfSample( Vector3Int worldSample, VoxelChunk preferredChunk )
	{
		var preferredOrigin = GetChunkVoxelOrigin( preferredChunk.Coordinate );
		var local = worldSample - preferredOrigin;
		if ( local.x >= 0 && local.x <= preferredChunk.Size && local.y >= 0 && local.y <= preferredChunk.Size &&
			local.z >= 0 && local.z <= preferredChunk.Size )
			return preferredChunk.GetVoxel( local.x, local.y, local.z ).Distance;

		foreach ( var candidate in _chunks.Values )
		{
			var candidateLocal = worldSample - GetChunkVoxelOrigin( candidate.Coordinate );
			if ( candidateLocal.x >= 0 && candidateLocal.x <= candidate.Size && candidateLocal.y >= 0 && candidateLocal.y <= candidate.Size &&
				candidateLocal.z >= 0 && candidateLocal.z <= candidate.Size )
				return candidate.GetVoxel( candidateLocal.x, candidateLocal.y, candidateLocal.z ).Distance;
		}

		return worldSample.z;
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
		renderer.Enabled = false;
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
		state.Renderer.Enabled = false;
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

	private static Model BuildGpuCollisionModel( ChunkRendererState rendererState, Vector3[] worldVertices, int[] indices )
	{
		var collisionVertices = new List<Vector3>( worldVertices.Length );
		for ( var index = 0; index < worldVertices.Length; index++ )
		{
			collisionVertices.Add( rendererState.GameObject.WorldTransform.PointToLocal( worldVertices[index] ) );
		}
		var collisionIndices = new List<int>( indices );

		return new ModelBuilder()
			.AddCollisionMesh( collisionVertices, collisionIndices )
			.AddTraceMesh( collisionVertices, collisionIndices )
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

	private void StartGpuChunkWorld()
	{
		DisposeGpuWorld( true );
		if ( _chunks.Count == 0 )
		{
			Log.Warning( "Voxel GPU world skipped because no chunks are loaded." );
			return;
		}

		foreach ( var pair in _chunks )
		{
			if ( !_chunkRenderers.ContainsKey( pair.Key ) )
			{
				continue;
			}

			var state = new GpuChunkRuntime( pair.Key ) { NeedsBuild = true };
			_gpuChunkStates.Add( pair.Key, state );
			QueueGpuChunkBuild( state );
			_gpuBatchDirtyChunks.Add( pair.Key );
		}

		_gpuWorldHasCompletedInitialBuild = false;
		_gpuBatchSummaryPending = true;
		_gpuBatchStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		Log.Info(
			$"Voxel GPU world scheduled: chunks={_gpuChunkStates.Count:N0}, concurrency={GpuChunkBuildConcurrency:N0}, " +
			"CPU renderers remain disabled; temporary CPU colliders stay active until each exact GPU visual/collider pair is ready."
		);
		PumpGpuChunkBuildQueue();
	}

	private void UpdateGpuChunkWorld()
	{
		if ( _gpuChunkStates.Count == 0 )
		{
			return;
		}

		foreach ( var state in _gpuChunkStates.Values )
		{
			if ( state.Probe is null )
			{
				continue;
			}

			state.Probe.ReleaseCompletedResources();
			state.Probe.TryPreparePendingEmit();
			if ( !state.InFlight || !state.Probe.TryTakeCompletedResult( out var result ) )
			{
				continue;
			}

			state.InFlight = false;
			if ( state.NeedsBuild )
			{
				QueueGpuChunkBuild( state );
				continue;
			}

			CompleteGpuChunkBuild( state, result );
		}

		PumpGpuChunkBuildQueue();
		TryLogGpuBatchSummary();
	}

	private void CompleteGpuChunkBuild( GpuChunkRuntime state, VoxelGpuComputeResult result )
	{
		var expectedCells = checked( (uint)(ChunkSize * ChunkSize * ChunkSize) );
		var expectedActiveCells = checked( (uint)(ChunkSize * ChunkSize) );
		var expectedTriangles = expectedActiveCells * 2;
		var expectedVertices = (uint)((ChunkSize + 1) * (ChunkSize + 1));
		var resultPassed = result.IndexCount == result.TrianglesEmitted * 3 &&
			result.InstanceCount == 1 &&
			result.CellsProcessed == expectedCells &&
			result.BufferOverflowAttempts == 0;
		if ( resultPassed && !state.HasEverBeenEdited && state.GenerationCount == 0 )
		{
			resultPassed = result.ActiveCells == expectedActiveCells &&
				result.TrianglesEmitted == expectedTriangles && result.VertexCount == expectedVertices;
		}

		if ( !_chunkRenderers.TryGetValue( state.Coordinate, out var rendererState ) )
		{
			FailGpuChunk( state, "renderer state is missing" );
			return;
		}

		var collisionReady = result.VertexCount == 0 ||
			(result.CollisionVertices is not null && result.CollisionVertices.Length == result.VertexCount &&
			 result.CollisionIndices is not null && result.CollisionIndices.Length == result.IndexCount);
		var wasInitialActivation = !state.Active;
		Model collisionModel = null;
		var collisionBuildTime = System.TimeSpan.Zero;
		if ( resultPassed && collisionReady && wasInitialActivation )
		{
			try
			{
				var collisionBuildStart = System.Diagnostics.Stopwatch.GetTimestamp();
				collisionModel = result.VertexCount > 0 ? BuildGpuCollisionModel( rendererState, result.CollisionVertices, result.CollisionIndices ) : null;
				collisionBuildTime = System.Diagnostics.Stopwatch.GetElapsedTime( collisionBuildStart );
			}
			catch ( System.Exception exception )
			{
				collisionReady = false;
				Log.Error( $"Voxel GPU exact collider build failed for chunk {state.Coordinate}: {exception.Message}" );
			}
		}

		var material = GpuTerrainMaterial ?? Material.FromShader( "shaders/voxel_gpu_grass.shader" );
		if ( !resultPassed || !collisionReady || !state.Probe.ActivateIndirectDraw( Scene.Camera, material ) )
		{
			FailGpuChunk(
				state,
				$"cells={result.CellsProcessed:N0}/{expectedCells:N0}, vertices={result.VertexCount:N0}, " +
				$"triangles={result.TrianglesEmitted:N0}, overflowAttempts={result.BufferOverflowAttempts:N0}, collisionReady={collisionReady}"
			);
			return;
		}

		if ( wasInitialActivation )
		{
			rendererState.Collider.Enabled = false;
			rendererState.Collider.Model = collisionModel;
			rendererState.Collider.Enabled = result.VertexCount > 0;
		}
		rendererState.Renderer.Enabled = false;

		state.Active = true;
		state.GenerationCount++;
		state.LastVertexCount = result.VertexCount;
		state.LastTriangleCount = result.TrianglesEmitted;
		state.LastVertices = result.CollisionVertices;
		state.LastIndices = result.CollisionIndices;
		state.LastTiming = result.Timing;
		state.LastColliderBuildTime = collisionBuildTime;
		state.ColliderUpdatePending = !wasInitialActivation;
		var computeScratchReleased = state.Probe.TryReleaseComputeWorkingSet();
		Log.Info(
			$"Voxel GPU chunk {(wasInitialActivation ? "initial generation" : "regeneration")}: result=PASS, chunk={state.Coordinate}, " +
			$"cells={result.CellsProcessed:N0}, activeCells={result.ActiveCells:N0}, vertices={result.VertexCount:N0}, indices={result.IndexCount:N0}, triangles={result.TrianglesEmitted:N0}, " +
			$"degeneratesRejected={result.DegeneratesRejected:N0}, overflowAttempts={result.BufferOverflowAttempts:N0}, " +
			$"slivers={result.SliverTriangles:N0}, " +
			$"gpuBuffers={FormatBytes( state.Probe.EstimatedGpuBufferBytes )}, pooledVertexCapacity={state.Probe.VertexCapacity:N0}, " +
			$"retiredVertexCapacity={state.Probe.RetiredVertexCapacity:N0}, retiredIndexCapacity={state.Probe.RetiredIndexCapacity:N0}, " +
			$"outputBufferAllocations={state.Probe.OutputBufferAllocationCount:N0}, " +
			$"computeScratch={(computeScratchReleased ? "released" : "retained")}, " +
			$"asyncLatency=[{FormatGpuTimings( result.Timing )}], colliderBuild={collisionBuildTime.TotalMilliseconds:F2}ms, " +
			$"cpuRenderer=disabled, collider={(state.ColliderUpdatePending ? "gpu-exact-pending" : "gpu-exact")}."
		);
	}

	private void FlushSettledGpuColliders()
	{
		if ( _lastGpuBrushEditTimestamp == 0 ||
			System.Diagnostics.Stopwatch.GetElapsedTime( _lastGpuBrushEditTimestamp ).TotalSeconds < GpuColliderEditSettleSeconds )
		{
			return;
		}

		foreach ( var state in _gpuChunkStates.Values )
		{
			if ( !state.ColliderUpdatePending || state.InFlight || state.NeedsBuild || state.Failed ||
				!_chunkRenderers.TryGetValue( state.Coordinate, out var rendererState ) )
			{
				continue;
			}

			try
			{
				var buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
				var collisionModel = state.LastVertexCount > 0
					? BuildGpuCollisionModel( rendererState, state.LastVertices, state.LastIndices )
					: null;
				rendererState.Collider.Enabled = false;
				rendererState.Collider.Model = collisionModel;
				rendererState.Collider.Enabled = state.LastVertexCount > 0;
				state.LastColliderBuildTime = System.Diagnostics.Stopwatch.GetElapsedTime( buildStart );
				state.ColliderUpdatePending = false;
				Log.Info(
					$"Voxel GPU exact collider settled: chunk={state.Coordinate}, vertices={state.LastVertexCount:N0}, " +
					$"triangles={state.LastTriangleCount:N0}, build={state.LastColliderBuildTime.TotalMilliseconds:F2}ms."
				);
			}
			catch ( System.Exception exception )
			{
				Log.Error( $"Voxel GPU exact collider settle failed for chunk {state.Coordinate}: {exception.Message}" );
			}
		}
	}

	private void FailGpuChunk( GpuChunkRuntime state, string reason )
	{
		Log.Error( $"Voxel GPU chunk {state.Coordinate}: result=FAIL, {reason}. CPU collision retained; GPU visual unavailable." );
		state.Probe?.Dispose();
		state.Probe = null;
		state.InFlight = false;
		state.NeedsBuild = false;
		state.Failed = true;
		if ( state.Active && _chunks.TryGetValue( state.Coordinate, out var chunk ) )
		{
			state.Active = false;
			RebuildChunkRenderer( chunk );
		}
	}

	private void MarkGpuChunkDirty( GpuChunkRuntime state )
	{
		if ( state.Failed )
		{
			return;
		}

		state.HasEverBeenEdited = true;
		state.NeedsBuild = true;
		if ( !_gpuBatchSummaryPending )
		{
			_gpuBatchSummaryPending = true;
			_gpuBatchStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
			_gpuBatchDirtyChunks.Clear();
		}
		_gpuBatchDirtyChunks.Add( state.Coordinate );

		if ( !state.InFlight )
		{
			QueueGpuChunkBuild( state );
		}
	}

	private void QueueGpuChunkBuild( GpuChunkRuntime state )
	{
		if ( state.Failed || state.InFlight || !_gpuQueuedChunks.Add( state.Coordinate ) )
		{
			return;
		}

		_gpuChunkBuildQueue.Enqueue( state.Coordinate );
	}

	private void PumpGpuChunkBuildQueue()
	{
		var inFlightCount = 0;
		foreach ( var state in _gpuChunkStates.Values )
		{
			inFlightCount += state.InFlight ? 1 : 0;
		}

		while ( inFlightCount < GpuChunkBuildConcurrency && _gpuChunkBuildQueue.TryDequeue( out var coordinate ) )
		{
			_gpuQueuedChunks.Remove( coordinate );
			if ( !_gpuChunkStates.TryGetValue( coordinate, out var state ) || state.Failed || state.InFlight || !state.NeedsBuild ||
				!_chunks.TryGetValue( coordinate, out var chunk ) )
			{
				continue;
			}

			try
			{
				if ( state.Probe is null )
				{
					var localOrigin = new Vector3( GetChunkVoxelOrigin( coordinate ) ) * VoxelSize;
					var worldOrigin = GameObject.WorldTransform.PointToWorld( localOrigin );
					state.Probe = new VoxelGpuComputeProbe(
						Scene.SceneWorld,
						CreateGpuSdfHalo( chunk ),
						chunk.Size,
						worldOrigin,
						VoxelSize,
						CalculateGpuVertexCapacity( chunk.Size ),
						true
					);
					state.Probe.Run();
				}
				else if ( !state.Probe.TryRegenerate( CreateGpuSdfHalo( chunk ) ) )
				{
					QueueGpuChunkBuild( state );
					break;
				}

				state.NeedsBuild = false;
				state.InFlight = true;
				inFlightCount++;
			}
			catch ( System.Exception exception )
			{
				FailGpuChunk( state, $"failed to schedule: {exception.Message}" );
			}
		}
	}

	private void TryLogGpuBatchSummary()
	{
		if ( !_gpuBatchSummaryPending || _gpuChunkBuildQueue.Count != 0 )
		{
			return;
		}

		var activeCount = 0;
		var failedCount = 0;
		long vertexCount = 0;
		long triangleCount = 0;
		long gpuBufferBytes = 0;
		double totalAsyncMilliseconds = 0.0;
		double totalColliderMilliseconds = 0.0;
		foreach ( var state in _gpuChunkStates.Values )
		{
			if ( state.InFlight || state.NeedsBuild )
			{
				return;
			}

			activeCount += state.Active ? 1 : 0;
			failedCount += state.Failed ? 1 : 0;
			vertexCount += state.LastVertexCount;
			triangleCount += state.LastTriangleCount;
			gpuBufferBytes += state.Probe?.EstimatedGpuBufferBytes ?? 0;
			totalAsyncMilliseconds += state.LastTiming.TotalLatency.TotalMilliseconds;
			totalColliderMilliseconds += state.LastColliderBuildTime.TotalMilliseconds;
		}

		var elapsed = _gpuBatchStartTimestamp != 0
			? System.Diagnostics.Stopwatch.GetElapsedTime( _gpuBatchStartTimestamp )
			: System.TimeSpan.Zero;
		var batchKind = _gpuWorldHasCompletedInitialBuild ? "edit batch" : "initial world";
		var averageAsyncMilliseconds = activeCount > 0 ? totalAsyncMilliseconds / activeCount : 0.0;
		Log.Info(
			$"Voxel GPU {batchKind}: result={(failedCount == 0 ? "PASS" : "FAIL")}, chunks={_gpuChunkStates.Count:N0}, " +
			$"active={activeCount:N0}, failed={failedCount:N0}, touched={_gpuBatchDirtyChunks.Count:N0}, concurrency={GpuChunkBuildConcurrency:N0}, " +
			$"vertices={vertexCount:N0}, triangles={triangleCount:N0}, gpuBuffers={FormatBytes( gpuBufferBytes )}, " +
			$"averageAsyncLatency={averageAsyncMilliseconds:F2}ms, colliderBuildTotal={totalColliderMilliseconds:F2}ms, batchElapsed={elapsed.TotalMilliseconds:F2}ms."
		);

		_gpuWorldHasCompletedInitialBuild = true;
		_gpuBatchSummaryPending = false;
		_gpuBatchDirtyChunks.Clear();
	}

	private void DisposeGpuFixtureProbe()
	{
		_gpuComputeProbe?.Dispose();
		_gpuComputeProbe = null;
	}

	private void DisposeGpuWorld( bool restoreCpuChunks )
	{
		foreach ( var state in _gpuChunkStates.Values )
		{
			state.Probe?.Dispose();
		}

		if ( restoreCpuChunks )
		{
			foreach ( var state in _gpuChunkStates.Values )
			{
				if ( state.Active && _chunks.TryGetValue( state.Coordinate, out var chunk ) )
				{
					RebuildChunkRenderer( chunk );
				}
			}
		}

		_gpuChunkStates.Clear();
		_gpuChunkBuildQueue.Clear();
		_gpuQueuedChunks.Clear();
		_gpuBatchDirtyChunks.Clear();
		_gpuBatchSummaryPending = false;
		_gpuWorldHasCompletedInitialBuild = false;
		_gpuBatchStartTimestamp = 0;
		_lastGpuBrushEditTimestamp = 0;
	}

	private void DisposeGpuSystems( bool restoreCpuChunks )
	{
		DisposeGpuFixtureProbe();
		DisposeGpuWorld( restoreCpuChunks );
	}

	private enum GpuFixtureType
	{
		AllAir,
		AllSolid,
		OffsetPlane,
		Sphere,
		Saddle,
		ExactZeroPlane,
		BoundarySphere
	}

	private sealed class GpuFixtureCase
	{
		public GpuFixtureType Type { get; }
		public string Name { get; }
		public int Size { get; }
		public float[] Distances { get; }

		public GpuFixtureCase( GpuFixtureType type, string name, int size, float[] distances )
		{
			Type = type;
			Name = name;
			Size = size;
			Distances = distances;
		}
	}

	private sealed class GpuChunkRuntime
	{
		public Vector3Int Coordinate { get; }
		public VoxelGpuComputeProbe Probe { get; set; }
		public bool Active { get; set; }
		public bool Failed { get; set; }
		public bool InFlight { get; set; }
		public bool NeedsBuild { get; set; }
		public bool HasEverBeenEdited { get; set; }
		public int GenerationCount { get; set; }
		public uint LastVertexCount { get; set; }
		public uint LastTriangleCount { get; set; }
		public Vector3[] LastVertices { get; set; }
		public int[] LastIndices { get; set; }
		public VoxelGpuTimingReport LastTiming { get; set; }
		public System.TimeSpan LastColliderBuildTime { get; set; }
		public bool ColliderUpdatePending { get; set; }

		public GpuChunkRuntime( Vector3Int coordinate )
		{
			Coordinate = coordinate;
		}
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
