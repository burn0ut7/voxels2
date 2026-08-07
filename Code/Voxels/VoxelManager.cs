public sealed class VoxelManager : Component
{
	public const string ChunkTag = "voxel_chunk";

	private const int MinimumChunkSize = 4;
	private const int MaximumChunkSize = 128;
	private const int MaximumChunkRadius = 128;
	private const int MaximumDetailedChunkLogs = 256;
	private const int MaximumConcurrentCpuChunkBuilds = 32;
	private const int MaximumCpuMeshUploadsPerFrame = 16;
	private const double CollisionEditSettleSeconds = 0.20;
	private const int MaximumCollisionChunkRadius = 16;
	private const int MaximumCollisionBuildsPerFrame = 16;
	private const int MaximumConcurrentCollisionBuilds = 8;

	private readonly Dictionary<Vector3Int, VoxelChunk> _chunks = new();
	private readonly object _sdfLock = new();
	private readonly Dictionary<Vector3Int, ChunkCollisionState> _chunkColliders = new();
	private readonly Queue<Vector3Int> _collisionBuildQueue = new();
	private readonly HashSet<Vector3Int> _collisionQueuedChunks = new();
	private readonly HashSet<Vector3Int> _collisionDesiredChunks = new();
	private readonly Dictionary<Vector3Int, CpuChunkRuntime> _cpuChunkStates = new();
	private readonly Queue<Vector3Int> _cpuChunkBuildQueue = new();
	private readonly HashSet<Vector3Int> _cpuQueuedChunks = new();
	private readonly List<double> _cpuBatchFrameMilliseconds = new( 4096 );
	private readonly List<System.Threading.Tasks.Task<WorldGenerationWorkerResult>> _worldGenerationTasks = new();
	private readonly List<double> _worldGenerationFrameMilliseconds = new( 512 );
	private long _cpuBatchStartTimestamp;
	private bool _cpuBatchSummaryPending;
	private long _lastCollisionInterestTimestamp;
	private long _worldGenerationStartTimestamp;
	private bool _worldGenerationPending;
	private double _lastWorldGenerationElapsedMilliseconds;
	private double _lastWorldGenerationWorkerMilliseconds;
	private double _lastWorldGenerationP95FrameMilliseconds;
	private double _lastWorldGenerationMaximumFrameMilliseconds;
	private double _lastVisualBatchElapsedMilliseconds;
	private double _lastVisualBatchAverageFrameMilliseconds;
	private double _lastVisualBatchP95FrameMilliseconds;
	private double _lastVisualBatchMaximumFrameMilliseconds;
	private int _cpuBatchCompletedBuilds;
	private double _cpuBatchSnapshotWaitMilliseconds;
	private double _cpuBatchSnapshotCopyMilliseconds;
	private double _cpuBatchWorkerMeshMilliseconds;
	private double _cpuBatchUploadMilliseconds;
	private double _lastVisualSnapshotWaitMilliseconds;
	private double _lastVisualSnapshotCopyMilliseconds;
	private double _lastVisualWorkerMeshMilliseconds;
	private double _lastVisualUploadMilliseconds;
	private long _totalVisualBuildsCompleted;
	private double _totalVisualBatchElapsedMilliseconds;
	private double _totalSnapshotWaitMilliseconds;
	private double _totalSnapshotCopyMilliseconds;
	private double _totalWorkerMeshMilliseconds;
	private double _totalMainThreadUploadMilliseconds;
	private long _callManagerUpdates;
	private long _callWorldGenerationRequests;
	private long _callWorldGenerationPolls;
	private long _callWorldChunksGenerated;
	private long _callBrushRequests;
	private long _callBrushChunkTests;
	private long _callBrushSamplesTested;
	private long _callBrushSamplesChanged;
	private long _callVisualWorldStarts;
	private long _callVisualQueuePumps;
	private long _callVisualBuildsQueued;
	private long _callVisualBuildsStarted;
	private long _callSdfHaloSnapshots;
	private long _callSdfHaloSamplesCopied;
	private long _callVisualBuildsCompleted;
	private long _callVisualUploads;
	private long _callCollisionInterestRefreshes;
	private long _callCollisionQueuePumps;
	private long _callCollisionBuildsQueued;
	private long _callCollisionBuildsStarted;
	private long _callCollisionSnapshotSamplesCopied;
	private long _callCollisionBuildsCompleted;
	private long _callCollisionUploads;

	[Property, Group( "World" ), Range( MinimumChunkSize, MaximumChunkSize )]
	public int ChunkSize { get; set; } = 32;

	[Property, Group( "World" ), Range( 1, MaximumChunkRadius )]
	public int ChunkRadius { get; set; } = 4;

	[Property, Group( "World" ), Range( 1.0f, 128.0f )]
	public float VoxelSize { get; set; } = 16.0f;

	[Property, Group( "World" ), Range( 1.0f, MaximumChunkSize )]
	public float SdfClampDistance { get; set; } = 8.0f;

	[Property, Group( "Rendering" )]
	public Material TerrainMaterial { get; set; }

	[Property, Group( "Meshing" ), Range( 1, MaximumConcurrentCpuChunkBuilds )]
	public int CpuChunkBuildConcurrency { get; set; } = 4;

	[Property, Group( "Rendering" ), Range( 1, MaximumCpuMeshUploadsPerFrame )]
	public int CpuMeshUploadsPerFrame { get; set; } = 4;

	[Property, Group( "Rendering" ), Range( 0.25f, 12.0f )]
	public float CpuMainThreadBudgetMilliseconds { get; set; } = 4.0f;

	[Property, Group( "Collision" ), Range( 1, MaximumCollisionChunkRadius )]
	public int CollisionChunkRadius { get; set; } = 2;

	[Property, Group( "Collision" ), Range( 1, 8 )]
	public int CollisionResolutionDivisor { get; set; } = 2;

	[Property, Group( "Collision" ), Range( 1, MaximumCollisionBuildsPerFrame )]
	public int CollisionBuildsPerFrame { get; set; } = 4;

	[Property, Group( "Collision" ), Range( 1, MaximumConcurrentCollisionBuilds )]
	public int CollisionBuildConcurrency { get; set; } = 2;

	[Property, Group( "Diagnostics" )]
	public bool LogGeneration { get; set; }

	[Property, Group( "Diagnostics" )]
	public bool CaptureCallCounts { get; set; }

	[Property, Group( "Diagnostics" ), Range( 0, MaximumDetailedChunkLogs )]
	public int DetailedChunkLogLimit { get; set; } = 64;

	public IReadOnlyDictionary<Vector3Int, VoxelChunk> Chunks => _chunks;
	public int LoadedChunkCount => _chunks.Count;
	public int ChunkDiameter => ChunkRadius * 2;
	public int ConfiguredChunkCount => checked( ChunkDiameter * ChunkDiameter );
	public bool IsWorldGenerationPending => _worldGenerationPending;
	public bool IsTerrainSettled => LoadedChunkCount == ConfiguredChunkCount &&
		(Application.IsDedicatedServer || _cpuChunkStates.Count == LoadedChunkCount) &&
		GetPendingVisualBuildCount() == 0 && GetPendingCollisionBuildCount() == 0 &&
		!_worldGenerationPending && !_cpuBatchSummaryPending;

	protected override void OnValidate()
	{
		ChunkSize = System.Math.Clamp( ChunkSize, MinimumChunkSize, MaximumChunkSize );
		ChunkRadius = System.Math.Clamp( ChunkRadius, 1, MaximumChunkRadius );
		VoxelSize = System.Math.Clamp( VoxelSize, 1.0f, 128.0f );
		SdfClampDistance = System.Math.Clamp( SdfClampDistance, 1.0f, (float)MaximumChunkSize );
		CpuChunkBuildConcurrency = System.Math.Clamp( CpuChunkBuildConcurrency, 1, MaximumConcurrentCpuChunkBuilds );
		CpuMeshUploadsPerFrame = System.Math.Clamp( CpuMeshUploadsPerFrame, 1, MaximumCpuMeshUploadsPerFrame );
		CpuMainThreadBudgetMilliseconds = System.Math.Clamp( CpuMainThreadBudgetMilliseconds, 0.25f, 12.0f );
		CollisionChunkRadius = System.Math.Clamp( CollisionChunkRadius, 1, MaximumCollisionChunkRadius );
		CollisionResolutionDivisor = System.Math.Clamp( CollisionResolutionDivisor, 1, 8 );
		CollisionBuildsPerFrame = System.Math.Clamp( CollisionBuildsPerFrame, 1, MaximumCollisionBuildsPerFrame );
		CollisionBuildConcurrency = System.Math.Clamp( CollisionBuildConcurrency, 1, MaximumConcurrentCollisionBuilds );
		DetailedChunkLogLimit = System.Math.Clamp( DetailedChunkLogLimit, 0, MaximumDetailedChunkLogs );
	}

	protected override void OnStart()
	{
		GenerateWorld();
	}

	protected override void OnUpdate()
	{
		CountCall( ref _callManagerUpdates );
		UpdateWorldGeneration();
		if ( _worldGenerationPending )
		{
			return;
		}
		UpdateCpuChunkWorld();
		UpdateCpuCollisionWorld();
	}

	protected override void OnDisabled()
	{
		DisposeCpuVisualWorld();
	}

	protected override void OnDestroy()
	{
		DisposeCpuVisualWorld();
	}


	private static string FormatBytes( long byteCount )
	{
		const double mebibyte = 1024.0 * 1024.0;
		return $"{byteCount / mebibyte:F2}MiB";
	}


	public void GenerateWorld()
	{
		CountCall( ref _callWorldGenerationRequests );
		if ( _worldGenerationPending )
		{
			Log.Warning( "Voxel world generation is already running." );
			return;
		}

		DisposeCpuVisualWorld();
		ClearChunkColliders();
		lock ( _sdfLock )
		{
			_chunks.Clear();
		}

		var coordinates = new List<Vector3Int>( ConfiguredChunkCount );
		for ( var y = -ChunkRadius; y < ChunkRadius; y++ )
		for ( var x = -ChunkRadius; x < ChunkRadius; x++ )
		{
			coordinates.Add( new Vector3Int( x, y, 0 ) );
		}
		coordinates.Sort( (left, right) => GetChunkBuildPriority( left ).CompareTo( GetChunkBuildPriority( right ) ) );

		var workerCount = System.Math.Min( CpuChunkBuildConcurrency, coordinates.Count );
		var chunkSize = ChunkSize;
		var sdfClampDistance = SdfClampDistance;
		var captureTopology = LogGeneration;
		_worldGenerationTasks.Clear();
		_worldGenerationFrameMilliseconds.Clear();
		_worldGenerationStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		_worldGenerationPending = true;
		for ( var workerIndex = 0; workerIndex < workerCount; workerIndex++ )
		{
			var partitionIndex = workerIndex;
			_worldGenerationTasks.Add( GameTask.RunInThreadAsync( () =>
			{
				var generated = new List<GeneratedChunkResult>( (coordinates.Count + workerCount - 1) / workerCount );
				for ( var coordinateIndex = partitionIndex; coordinateIndex < coordinates.Count; coordinateIndex += workerCount )
				{
					var start = System.Diagnostics.Stopwatch.GetTimestamp();
					var chunk = new VoxelChunk( coordinates[coordinateIndex], chunkSize, sdfClampDistance );
					FillChunk( chunk, chunkSize );
					var dataElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( start );
					var report = captureTopology
						? AnalyzeChunk( chunk, 0, 0, dataElapsed, System.Diagnostics.Stopwatch.GetElapsedTime( start ) )
						: default;
					generated.Add( new GeneratedChunkResult( chunk, report, System.Diagnostics.Stopwatch.GetElapsedTime( start ) ) );
					CountCall( ref _callWorldChunksGenerated );
				}
				return new WorldGenerationWorkerResult( generated );
			} ) );
		}

		Log.Info( $"Voxel CPU SDF generation scheduled: chunks={coordinates.Count:N0}, workers={workerCount:N0}, authoritative=CPU." );
	}

	private void UpdateWorldGeneration()
	{
		CountCall( ref _callWorldGenerationPolls );
		if ( !_worldGenerationPending )
		{
			return;
		}

		if ( _worldGenerationFrameMilliseconds.Count < _worldGenerationFrameMilliseconds.Capacity )
		{
			_worldGenerationFrameMilliseconds.Add( Sandbox.Diagnostics.PerformanceStats.FrameTime * 1000.0 );
		}

		foreach ( var task in _worldGenerationTasks )
		{
			if ( !task.IsCompleted )
			{
				return;
			}
		}

		foreach ( var task in _worldGenerationTasks )
		{
			if ( task.IsFaulted )
			{
				_worldGenerationPending = false;
				Log.Error( $"Voxel CPU SDF generation failed: {task.Exception?.GetBaseException().Message}" );
				_worldGenerationTasks.Clear();
				return;
			}
			if ( task.IsCanceled )
			{
				_worldGenerationPending = false;
				Log.Error( "Voxel CPU SDF generation was canceled." );
				_worldGenerationTasks.Clear();
				return;
			}
		}

		var generated = new List<GeneratedChunkResult>( ConfiguredChunkCount );
		double workerMilliseconds = 0.0;
		foreach ( var task in _worldGenerationTasks )
		{
			foreach ( var result in task.Result.Chunks )
			{
				generated.Add( result );
				workerMilliseconds += result.BuildTime.TotalMilliseconds;
			}
		}
		generated.Sort( (left, right) => GetChunkBuildPriority( left.Chunk.Coordinate ).CompareTo( GetChunkBuildPriority( right.Chunk.Coordinate ) ) );

		lock ( _sdfLock )
		{
			foreach ( var result in generated )
			{
				_chunks.Add( result.Chunk.Coordinate, result.Chunk );
			}
		}

		var allAirChunkCount = 0;
		var allSolidChunkCount = 0;
		var surfaceChunkCount = 0;
		var detailedChunkCount = 0;
		var uniformChunkCount = 0;
		long totalSampleCount = 0;
		long totalSdfStorageBytes = 0;
		foreach ( var result in generated )
		{
			totalSdfStorageBytes += result.Chunk.EstimatedStorageBytes;
			uniformChunkCount += result.Chunk.IsUniform ? 1 : 0;
			if ( !LogGeneration )
			{
				continue;
			}

			totalSampleCount += result.Report.SampleCount;
			allAirChunkCount += result.Report.IsAllAir ? 1 : 0;
			allSolidChunkCount += result.Report.IsAllSolid ? 1 : 0;
			surfaceChunkCount += result.Report.HasSurface ? 1 : 0;
			if ( detailedChunkCount < DetailedChunkLogLimit )
			{
				LogChunkTopology( result.Chunk, result.Report );
				detailedChunkCount++;
			}
		}

		var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime( _worldGenerationStartTimestamp );
		_worldGenerationFrameMilliseconds.Sort();
		var p95FrameMilliseconds = _worldGenerationFrameMilliseconds.Count > 0
			? _worldGenerationFrameMilliseconds[(int)System.Math.Clamp( System.Math.Ceiling( _worldGenerationFrameMilliseconds.Count * 0.95 ) - 1, 0, _worldGenerationFrameMilliseconds.Count - 1 )]
			: 0.0;
		var maximumFrameMilliseconds = _worldGenerationFrameMilliseconds.Count > 0 ? _worldGenerationFrameMilliseconds[^1] : 0.0;
		_lastWorldGenerationElapsedMilliseconds = elapsed.TotalMilliseconds;
		_lastWorldGenerationWorkerMilliseconds = workerMilliseconds;
		_lastWorldGenerationP95FrameMilliseconds = p95FrameMilliseconds;
		_lastWorldGenerationMaximumFrameMilliseconds = maximumFrameMilliseconds;
		Log.Info(
			$"Voxel CPU SDF generation batch: result=PASS, chunks={generated.Count:N0}, workers={_worldGenerationTasks.Count:N0}, " +
			$"workerTotal={workerMilliseconds:F2}ms, elapsed={elapsed.TotalMilliseconds:F2}ms, frames={_worldGenerationFrameMilliseconds.Count:N0}, " +
			$"frameMs(p95/max)={p95FrameMilliseconds:F2}/{maximumFrameMilliseconds:F2}."
		);

		if ( LogGeneration )
		{
			LogWorldTopology( totalSampleCount, totalSdfStorageBytes, uniformChunkCount, allAirChunkCount, allSolidChunkCount, surfaceChunkCount, detailedChunkCount, elapsed );
		}

		_worldGenerationPending = false;
		_worldGenerationTasks.Clear();
		StartCpuChunkWorld();
	}

	[Button]
	public void RebuildVisualWorld()
	{
		DisposeCpuVisualWorld();
		StartCpuChunkWorld();
	}

	public VoxelChunk GenerateChunk( Vector3Int coordinate )
	{
		lock ( _sdfLock )
		{
			return GenerateChunk( coordinate, LogGeneration, out _ );
		}
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
		var chunk = new VoxelChunk( coordinate, ChunkSize, SdfClampDistance );
		FillChunk( chunk, ChunkSize );
		var dataCompletedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		_chunks.Add( coordinate, chunk );
		const int vertexCount = 0;
		const int triangleCount = 0;

		if ( LogGeneration )
		{
			var dataElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( startTimestamp, dataCompletedTimestamp );
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
		lock ( _sdfLock )
		{
			return _chunks.TryGetValue( coordinate, out chunk );
		}
	}

	public VoxelTerrainDiagnostics CaptureTerrainDiagnostics()
	{
		long authoritativeSdfStorageBytes = 0;
		var uniformSdfChunks = 0;
		lock ( _sdfLock )
		{
			foreach ( var chunk in _chunks.Values )
			{
				authoritativeSdfStorageBytes += chunk.EstimatedStorageBytes;
				uniformSdfChunks += chunk.IsUniform ? 1 : 0;
			}
		}

		var activeVisualChunks = 0;
		var failedVisualChunks = 0;
		long vertices = 0;
		long triangles = 0;
		foreach ( var state in _cpuChunkStates.Values )
		{
			activeVisualChunks += state.Renderer?.Enabled == true ? 1 : 0;
			failedVisualChunks += state.Failed ? 1 : 0;
			vertices += state.VertexCount;
			triangles += state.TriangleCount;
		}

		var activeColliders = 0;
		long collisionTriangles = 0;
		foreach ( var state in _chunkColliders.Values )
		{
			activeColliders += state.Collider.Enabled ? 1 : 0;
			collisionTriangles += state.TriangleCount;
		}

		return new VoxelTerrainDiagnostics(
			LoadedChunkCount,
			authoritativeSdfStorageBytes,
			uniformSdfChunks,
			activeVisualChunks,
			failedVisualChunks,
			GetPendingVisualBuildCount(),
			_cpuBatchCompletedBuilds,
			vertices,
			triangles,
			activeColliders,
			GetPendingCollisionBuildCount(),
			collisionTriangles,
			_lastVisualSnapshotWaitMilliseconds,
			_lastVisualSnapshotCopyMilliseconds,
			_lastVisualWorkerMeshMilliseconds,
			_lastVisualUploadMilliseconds,
			_lastWorldGenerationElapsedMilliseconds,
			_lastWorldGenerationWorkerMilliseconds,
			_lastWorldGenerationP95FrameMilliseconds,
			_lastWorldGenerationMaximumFrameMilliseconds,
			_lastVisualBatchElapsedMilliseconds,
			_lastVisualBatchAverageFrameMilliseconds,
			_lastVisualBatchP95FrameMilliseconds,
			_lastVisualBatchMaximumFrameMilliseconds,
			_totalVisualBuildsCompleted,
			_totalVisualBatchElapsedMilliseconds,
			_totalSnapshotWaitMilliseconds,
			_totalSnapshotCopyMilliseconds,
			_totalWorkerMeshMilliseconds,
			_totalMainThreadUploadMilliseconds
		);
	}

	public VoxelCallCountSnapshot CaptureCallCountSnapshot()
	{
		return new VoxelCallCountSnapshot(
			System.Threading.Interlocked.Read( ref _callManagerUpdates ),
			System.Threading.Interlocked.Read( ref _callWorldGenerationRequests ),
			System.Threading.Interlocked.Read( ref _callWorldGenerationPolls ),
			System.Threading.Interlocked.Read( ref _callWorldChunksGenerated ),
			System.Threading.Interlocked.Read( ref _callBrushRequests ),
			System.Threading.Interlocked.Read( ref _callBrushChunkTests ),
			System.Threading.Interlocked.Read( ref _callBrushSamplesTested ),
			System.Threading.Interlocked.Read( ref _callBrushSamplesChanged ),
			System.Threading.Interlocked.Read( ref _callVisualWorldStarts ),
			System.Threading.Interlocked.Read( ref _callVisualQueuePumps ),
			System.Threading.Interlocked.Read( ref _callVisualBuildsQueued ),
			System.Threading.Interlocked.Read( ref _callVisualBuildsStarted ),
			System.Threading.Interlocked.Read( ref _callSdfHaloSnapshots ),
			System.Threading.Interlocked.Read( ref _callSdfHaloSamplesCopied ),
			System.Threading.Interlocked.Read( ref _callVisualBuildsCompleted ),
			System.Threading.Interlocked.Read( ref _callVisualUploads ),
			System.Threading.Interlocked.Read( ref _callCollisionInterestRefreshes ),
			System.Threading.Interlocked.Read( ref _callCollisionQueuePumps ),
			System.Threading.Interlocked.Read( ref _callCollisionBuildsQueued ),
			System.Threading.Interlocked.Read( ref _callCollisionBuildsStarted ),
			System.Threading.Interlocked.Read( ref _callCollisionSnapshotSamplesCopied ),
			System.Threading.Interlocked.Read( ref _callCollisionBuildsCompleted ),
			System.Threading.Interlocked.Read( ref _callCollisionUploads )
		);
	}

	private void CountCall( ref long counter, long amount = 1 )
	{
		if ( !CaptureCallCounts || amount <= 0 ) return;
		System.Threading.Interlocked.Add( ref counter, amount );
	}

	private int GetPendingVisualBuildCount()
	{
		var pending = _cpuChunkBuildQueue.Count;
		foreach ( var state in _cpuChunkStates.Values )
		{
			if ( state.Task is not null || state.CompletedGeneration < state.DesiredGeneration )
			{
				pending++;
			}
		}
		return pending;
	}

	private int GetPendingCollisionBuildCount()
	{
		var pending = _collisionBuildQueue.Count;
		foreach ( var state in _chunkColliders.Values )
		{
			if ( state.Task is not null || state.Dirty ||
				(_collisionDesiredChunks.Contains( state.Coordinate ) && state.CompletedGeneration < state.DesiredGeneration) )
			{
				pending++;
			}
		}
		return pending;
	}

	[Button]
	public void ReportMeshTopology()
	{
		if ( _cpuChunkStates.Count > 0 )
		{
			var active = 0;
			var failed = 0;
			long vertices = 0;
			long triangles = 0;
			double snapshotMilliseconds = 0.0;
			double snapshotWaitMilliseconds = 0.0;
			double meshingMilliseconds = 0.0;
			double uploadMilliseconds = 0.0;
			foreach ( var state in _cpuChunkStates.Values )
			{
				active += state.Renderer?.Enabled == true ? 1 : 0;
				failed += state.Failed ? 1 : 0;
				vertices += state.VertexCount;
				triangles += state.TriangleCount;
				snapshotMilliseconds += state.SnapshotTime.TotalMilliseconds;
				snapshotWaitMilliseconds += state.SnapshotWaitTime.TotalMilliseconds;
				meshingMilliseconds += state.MeshingTime.TotalMilliseconds;
				uploadMilliseconds += state.UploadTime.TotalMilliseconds;
			}
			Log.Info( $"Voxel mesh topology: visualPath=CPU-Transvoxel-regular/worker-pool, visualChunks={active:N0}/{_cpuChunkStates.Count:N0}, failed={failed:N0}, vertices={vertices:N0}, triangles={triangles:N0}, snapshotWaitTotal={snapshotWaitMilliseconds:F2}ms, snapshotCopyTotal={snapshotMilliseconds:F2}ms, workerMeshTotal={meshingMilliseconds:F2}ms, mainUploadTotal={uploadMilliseconds:F2}ms; collisionPath=CPU-marching-tetrahedra/{CollisionResolutionDivisor}:1." );
			return;
		}

		Log.Warning( "Mesh topology report skipped because the voxel world has no visual chunks." );
	}

	public int DisplaceSdf( Vector3 worldPosition, float radius, float displacement )
	{
		CountCall( ref _callBrushRequests );
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
		var writeWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
		System.TimeSpan writeWaitElapsed;
		lock ( _sdfLock )
		{
			writeWaitElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( writeWaitStart );
			CountCall( ref _callBrushChunkTests, _chunks.Count );
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
		}

		foreach ( var coordinate in changedChunks )
		{
			if ( _cpuChunkStates.TryGetValue( coordinate, out var cpuState ) )
			{
				MarkCpuChunkDirty( cpuState );
			}
			MarkCollisionDirty( coordinate );
		}

		PumpCpuChunkBuildQueue();

		var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime( startTimestamp );
		Log.Info(
			$"Voxel brush: center={worldPosition}, radius={radius:F1}, displacement={displacement:F1}, " +
			$"changedSamples={changedSampleCount:N0}, dirtyChunks={changedChunks.Count:N0}, " +
			$"cpuCollisionQueued={changedChunks.Count:N0}, sdfWriteWait={writeWaitElapsed.TotalMilliseconds:F2}ms, total={elapsed.TotalMilliseconds:F2} ms."
		);

		return changedChunks.Count;
	}

	private static void FillChunk( VoxelChunk chunk, int chunkSize )
	{
		var sampleSize = chunk.SampleSize;
		var chunkOrigin = new Vector3Int(
			chunk.Coordinate.x * chunkSize,
			chunk.Coordinate.y * chunkSize,
			(chunk.Coordinate.z - 1) * chunkSize
		);
		var minimumDistance = chunkOrigin.z;
		var maximumDistance = chunkOrigin.z + chunk.Size;
		if ( minimumDistance >= chunk.DistanceClamp )
		{
			chunk.Fill( new Voxel( chunk.DistanceClamp, VoxelMaterial.Air ) );
			return;
		}
		if ( maximumDistance <= -chunk.DistanceClamp )
		{
			chunk.Fill( new Voxel( -chunk.DistanceClamp, VoxelMaterial.Terrain ) );
			return;
		}

		for ( var z = 0; z < sampleSize; z++ )
		{
			var distance = chunkOrigin.z + z;
			var material = distance < 0.0f ? VoxelMaterial.Terrain : VoxelMaterial.Air;
			chunk.FillLayer( z, new Voxel( distance, material ) );
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
		var testedSampleCount = checked( (long)(maximumX - minimumX + 1) * (maximumY - minimumY + 1) * (maximumZ - minimumZ + 1) );
		CountCall( ref _callBrushSamplesTested, testedSampleCount );

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
					var distance = chunk.GetDistanceByIndex( index ) + sdfDisplacement * falloff;
					var material = distance < 0.0f ? VoxelMaterial.Terrain : VoxelMaterial.Air;
					chunk.SetVoxelByIndex( index, new Voxel( distance, material ) );
					changedSampleCount++;
				}
			}
		}

		CountCall( ref _callBrushSamplesChanged, changedSampleCount );
		return changedSampleCount;
	}

	private float[] CreateSdfHalo( VoxelChunk chunk )
	{
		var haloSize = chunk.Size + 3;
		var halo = new float[checked( haloSize * haloSize * haloSize )];
		CountCall( ref _callSdfHaloSnapshots );
		CountCall( ref _callSdfHaloSamplesCopied, halo.Length );
		var origin = GetChunkVoxelOrigin( chunk.Coordinate );
		for ( var z = -1; z <= chunk.Size + 1; z++ )
		{
			for ( var y = -1; y <= chunk.Size + 1; y++ )
			{
				var rowStart = haloSize * ((y + 1) + haloSize * (z + 1));
				if ( y >= 0 && y <= chunk.Size && z >= 0 && z <= chunk.Size )
				{
					chunk.CopyDistanceRowTo( y, z, halo, rowStart + 1 );
					halo[rowStart] = GetWorldSdfSample( origin + new Vector3Int( -1, y, z ), chunk );
					halo[rowStart + haloSize - 1] = GetWorldSdfSample( origin + new Vector3Int( chunk.Size + 1, y, z ), chunk );
					continue;
				}

				for ( var x = -1; x <= chunk.Size + 1; x++ )
				{
					var worldSample = origin + new Vector3Int( x, y, z );
					halo[rowStart + x + 1] = GetWorldSdfSample( worldSample, chunk );
				}
			}
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

		var candidateCoordinate = new Vector3Int(
			FloorDiv( worldSample.x, ChunkSize ),
			FloorDiv( worldSample.y, ChunkSize ),
			FloorDiv( worldSample.z, ChunkSize ) + 1
		);
		if ( _chunks.TryGetValue( candidateCoordinate, out var candidate ) )
		{
			var candidateLocal = worldSample - GetChunkVoxelOrigin( candidate.Coordinate );
			if ( candidateLocal.x >= 0 && candidateLocal.x <= candidate.Size && candidateLocal.y >= 0 && candidateLocal.y <= candidate.Size &&
				candidateLocal.z >= 0 && candidateLocal.z <= candidate.Size )
				return candidate.GetVoxel( candidateLocal.x, candidateLocal.y, candidateLocal.z ).Distance;
		}

		return System.Math.Clamp( (float)worldSample.z, -SdfClampDistance, SdfClampDistance );
	}

	private static int FloorDiv( int value, int divisor )
	{
		var quotient = value / divisor;
		return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
	}

	private ChunkCollisionState GetOrCreateChunkCollider( Vector3Int coordinate )
	{
		if ( _chunkColliders.TryGetValue( coordinate, out var existing ) )
		{
			return existing;
		}

		var collisionObject = new GameObject( true, $"Voxel Collision {coordinate}" );
		collisionObject.Parent = GameObject;
		collisionObject.Tags.Add( ChunkTag );
		var chunkOrigin = GetChunkVoxelOrigin( coordinate );
		collisionObject.LocalPosition = new Vector3( chunkOrigin.x, chunkOrigin.y, chunkOrigin.z ) * VoxelSize;
		var collider = collisionObject.AddComponent<ModelCollider>();
		collider.Static = true;
		collider.Enabled = false;
		var state = new ChunkCollisionState( coordinate, collisionObject, collider );
		_chunkColliders.Add( coordinate, state );
		return state;
	}

	private void UploadChunkCollider( ChunkCollisionState state, CollisionBuildResult result )
	{
		CountCall( ref _callCollisionBuildsCompleted );
		var modelStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var collisionModel = result.Mesh.Indices.Count > 0 ? BuildCollisionModel( result.Mesh ) : null;
		state.Collider.Enabled = false;
		state.Collider.Model = collisionModel;
		state.Collider.Enabled = result.Mesh.Indices.Count > 0;
		state.Built = true;
		state.Dirty = false;
		state.Queued = false;
		state.PinnedByEdit = false;
		state.CompletedGeneration = result.Generation;
		state.VertexCount = result.Mesh.Vertices.Count;
		state.TriangleCount = result.Mesh.Indices.Count / 3;
		state.SnapshotWaitTime = result.SnapshotWaitTime;
		state.SnapshotTime = result.SnapshotTime;
		state.MeshingTime = result.MeshingTime;
		state.ModelBuildTime = System.Diagnostics.Stopwatch.GetElapsedTime( modelStart );
		CountCall( ref _callCollisionUploads );
		if ( LogGeneration )
		{
			Log.Info(
				$"Voxel CPU collision: chunk={state.Coordinate}, resolution={result.Resolution}^3, " +
				$"vertices={state.VertexCount:N0}, triangles={state.TriangleCount:N0}, meshing={state.MeshingTime.TotalMilliseconds:F2}ms, " +
				$"snapshotWait={state.SnapshotWaitTime.TotalMilliseconds:F2}ms, snapshot={state.SnapshotTime.TotalMilliseconds:F2}ms, " +
				$"physicsModel={state.ModelBuildTime.TotalMilliseconds:F2}ms, objectReused=yes."
			);
		}
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

	private static ChunkTopologyReport AnalyzeChunk( VoxelChunk chunk, int vertexCount, int triangleCount, System.TimeSpan dataElapsed, System.TimeSpan totalElapsed )
	{
		var solidSampleCount = 0;
		var minimumDistance = float.MaxValue;
		var maximumDistance = float.MinValue;

		for ( var index = 0; index < chunk.SampleCount; index++ )
		{
			var voxel = chunk.GetVoxelByIndex( index );
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
		var hasCollider = _chunkColliders.TryGetValue( chunk.Coordinate, out var colliderState ) && colliderState.Built;

		Log.Info(
			$"Voxel chunk {chunk.Coordinate}: state={state}, voxelOrigin={voxelOrigin}, localPosition={localPosition}, worldPosition={worldPosition}, " +
			$"samples={report.SampleCount:N0} (solid={report.SolidSampleCount:N0}, air={report.AirSampleCount:N0}), " +
			$"SDF=[{report.MinimumDistance:F3}, {report.MaximumDistance:F3}], storage={FormatBytes( chunk.EstimatedStorageBytes )}, " +
			$"uniform={(chunk.IsUniform ? "yes" : "no")}, mesh={report.VertexCount:N0} vertices/{report.TriangleCount:N0} triangles, " +
			$"cpuCollider={(hasCollider ? "yes" : "no")}, data={report.DataElapsed.TotalMilliseconds:F2} ms, total={report.TotalElapsed.TotalMilliseconds:F2} ms."
		);
	}

	private void LogWorldTopology( long totalSampleCount, long totalSdfStorageBytes, int uniformChunkCount, int allAirChunkCount, int allSolidChunkCount, int surfaceChunkCount, int detailedChunkCount, System.TimeSpan elapsed )
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
		var previousStorageBytes = totalSampleCount * 8L;
		var storageReduction = previousStorageBytes > 0 ? 100.0 * (1.0 - (double)totalSdfStorageBytes / previousStorageBytes) : 0.0;

		Log.Info(
			$"Voxel topology: radius={ChunkRadius}, diameter={ChunkDiameter}, configured={ConfiguredChunkCount:N0}, loaded={LoadedChunkCount:N0}, " +
			$"surface={surfaceChunkCount:N0}, all-solid={allSolidChunkCount:N0}, all-air={allAirChunkCount:N0}, cpuColliders={_chunkColliders.Count:N0}, " +
			$"samples={totalSampleCount:N0}, SDFStorage={FormatBytes( totalSdfStorageBytes )} (previous={FormatBytes( previousStorageBytes )}, reduction={storageReduction:F1}%, uniformChunks={uniformChunkCount:N0}), " +
			$"SDFClamp=+/-{SdfClampDistance:F2} voxels, maxQuantizationError={SdfClampDistance / (32767.0f * 2.0f):F6} voxels, " +
			$"coordinates={minimumCoordinate}..{maximumCoordinate}, voxelBounds={minimumVoxel}..{maximumVoxel}, " +
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

	private void ClearChunkColliders()
	{
		foreach ( var state in _chunkColliders.Values )
		{
			state.GameObject.Destroy();
		}

		_chunkColliders.Clear();
		_collisionBuildQueue.Clear();
		_collisionQueuedChunks.Clear();
		_collisionDesiredChunks.Clear();
		_lastCollisionInterestTimestamp = 0;
	}


	private static long GetChunkBuildPriority( Vector3Int coordinate )
	{
		return (long)coordinate.x * coordinate.x + (long)coordinate.y * coordinate.y + (long)coordinate.z * coordinate.z;
	}


	private void UpdateCpuCollisionWorld()
	{
		if ( _lastCollisionInterestTimestamp == 0 ||
			System.Diagnostics.Stopwatch.GetElapsedTime( _lastCollisionInterestTimestamp ).TotalSeconds >= 0.25 )
		{
			RefreshCollisionInterests();
		}

		PumpCollisionBuildQueue();
	}

	private void RefreshCollisionInterests()
	{
		CountCall( ref _callCollisionInterestRefreshes );
		_lastCollisionInterestTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		_collisionDesiredChunks.Clear();
		_collisionBuildQueue.Clear();
		_collisionQueuedChunks.Clear();
		var observers = new List<Vector3Int>();

		foreach ( var controller in Scene.GetAllComponents<PlayerController>() )
		{
			observers.Add( GetCollisionObserverChunk( controller.WorldPosition ) );
		}

		if ( observers.Count == 0 && Scene.Camera is not null )
		{
			observers.Add( GetCollisionObserverChunk( Scene.Camera.WorldPosition ) );
		}

		foreach ( var observer in observers )
		{
			for ( var y = -CollisionChunkRadius; y < CollisionChunkRadius; y++ )
			for ( var x = -CollisionChunkRadius; x < CollisionChunkRadius; x++ )
			{
				var coordinate = new Vector3Int( observer.x + x, observer.y + y, 0 );
				if ( _chunks.ContainsKey( coordinate ) )
				{
					_collisionDesiredChunks.Add( coordinate );
				}
			}
		}

		foreach ( var pair in _chunkColliders )
		{
			if ( pair.Value.PinnedByEdit )
			{
				_collisionDesiredChunks.Add( pair.Key );
			}
			else if ( !_collisionDesiredChunks.Contains( pair.Key ) )
			{
				pair.Value.Collider.Enabled = false;
			}
		}

		var ordered = new List<Vector3Int>( _collisionDesiredChunks );
		ordered.Sort( (left, right) => GetCollisionPriority( left, observers ).CompareTo( GetCollisionPriority( right, observers ) ) );
		foreach ( var coordinate in ordered )
		{
			if ( _chunkColliders.TryGetValue( coordinate, out var state ) )
			{
				if ( state.Built && !state.Dirty )
				{
					state.Collider.Enabled = state.TriangleCount > 0;
					continue;
				}
				if ( state.Dirty && System.Diagnostics.Stopwatch.GetElapsedTime( state.DirtyTimestamp ).TotalSeconds < CollisionEditSettleSeconds )
				{
					continue;
				}
			}

			QueueCollisionBuild( coordinate );
		}
	}

	private Vector3Int GetCollisionObserverChunk( Vector3 worldPosition )
	{
		var localVoxelPosition = GameObject.WorldTransform.PointToLocal( worldPosition ) / VoxelSize;
		return new Vector3Int(
			(int)System.MathF.Floor( localVoxelPosition.x / ChunkSize ),
			(int)System.MathF.Floor( localVoxelPosition.y / ChunkSize ),
			0
		);
	}

	private static long GetCollisionPriority( Vector3Int coordinate, List<Vector3Int> observers )
	{
		var best = long.MaxValue;
		foreach ( var observer in observers )
		{
			var x = (long)coordinate.x - observer.x;
			var y = (long)coordinate.y - observer.y;
			best = System.Math.Min( best, x * x + y * y );
		}
		return best == long.MaxValue ? GetChunkBuildPriority( coordinate ) : best;
	}

	private void MarkCollisionDirty( Vector3Int coordinate )
	{
		var state = GetOrCreateChunkCollider( coordinate );
		state.DesiredGeneration++;
		state.Dirty = true;
		state.PinnedByEdit = true;
		state.DirtyTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		_collisionDesiredChunks.Add( coordinate );
	}

	private void QueueCollisionBuild( Vector3Int coordinate )
	{
		var state = GetOrCreateChunkCollider( coordinate );
		if ( state.DesiredGeneration == 0 )
		{
			state.DesiredGeneration = 1;
		}
		if ( state.Task is not null )
		{
			return;
		}
		if ( _collisionQueuedChunks.Add( coordinate ) )
		{
			_collisionBuildQueue.Enqueue( coordinate );
			state.Queued = true;
			CountCall( ref _callCollisionBuildsQueued );
		}
	}

	private void PumpCollisionBuildQueue()
	{
		CountCall( ref _callCollisionQueuePumps );
		var mainThreadStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var uploads = 0;
		foreach ( var state in _chunkColliders.Values )
		{
			if ( uploads >= CollisionBuildsPerFrame )
			{
				break;
			}
			if ( uploads > 0 && System.Diagnostics.Stopwatch.GetElapsedTime( mainThreadStart ).TotalMilliseconds >= CpuMainThreadBudgetMilliseconds )
			{
				break;
			}
			if ( state.Task is null || !state.Task.IsCompleted )
			{
				continue;
			}

			var task = state.Task;
			state.Task = null;
			state.Queued = false;
			if ( task.IsFaulted )
			{
				Log.Error( $"Voxel CPU collision build failed for chunk {state.Coordinate}: {task.Exception?.GetBaseException().Message}" );
				continue;
			}
			if ( task.IsCanceled )
			{
				continue;
			}

			var result = task.Result;
			if ( result.Generation != state.DesiredGeneration )
			{
				continue;
			}
			if ( !_collisionDesiredChunks.Contains( state.Coordinate ) )
			{
				continue;
			}
			UploadChunkCollider( state, result );
			uploads++;
		}

		var inFlight = 0;
		foreach ( var state in _chunkColliders.Values )
		{
			inFlight += state.Task is not null ? 1 : 0;
		}

		while ( inFlight < CollisionBuildConcurrency && _collisionBuildQueue.TryDequeue( out var coordinate ) )
		{
			_collisionQueuedChunks.Remove( coordinate );
			if ( !_collisionDesiredChunks.Contains( coordinate ) || !_chunks.ContainsKey( coordinate ) )
			{
				continue;
			}

			if ( !_chunkColliders.TryGetValue( coordinate, out var state ) || state.Task is not null )
			{
				continue;
			}
			if ( state.Dirty &&
				System.Diagnostics.Stopwatch.GetElapsedTime( state.DirtyTimestamp ).TotalSeconds < CollisionEditSettleSeconds )
			{
				state.Queued = false;
				continue;
			}

			if ( !_chunks.TryGetValue( coordinate, out var chunk ) )
			{
				state.Queued = false;
				continue;
			}

			var generation = state.DesiredGeneration;
			var resolutionDivisor = CollisionResolutionDivisor;
			var voxelSize = VoxelSize;
			var chunkSize = chunk.Size;
			state.TaskGeneration = generation;
			state.Queued = false;
			CountCall( ref _callCollisionBuildsStarted );
			state.Task = GameTask.RunInThreadAsync( () =>
			{
				var snapshotWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
				System.TimeSpan snapshotWaitElapsed;
				System.TimeSpan snapshotElapsed;
				float[] distanceSnapshot;
				lock ( _sdfLock )
				{
					snapshotWaitElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( snapshotWaitStart );
					var snapshotStart = System.Diagnostics.Stopwatch.GetTimestamp();
					distanceSnapshot = VoxelCollisionMesher.CreateDistanceSnapshot( chunk, resolutionDivisor );
					CountCall( ref _callCollisionSnapshotSamplesCopied, distanceSnapshot.Length );
					snapshotElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( snapshotStart );
				}
				var meshStart = System.Diagnostics.Stopwatch.GetTimestamp();
				var mesh = VoxelCollisionMesher.Build( distanceSnapshot, chunkSize, voxelSize, resolutionDivisor );
				var resolution = (chunkSize + resolutionDivisor - 1) / resolutionDivisor;
				return new CollisionBuildResult( generation, mesh, resolution, snapshotWaitElapsed, snapshotElapsed, System.Diagnostics.Stopwatch.GetElapsedTime( meshStart ) );
			} );
			inFlight++;
		}
	}


	private void StartCpuChunkWorld()
	{
		CountCall( ref _callVisualWorldStarts );
		DisposeCpuVisualWorld();
		if ( Application.IsDedicatedServer )
		{
			Log.Info( "Voxel dedicated-server world active: authoritative CPU SDF/materials and proximity collision enabled; visual mesh upload disabled." );
			RefreshCollisionInterests();
			PumpCollisionBuildQueue();
			return;
		}

		_cpuBatchStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		_cpuBatchSummaryPending = true;
		_cpuBatchFrameMilliseconds.Clear();
		ResetCpuBatchMeasurements();
		var orderedChunks = new List<KeyValuePair<Vector3Int, VoxelChunk>>( _chunks );
		orderedChunks.Sort( (left, right) => GetChunkBuildPriority( left.Key ).CompareTo( GetChunkBuildPriority( right.Key ) ) );
		foreach ( var pair in orderedChunks )
		{
			var state = new CpuChunkRuntime( pair.Key ) { DesiredGeneration = 1 };
			_cpuChunkStates.Add( pair.Key, state );
			QueueCpuChunkBuild( state );
		}

		Log.Info(
			$"Voxel CPU Transvoxel regular-cell world scheduled: chunks={_cpuChunkStates.Count:N0}, workers={CpuChunkBuildConcurrency:N0}, " +
			$"meshUploadsPerFrame={CpuMeshUploadsPerFrame:N0}, topology=CPU, rendering=GPU-rasterized, sdf=authoritative-cpu."
		);
		PumpCpuChunkBuildQueue();
	}

	private void MarkCpuChunkDirty( CpuChunkRuntime state )
	{
		state.DesiredGeneration++;
		state.Failed = false;
		if ( !_cpuBatchSummaryPending )
		{
			_cpuBatchSummaryPending = true;
			_cpuBatchStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
			_cpuBatchFrameMilliseconds.Clear();
			ResetCpuBatchMeasurements();
		}
		QueueCpuChunkBuild( state );
	}

	private void QueueCpuChunkBuild( CpuChunkRuntime state )
	{
		if ( state.Task is not null || state.CompletedGeneration >= state.DesiredGeneration || !_cpuQueuedChunks.Add( state.Coordinate ) ) return;
		_cpuChunkBuildQueue.Enqueue( state.Coordinate );
		CountCall( ref _callVisualBuildsQueued );
	}

	private void PumpCpuChunkBuildQueue()
	{
		CountCall( ref _callVisualQueuePumps );
		var mainThreadStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var scheduled = 0;
		var inFlight = 0;
		foreach ( var state in _cpuChunkStates.Values ) inFlight += state.Task is not null ? 1 : 0;
		while ( inFlight < CpuChunkBuildConcurrency && _cpuChunkBuildQueue.TryDequeue( out var coordinate ) )
		{
			if ( scheduled > 0 && System.Diagnostics.Stopwatch.GetElapsedTime( mainThreadStart ).TotalMilliseconds >= CpuMainThreadBudgetMilliseconds )
			{
				_cpuChunkBuildQueue.Enqueue( coordinate );
				break;
			}
			_cpuQueuedChunks.Remove( coordinate );
			if ( !_cpuChunkStates.TryGetValue( coordinate, out var state ) || state.Task is not null || state.CompletedGeneration >= state.DesiredGeneration || !_chunks.TryGetValue( coordinate, out var chunk ) ) continue;
			var generation = state.DesiredGeneration;
			var chunkSize = ChunkSize;
			var voxelSize = VoxelSize;
			state.TaskGeneration = generation;
			CountCall( ref _callVisualBuildsStarted );
			state.Task = GameTask.RunInThreadAsync( () =>
			{
				var snapshotWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
				System.TimeSpan snapshotWaitElapsed;
				float[] halo;
				System.TimeSpan snapshotCopyElapsed;
				lock ( _sdfLock )
				{
					snapshotWaitElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( snapshotWaitStart );
					var snapshotCopyStart = System.Diagnostics.Stopwatch.GetTimestamp();
					halo = CreateSdfHalo( chunk );
					snapshotCopyElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( snapshotCopyStart );
				}
				var meshStart = System.Diagnostics.Stopwatch.GetTimestamp();
				var mesh = VoxelTransvoxelMesher.Build( halo, chunkSize, voxelSize );
				return new CpuBuildResult( generation, mesh, snapshotWaitElapsed, snapshotCopyElapsed, System.Diagnostics.Stopwatch.GetElapsedTime( meshStart ) );
			} );
			inFlight++;
			scheduled++;
		}
	}

	private void UpdateCpuChunkWorld()
	{
		if ( _cpuChunkStates.Count == 0 ) return;
		if ( _cpuBatchSummaryPending && _cpuBatchFrameMilliseconds.Count < _cpuBatchFrameMilliseconds.Capacity )
		{
			_cpuBatchFrameMilliseconds.Add( Sandbox.Diagnostics.PerformanceStats.FrameTime * 1000.0 );
		}
		var mainThreadStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var uploadsRemaining = CpuMeshUploadsPerFrame;
		foreach ( var state in _cpuChunkStates.Values )
		{
			if ( uploadsRemaining <= 0 ) break;
			if ( uploadsRemaining < CpuMeshUploadsPerFrame && System.Diagnostics.Stopwatch.GetElapsedTime( mainThreadStart ).TotalMilliseconds >= CpuMainThreadBudgetMilliseconds ) break;
			if ( state.Task is null || !state.Task.IsCompleted ) continue;
			var task = state.Task;
			state.Task = null;
			if ( task.IsFaulted )
			{
				state.Failed = true;
				state.CompletedGeneration = state.TaskGeneration;
				Log.Error( $"Voxel CPU Transvoxel chunk {state.Coordinate} failed: {task.Exception?.GetBaseException().Message}" );
				continue;
			}
			if ( task.IsCanceled )
			{
				QueueCpuChunkBuild( state );
				continue;
			}

			var result = task.Result;
			if ( result.Generation != state.DesiredGeneration )
			{
				QueueCpuChunkBuild( state );
				continue;
			}
			UploadCpuChunkMesh( state, result );
			uploadsRemaining--;
		}
		PumpCpuChunkBuildQueue();
		TryLogCpuBatchSummary();
	}

	private void UploadCpuChunkMesh( CpuChunkRuntime state, CpuBuildResult result )
	{
		CountCall( ref _callVisualBuildsCompleted );
		var uploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
		if ( result.Generation > 1 && LogGeneration )
		{
			var topology = VoxelMeshTopologyAnalyzer.Analyze( result.Mesh, VoxelSize, result.MeshingTime );
			if ( topology.InvalidTriangleCount > 0 || topology.DegenerateTriangleCount > 0 || topology.ReversedNormalTriangleCount > 0 || topology.NonManifoldEdgeCount > 0 || topology.MaximumEdgeLength > VoxelSize * 2.0f )
			{
				Log.Warning( $"Voxel CPU Transvoxel edited topology anomaly: chunk={state.Coordinate}, invalid={topology.InvalidTriangleCount:N0}, degenerate={topology.DegenerateTriangleCount:N0}, reversed={topology.ReversedNormalTriangleCount:N0}, nonManifold={topology.NonManifoldEdgeCount:N0}, maxEdge={topology.MaximumEdgeLength:F2}, vertices={topology.VertexCount:N0}, triangles={topology.TriangleCount:N0}." );
			}
		}
		if ( state.GameObject is null )
		{
			state.GameObject = new GameObject( true, $"Voxel CPU Visual {state.Coordinate}" );
			state.GameObject.Parent = GameObject;
			state.GameObject.Tags.Add( ChunkTag );
			var origin = GetChunkVoxelOrigin( state.Coordinate );
			state.GameObject.LocalPosition = new Vector3( origin.x, origin.y, origin.z ) * VoxelSize;
			state.Renderer = state.GameObject.AddComponent<ModelRenderer>();
		}

		if ( result.Mesh.Indices.Count == 0 )
		{
			state.Renderer.Enabled = false;
		}
		else
		{
			var material = TerrainMaterial ?? Material.FromShader( "shaders/voxel_grass.shader" );
			if ( state.Mesh is null )
			{
				state.Mesh = new Mesh( material );
				state.Mesh.CreateVertexBuffer<Vertex>( result.Mesh.Vertices.Count, result.Mesh.Vertices );
				state.Mesh.CreateIndexBuffer( result.Mesh.Indices.Count, result.Mesh.Indices );
				state.Mesh.SetVertexRange( 0, result.Mesh.Vertices.Count );
				state.Mesh.SetIndexRange( 0, result.Mesh.Indices.Count );
				state.Mesh.Bounds = BBox.FromPositionAndSize( Vector3.One * ChunkSize * VoxelSize * 0.5f, Vector3.One * ChunkSize * VoxelSize );
				state.Renderer.Model = new ModelBuilder().AddMesh( state.Mesh ).Create();
			}
			else
			{
				state.Mesh.SetVertexBufferSize( result.Mesh.Vertices.Count );
				state.Mesh.SetVertexBufferData<Vertex>( result.Mesh.Vertices );
				state.Mesh.SetIndexBufferSize( result.Mesh.Indices.Count );
				state.Mesh.SetIndexBufferData( result.Mesh.Indices );
				state.Mesh.SetVertexRange( 0, result.Mesh.Vertices.Count );
				state.Mesh.SetIndexRange( 0, result.Mesh.Indices.Count );
			}
			state.Renderer.Enabled = true;
		}

		state.CompletedGeneration = result.Generation;
		state.Failed = false;
		state.VertexCount = result.Mesh.Vertices.Count;
		state.TriangleCount = result.Mesh.Indices.Count / 3;
		state.SnapshotWaitTime = result.SnapshotWaitTime;
		state.SnapshotTime = result.SnapshotTime;
		state.MeshingTime = result.MeshingTime;
		state.UploadTime = System.Diagnostics.Stopwatch.GetElapsedTime( uploadStart );
		CountCall( ref _callVisualUploads );
		_cpuBatchCompletedBuilds++;
		_cpuBatchSnapshotWaitMilliseconds += result.SnapshotWaitTime.TotalMilliseconds;
		_cpuBatchSnapshotCopyMilliseconds += result.SnapshotTime.TotalMilliseconds;
		_cpuBatchWorkerMeshMilliseconds += result.MeshingTime.TotalMilliseconds;
		_cpuBatchUploadMilliseconds += state.UploadTime.TotalMilliseconds;
		_totalVisualBuildsCompleted++;
		_totalSnapshotWaitMilliseconds += result.SnapshotWaitTime.TotalMilliseconds;
		_totalSnapshotCopyMilliseconds += result.SnapshotTime.TotalMilliseconds;
		_totalWorkerMeshMilliseconds += result.MeshingTime.TotalMilliseconds;
		_totalMainThreadUploadMilliseconds += state.UploadTime.TotalMilliseconds;
		if ( LogGeneration )
		{
			Log.Info(
				$"Voxel CPU Transvoxel chunk {state.Coordinate}: vertices={state.VertexCount:N0}, triangles={state.TriangleCount:N0}, " +
				$"snapshotWait={state.SnapshotWaitTime.TotalMilliseconds:F2}ms, snapshotCopy={state.SnapshotTime.TotalMilliseconds:F2}ms, workerMesh={state.MeshingTime.TotalMilliseconds:F2}ms, mainUpload={state.UploadTime.TotalMilliseconds:F2}ms, objectReused=yes."
			);
		}
	}

	private void TryLogCpuBatchSummary()
	{
		if ( !_cpuBatchSummaryPending || _cpuChunkBuildQueue.Count != 0 ) return;
		var inFlight = 0;
		var pending = 0;
		var failed = 0;
		long vertices = 0;
		long triangles = 0;
		foreach ( var state in _cpuChunkStates.Values )
		{
			inFlight += state.Task is not null ? 1 : 0;
			pending += state.CompletedGeneration < state.DesiredGeneration ? 1 : 0;
			failed += state.Failed ? 1 : 0;
			vertices += state.VertexCount;
			triangles += state.TriangleCount;
		}
		if ( inFlight != 0 || pending != 0 ) return;
		var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime( _cpuBatchStartTimestamp );
		_cpuBatchFrameMilliseconds.Sort();
		var averageFrameMilliseconds = 0.0;
		foreach ( var frame in _cpuBatchFrameMilliseconds ) averageFrameMilliseconds += frame;
		averageFrameMilliseconds = _cpuBatchFrameMilliseconds.Count > 0 ? averageFrameMilliseconds / _cpuBatchFrameMilliseconds.Count : 0.0;
		var p95FrameMilliseconds = _cpuBatchFrameMilliseconds.Count > 0 ? _cpuBatchFrameMilliseconds[(int)System.Math.Clamp( System.Math.Ceiling( _cpuBatchFrameMilliseconds.Count * 0.95 ) - 1, 0, _cpuBatchFrameMilliseconds.Count - 1 )] : 0.0;
		var maximumFrameMilliseconds = _cpuBatchFrameMilliseconds.Count > 0 ? _cpuBatchFrameMilliseconds[^1] : 0.0;
		_lastVisualBatchElapsedMilliseconds = elapsed.TotalMilliseconds;
		_lastVisualBatchAverageFrameMilliseconds = averageFrameMilliseconds;
		_lastVisualBatchP95FrameMilliseconds = p95FrameMilliseconds;
		_lastVisualBatchMaximumFrameMilliseconds = maximumFrameMilliseconds;
		_lastVisualSnapshotWaitMilliseconds = _cpuBatchSnapshotWaitMilliseconds;
		_lastVisualSnapshotCopyMilliseconds = _cpuBatchSnapshotCopyMilliseconds;
		_lastVisualWorkerMeshMilliseconds = _cpuBatchWorkerMeshMilliseconds;
		_lastVisualUploadMilliseconds = _cpuBatchUploadMilliseconds;
		_totalVisualBatchElapsedMilliseconds += elapsed.TotalMilliseconds;
		Log.Info(
			$"Voxel CPU Transvoxel batch: result={(failed == 0 ? "PASS" : "FAIL")}, worldChunks={_cpuChunkStates.Count:N0}, builtChunks={_cpuBatchCompletedBuilds:N0}, failed={failed:N0}, workers={CpuChunkBuildConcurrency:N0}, " +
			$"vertices={vertices:N0}, triangles={triangles:N0}, snapshotWaitTotal={_cpuBatchSnapshotWaitMilliseconds:F2}ms, snapshotCopyTotal={_cpuBatchSnapshotCopyMilliseconds:F2}ms, workerMeshTotal={_cpuBatchWorkerMeshMilliseconds:F2}ms, " +
			$"mainUploadTotal={_cpuBatchUploadMilliseconds:F2}ms, batchElapsed={elapsed.TotalMilliseconds:F2}ms, " +
			$"frames={_cpuBatchFrameMilliseconds.Count:N0}, frameMs(avg/p95/max)={averageFrameMilliseconds:F2}/{p95FrameMilliseconds:F2}/{maximumFrameMilliseconds:F2}, topology=CPU, rendering=GPU-rasterized."
		);
		_cpuBatchSummaryPending = false;
	}

	private void ResetCpuBatchMeasurements()
	{
		_cpuBatchCompletedBuilds = 0;
		_cpuBatchSnapshotWaitMilliseconds = 0.0;
		_cpuBatchSnapshotCopyMilliseconds = 0.0;
		_cpuBatchWorkerMeshMilliseconds = 0.0;
		_cpuBatchUploadMilliseconds = 0.0;
	}

	private void DisposeCpuVisualWorld()
	{
		foreach ( var state in _cpuChunkStates.Values )
		{
			if ( state.Task?.IsFaulted == true ) _ = state.Task.Exception;
			state.GameObject?.Destroy();
		}
		_cpuChunkStates.Clear();
		_cpuChunkBuildQueue.Clear();
		_cpuQueuedChunks.Clear();
		_cpuBatchFrameMilliseconds.Clear();
		_cpuBatchSummaryPending = false;
	}


	private sealed class CpuChunkRuntime
	{
		public Vector3Int Coordinate { get; }
		public GameObject GameObject { get; set; }
		public ModelRenderer Renderer { get; set; }
		public Mesh Mesh { get; set; }
		public System.Threading.Tasks.Task<CpuBuildResult> Task { get; set; }
		public int DesiredGeneration { get; set; }
		public int TaskGeneration { get; set; }
		public int CompletedGeneration { get; set; }
		public bool Failed { get; set; }
		public int VertexCount { get; set; }
		public int TriangleCount { get; set; }
		public System.TimeSpan SnapshotWaitTime { get; set; }
		public System.TimeSpan SnapshotTime { get; set; }
		public System.TimeSpan MeshingTime { get; set; }
		public System.TimeSpan UploadTime { get; set; }

		public CpuChunkRuntime( Vector3Int coordinate )
		{
			Coordinate = coordinate;
		}
	}

	private readonly record struct CpuBuildResult( int Generation, VoxelMeshData Mesh, System.TimeSpan SnapshotWaitTime, System.TimeSpan SnapshotTime, System.TimeSpan MeshingTime );
	private readonly record struct CollisionBuildResult( int Generation, VoxelMeshData Mesh, int Resolution, System.TimeSpan SnapshotWaitTime, System.TimeSpan SnapshotTime, System.TimeSpan MeshingTime );
	private readonly record struct GeneratedChunkResult( VoxelChunk Chunk, ChunkTopologyReport Report, System.TimeSpan BuildTime );

	private sealed class WorldGenerationWorkerResult
	{
		public List<GeneratedChunkResult> Chunks { get; }

		public WorldGenerationWorkerResult( List<GeneratedChunkResult> chunks )
		{
			Chunks = chunks;
		}
	}

	private sealed class ChunkCollisionState
	{
		public Vector3Int Coordinate { get; }
		public GameObject GameObject { get; }
		public ModelCollider Collider { get; }
		public System.Threading.Tasks.Task<CollisionBuildResult> Task { get; set; }
		public int DesiredGeneration { get; set; }
		public int TaskGeneration { get; set; }
		public int CompletedGeneration { get; set; }
		public bool Built { get; set; }
		public bool Dirty { get; set; }
		public bool Queued { get; set; }
		public bool PinnedByEdit { get; set; }
		public long DirtyTimestamp { get; set; }
		public int VertexCount { get; set; }
		public int TriangleCount { get; set; }
		public System.TimeSpan SnapshotWaitTime { get; set; }
		public System.TimeSpan SnapshotTime { get; set; }
		public System.TimeSpan MeshingTime { get; set; }
		public System.TimeSpan ModelBuildTime { get; set; }

		public ChunkCollisionState( Vector3Int coordinate, GameObject gameObject, ModelCollider collider )
		{
			Coordinate = coordinate;
			GameObject = gameObject;
			Collider = collider;
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
