public sealed class VoxelManager : Component, Component.ExecuteInEditor
{
	public const string ChunkTag = "voxel_chunk";

	private const int MinimumChunkSize = 4;
	private const int MaximumChunkSize = 128;
	private const int MaximumChunkRadius = 128;
	private const int MaximumDetailedChunkLogs = 256;
	private const int MaximumConcurrentCpuChunkBuilds = 32;
	private const int MaximumCpuMeshUploadsPerFrame = 16;
	private const int MaximumCollisionChunkRadius = 16;
	private const int MaximumCollisionBuildsPerFrame = 16;
	private const int MaximumConcurrentCollisionBuilds = 8;
	private const int MaximumChunkTimingHistory = 65536;
	private const int MaximumBatchTimingHistory = 4096;
	private const int MaximumBackendCompareGpuBatchSize = 128;
	private static readonly int[] BackendCompareBatchSizes = { 1, 8, 32, 128, 256, 512, 1024 };

	private readonly Dictionary<Vector3Int, VoxelChunk> _chunks = new();
	private readonly Dictionary<Vector3Int, GameObject> _chunkGameObjects = new();
	private readonly HashSet<Vector3Int> _desiredChunkCoordinates = new();
	private readonly Queue<Vector3Int> _chunkStreamingGenerationQueue = new();
	private readonly HashSet<Vector3Int> _chunkStreamingQueuedCoordinates = new();
	private readonly Dictionary<Vector3Int, long> _chunkStreamingRequestTimestamps = new();
	private readonly object _sdfLock = new();
	private readonly Dictionary<Vector3Int, ChunkCollisionState> _chunkColliders = new();
	private readonly Queue<Vector3Int> _collisionBuildQueue = new();
	private readonly HashSet<Vector3Int> _collisionQueuedChunks = new();
	private readonly HashSet<Vector3Int> _collisionDesiredChunks = new();
	private readonly Dictionary<Vector3Int, CpuChunkRuntime> _cpuChunkStates = new();
	private readonly Queue<Vector3Int> _cpuChunkBuildQueue = new();
	private readonly HashSet<Vector3Int> _cpuQueuedChunks = new();
	private readonly List<HashSet<Vector3Int>> _coherentVisualEditBatches = new();
	private readonly HashSet<System.Guid> _protectedPlayerIds = new();
	private readonly List<double> _cpuBatchFrameMilliseconds = new( 4096 );
	private readonly List<System.Threading.Tasks.Task<WorldGenerationWorkerResult>> _worldGenerationTasks = new();
	private readonly List<double> _worldGenerationFrameMilliseconds = new( 512 );
	private readonly List<ChunkStreamTimingEvent> _chunkTimingHistory = new( MaximumChunkTimingHistory );
	private readonly List<BatchTimingEvent> _batchTimingHistory = new( MaximumBatchTimingHistory );
	private readonly Dictionary<Vector3Int, double> _initialSdfGenerationMilliseconds = new();
	private VoxelGpuTransvoxelProof _gpuTransvoxelProof;
	private VoxelGpuTransvoxelProofResult _lastGpuTransvoxelProofResult;
	private bool _hasGpuTransvoxelProofResult;
	private VoxelTransvoxelBackendCompareResult _lastTransvoxelBackendCompareResult;
	private bool _hasTransvoxelBackendCompareResult;
	private bool _awaitingTransvoxelBackendCompareResult;
	private VoxelTransvoxelBackendCompareBatchResult[] _backendCompareCurrentCpuBatchResults;
	private int _backendCompareRequestedBatchSizeIndex;
	private int _backendCompareLogicalBatchSize;
	private int _backendCompareLogicalBatchPassStartIndex;
	private int _backendCompareLogicalBatchPassesRemaining;
	private int _backendCompareLogicalBatchTotalPasses;
	private int _backendCompareLogicalBatchPassSize;
	private double _backendCompareCurrentGpuDensityMilliseconds;
	private double _backendCompareCurrentGpuSubmissionMilliseconds;
	private double _backendCompareCurrentGpuCompletionMilliseconds;
	private double _backendCompareCurrentGpuPublicationMilliseconds;
	private uint _backendCompareCurrentGpuVertexCount;
	private uint _backendCompareCurrentGpuIndexCount;
	private int _backendCompareCurrentGpuDispatchCount;
	private bool _backendCompareCurrentGpuPassed = true;
	private string _backendCompareCurrentGpuFailure;
	private System.Threading.Tasks.Task<VoxelTransvoxelBackendCompareBatchResult[]> _backendCompareCpuTask;
	private double _backendComparePassStartTimestamp;
	private readonly int[] _backendCompareBatchSizes = BackendCompareBatchSizes;
	private readonly System.Collections.Generic.List<VoxelTransvoxelBackendCompareBatchResult> _backendCompareLogicalBatchResults = new();
	private long _nextChunkTimingSequence;
	private long _nextBatchTimingSequence;
	private long _cpuBatchStartTimestamp;
	private long _cpuBatchStartChunkTimingSequence;
	private long _cpuBatchStartBatchTimingSequence;
	private bool _cpuBatchSummaryPending;
	private long _lastCollisionInterestTimestamp;
	private long _lastChunkStreamingInterestTimestamp;
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
	private int _cpuBatchCompletedStreamBuilds;
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
	private long _callPlayerSafetyActivations;
	private long _callPlayerSafetyUpdates;
	private long _callPlayersRepositioned;
	private long _callPlayerTraversalUpdates;
	private bool _benchmarkPlayerProtectionEnabled;
	private bool _playerSafetyActive;
	private bool _protectAllPlayers;
	private bool _worldRegenerationRequested;
	private WorldConfiguration _generationConfiguration;

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

	[Property, Group( "Collision" ), Range( 1, MaximumCollisionBuildsPerFrame )]
	public int CollisionBuildsPerFrame { get; set; } = 4;

	[Property, Group( "Collision" ), Range( 1, MaximumConcurrentCollisionBuilds )]
	public int CollisionBuildConcurrency { get; set; } = 2;

	[Property, Group( "Diagnostics" )]
	public bool LogGeneration { get; set; }

	[Property, Group( "Diagnostics" )]
	public bool CaptureCallCounts { get; set; }

	[Property, Group( "Diagnostics" )]
	public bool RequestGpuTransvoxelProof { get; set; }

	[Property, Group( "Diagnostics" ), Range( 0, MaximumDetailedChunkLogs )]
	public int DetailedChunkLogLimit { get; set; } = 64;

	public IReadOnlyDictionary<Vector3Int, VoxelChunk> Chunks => _chunks;
	public int LoadedChunkCount => _chunks.Count;
	public int DesiredChunkCount => _desiredChunkCoordinates.Count;
	public int ActiveChunkGameObjectCount => _chunkGameObjects.Count;
	public int ChunkDiameter => ChunkRadius * 2;
	public int ConfiguredChunkCount => checked( ChunkDiameter * ChunkDiameter );
	public bool IsWorldGenerationPending => _worldGenerationPending;
	public bool IsPlayerSafetyActive => _playerSafetyActive;
	internal bool IsGpuTransvoxelProofRunning => _gpuTransvoxelProof?.IsRunning == true;
	internal bool HasGpuTransvoxelProofResult => _hasGpuTransvoxelProofResult;
	internal VoxelGpuTransvoxelProofResult LastGpuTransvoxelProofResult => _lastGpuTransvoxelProofResult;
	internal bool HasTransvoxelBackendCompareResult => _hasTransvoxelBackendCompareResult;
	internal VoxelTransvoxelBackendCompareResult LastTransvoxelBackendCompareResult => _lastTransvoxelBackendCompareResult;
	public long LatestChunkTimingSequence => _nextChunkTimingSequence;
	public long LatestBatchTimingSequence => _nextBatchTimingSequence;
	public bool IsTerrainSettled => AreDesiredChunksLoaded() && _chunkStreamingGenerationQueue.Count == 0 &&
		(Application.IsDedicatedServer || (_cpuChunkStates.Count == DesiredChunkCount && ActiveChunkGameObjectCount == DesiredChunkCount)) &&
		GetPendingVisualBuildCount() == 0 && GetPendingCollisionBuildCount() == 0 &&
		!_worldGenerationPending && !_cpuBatchSummaryPending;
	public bool HasPartialVisualEditPublication
	{
		get
		{
			var hasPublishedChunk = false;
			var hasPendingChunk = false;
			foreach ( var batch in _coherentVisualEditBatches )
			{
				foreach ( var coordinate in batch )
				{
					if ( !_cpuChunkStates.TryGetValue( coordinate, out var state ) ) continue;
					hasPublishedChunk |= state.CompletedGeneration >= state.DesiredGeneration;
					hasPendingChunk |= state.CompletedGeneration < state.DesiredGeneration;
				}
				if ( hasPublishedChunk && hasPendingChunk ) return true;
				hasPublishedChunk = false;
				hasPendingChunk = false;
			}
			return false;
		}
	}
	public bool HasVisualCollisionMismatch
	{
		get
		{
			foreach ( var coordinate in _collisionDesiredChunks )
			{
				if ( !_cpuChunkStates.TryGetValue( coordinate, out var visualState ) ||
					visualState.PublishedMeshData is null || visualState.CompletedGeneration < visualState.DesiredGeneration )
				{
					continue;
				}

				if ( !_chunkColliders.TryGetValue( coordinate, out var collisionState ) || !collisionState.Built ||
					collisionState.CompletedGeneration != visualState.CompletedGeneration ||
					collisionState.VertexCount != visualState.VertexCount || collisionState.TriangleCount != visualState.TriangleCount )
				{
					return true;
				}
			}
			return false;
		}
	}

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
		CollisionBuildsPerFrame = System.Math.Clamp( CollisionBuildsPerFrame, 1, MaximumCollisionBuildsPerFrame );
		CollisionBuildConcurrency = System.Math.Clamp( CollisionBuildConcurrency, 1, MaximumConcurrentCollisionBuilds );
		DetailedChunkLogLimit = System.Math.Clamp( DetailedChunkLogLimit, 0, MaximumDetailedChunkLogs );
	}

	protected override void OnEnabled()
	{
		_worldRegenerationRequested = true;
	}

	protected override void OnUpdate()
	{
		CountCall( ref _callManagerUpdates );
		UpdateGpuTransvoxelProof();
		UpdateTransvoxelBackendCompare();
		if ( RequestGpuTransvoxelProof )
		{
			RequestGpuTransvoxelProof = false;
			RunGpuTransvoxelProof();
		}
		if ( CaptureWorldConfiguration() != _generationConfiguration )
		{
			_worldRegenerationRequested = true;
		}
		if ( _worldRegenerationRequested && !_worldGenerationPending )
		{
			GenerateWorld();
		}
		UpdateWorldGeneration();
		UpdatePlayerSafety();
		if ( _worldGenerationPending || _worldRegenerationRequested )
		{
			return;
		}
		UpdateChunkStreaming();
		UpdateCpuChunkWorld();
		UpdateCpuCollisionWorld();
		UpdatePlayerSafety();
	}

	protected override void OnFixedUpdate()
	{
		if ( _playerSafetyActive ) RepositionPlayersAtOrigin();
	}

	protected override void OnDisabled()
	{
		_playerSafetyActive = false;
		_protectAllPlayers = false;
		_protectedPlayerIds.Clear();
		ClearTransvoxelBackendCompare();
		DisposeGpuTransvoxelProof();
		DisposeCpuVisualWorld();
		ClearChunkColliders();
		ClearChunkGameObjects();
		ResetWorldGeneration();
	}

	protected override void OnDestroy()
	{
		ClearTransvoxelBackendCompare();
		DisposeGpuTransvoxelProof();
		DisposeCpuVisualWorld();
		ClearChunkColliders();
		ClearChunkGameObjects();
		ResetWorldGeneration();
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
		_generationConfiguration = CaptureWorldConfiguration();
		_worldRegenerationRequested = false;
		ActivatePlayerSafety();

		DisposeCpuVisualWorld();
		ClearChunkColliders();
		ClearChunkGameObjects();
		lock ( _sdfLock )
		{
			_chunks.Clear();
		}
		_initialSdfGenerationMilliseconds.Clear();
		_chunkStreamingGenerationQueue.Clear();
		_chunkStreamingQueuedCoordinates.Clear();
		_chunkStreamingRequestTimestamps.Clear();
		_lastChunkStreamingInterestTimestamp = 0;

		var observers = GetStreamingObserverChunks();
		PopulateDesiredChunkCoordinates( observers, _desiredChunkCoordinates );
		var coordinates = new List<Vector3Int>( _desiredChunkCoordinates );
		coordinates.Sort( (left, right) => GetStreamingPriority( left, observers ).CompareTo( GetStreamingPriority( right, observers ) ) );

		var workerCount = System.Math.Min( CpuChunkBuildConcurrency, coordinates.Count );
		var chunkSize = _generationConfiguration.ChunkSize;
		var sdfClampDistance = _generationConfiguration.SdfClampDistance;
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

		if ( _worldRegenerationRequested || CaptureWorldConfiguration() != _generationConfiguration )
		{
			_worldGenerationPending = false;
			_worldGenerationTasks.Clear();
			_worldRegenerationRequested = true;
			GenerateWorld();
			return;
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
				_initialSdfGenerationMilliseconds[result.Chunk.Coordinate] = result.BuildTime.TotalMilliseconds;
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

	[Button]
	public void RunGpuTransvoxelProof()
	{
		DisposeGpuTransvoxelProof();
		_hasGpuTransvoxelProofResult = false;
		_lastGpuTransvoxelProofResult = default;
		if ( Application.IsDedicatedServer )
		{
			Log.Error( "Voxel GPU Transvoxel proof requires a rendering client." );
			return;
		}
		if ( Scene.Camera is null )
		{
			Log.Error( "Voxel GPU Transvoxel proof requires an active scene camera." );
			return;
		}

		var coordinate = Vector3Int.Zero;
		VoxelChunk chunk;
		float[] halo;
		lock ( _sdfLock )
		{
			if ( !_chunks.TryGetValue( coordinate, out chunk ) )
			{
				Log.Error( "Voxel GPU Transvoxel proof requires the origin chunk to be loaded." );
				return;
			}
			halo = CreateSdfHalo( chunk );
		}

		var cpuReference = VoxelTransvoxelMesher.Build( halo, ChunkSize, VoxelSize );
		var sampleOrigin = GetChunkVoxelOrigin( coordinate );
		var drawOrigin = new Vector3( sampleOrigin.x, sampleOrigin.y, sampleOrigin.z ) * VoxelSize +
			Vector3.Up * ChunkSize * VoxelSize * 2.0f;
		try
		{
			_gpuTransvoxelProof = new VoxelGpuTransvoxelProof(
				Scene.SceneWorld,
				Scene.Camera,
				cpuReference,
				sampleOrigin,
				drawOrigin,
				ChunkSize,
				VoxelSize,
				SdfClampDistance
			);
			_gpuTransvoxelProof.Run();
			Log.Info(
				$"Voxel GPU regular-cell Transvoxel proof scheduled: chunk={coordinate}, cells={ChunkSize}^{3}, " +
				$"cpuReferenceVertices={cpuReference.Vertices.Count:N0}, cpuReferenceTriangles={cpuReference.Indices.Count / 3:N0}, " +
				"density=GPU-procedural, batches=1/8/32/128, geometry=shared-GPU-pool, validationReadback=opt-in-proof-only."
			);
		}
		catch ( System.Exception exception )
		{
			DisposeGpuTransvoxelProof();
			_lastGpuTransvoxelProofResult = new VoxelGpuTransvoxelProofResult( false, exception.Message, 0, 0, 0, 0, 0, 0.0, 0.0, 0.0 );
			_hasGpuTransvoxelProofResult = true;
			Log.Error( $"Voxel GPU regular-cell Transvoxel proof failed to start: {exception.Message}" );
		}
	}

	[Button]
	public void RunTransvoxelBackendCompare()
	{
		ClearTransvoxelBackendCompare();
		if ( Application.IsDedicatedServer )
		{
			Log.Error( "Voxel transvoxel backend compare requires a rendering client." );
			return;
		}
		if ( Scene.Camera is null )
		{
			Log.Error( "Voxel transvoxel backend compare requires an active scene camera." );
			return;
		}

		if ( _backendCompareBatchSizes.Length == 0 || _backendCompareBatchSizes.All( size => size <= 0 ) )
		{
			Log.Error( "Voxel transvoxel backend compare requires at least one positive batch size." );
			return;
		}

		_hasTransvoxelBackendCompareResult = false;
		_awaitingTransvoxelBackendCompareResult = true;
		_backendCompareRequestedBatchSizeIndex = 0;
		_backendCompareLogicalBatchResults.Clear();
		_backendCompareLogicalBatchSize = 0;
		_backendCompareCurrentCpuBatchResults = null;
		_backendCompareCpuTask = GameTask.RunInThreadAsync( ComputeTransvoxelBackendCompareCpuResults );
		StartBackendCompareLogicalBatch();
	}

	public void ClearTransvoxelBackendCompare()
	{
		DisposeTransvoxelBackendCompareGpuProof();
		_hasTransvoxelBackendCompareResult = false;
		_awaitingTransvoxelBackendCompareResult = false;
		_backendCompareRequestedBatchSizeIndex = 0;
		_backendCompareLogicalBatchResults.Clear();
		_backendCompareCurrentCpuBatchResults = null;
		_backendCompareCpuTask = null;
		_backendCompareLogicalBatchSize = 0;
		_backendCompareLogicalBatchPassStartIndex = 0;
		_backendCompareLogicalBatchPassesRemaining = 0;
		_backendCompareLogicalBatchTotalPasses = 0;
		_backendCompareLogicalBatchPassSize = 0;
		_backendCompareCurrentGpuDensityMilliseconds = 0.0;
		_backendCompareCurrentGpuSubmissionMilliseconds = 0.0;
		_backendCompareCurrentGpuCompletionMilliseconds = 0.0;
		_backendCompareCurrentGpuPublicationMilliseconds = 0.0;
		_backendCompareCurrentGpuVertexCount = 0;
		_backendCompareCurrentGpuIndexCount = 0;
		_backendCompareCurrentGpuDispatchCount = 0;
		_backendCompareCurrentGpuPassed = true;
		_backendCompareCurrentGpuFailure = null;
		_backendComparePassStartTimestamp = 0;
	}

	public VoxelTransvoxelBackendCompareResult? TakeTransvoxelBackendCompareResult()
	{
		if ( !_hasTransvoxelBackendCompareResult ) return null;
		_hasTransvoxelBackendCompareResult = false;
		return _lastTransvoxelBackendCompareResult;
	}

	public void ClearGpuTransvoxelProof()
	{
		DisposeGpuTransvoxelProof();
	}

	private void UpdateGpuTransvoxelProof()
	{
		if ( _gpuTransvoxelProof is null || !_gpuTransvoxelProof.TryTakeResult( out var result ) ) return;
		_lastGpuTransvoxelProofResult = result;
		_hasGpuTransvoxelProofResult = true;
		Log.Info(
			$"Voxel GPU regular-cell Transvoxel proof: result={(result.Passed ? "PASS" : "FAIL")}, " +
			$"vertices={result.VertexCount:N0}, indices={result.IndexCount:N0}, activeCells={result.ActiveCells:N0}, " +
			$"overflow={result.OverflowAttempts:N0}, gpuBuffers={FormatBytes( result.GpuBufferBytes )}, " +
			$"cpuSubmission={result.SubmissionMilliseconds:F3}ms, completion={result.CompletionMilliseconds:F3}ms, " +
			$"batch={result.BatchSize:N0}, surfaceBlocks={result.SurfaceBlockCount:N0}, dispatches={result.DispatchCount:N0}, " +
			$"gpuPublication={(result.GpuCountPublicationPassed ? "PASS" : "FAIL")} ({result.GpuCountPublicationMilliseconds:F3}ms), " +
			$"cpuPublication={result.CpuCountPublicationMilliseconds:F3}ms, diagnosticGeometryReadback={result.GeometryReadbackMilliseconds:F3}ms, validation={result.Failure}."
		);
	}

	private void DisposeGpuTransvoxelProof()
	{
		_gpuTransvoxelProof?.Dispose();
		_gpuTransvoxelProof = null;
	}

	private void UpdateTransvoxelBackendCompare()
	{
		if ( !_awaitingTransvoxelBackendCompareResult )
		{
			return;
		}

		if ( _backendCompareCpuTask is not null && _backendCompareCurrentCpuBatchResults is null )
		{
			if ( !_backendCompareCpuTask.IsCompleted ) return;
			try
			{
				_backendCompareCurrentCpuBatchResults = _backendCompareCpuTask.Result;
			}
			catch ( System.Exception exception )
			{
				_backendCompareCurrentCpuBatchResults = null;
				_backendCompareCpuTask = null;
				_lastTransvoxelBackendCompareResult = new VoxelTransvoxelBackendCompareResult(
					false, $"CPU comparison failed to start: {exception.Message}", System.Array.Empty<VoxelTransvoxelBackendCompareBatchResult>()
				);
				_hasTransvoxelBackendCompareResult = true;
				_awaitingTransvoxelBackendCompareResult = false;
				Log.Error( $"Voxel transvoxel backend compare failed to start CPU workload: {exception.Message}" );
				DisposeTransvoxelBackendCompareGpuProof();
				return;
			}
		}

		if ( _backendCompareLogicalBatchSize == 0 )
		{
			if ( _backendCompareRequestedBatchSizeIndex >= _backendCompareBatchSizes.Length )
			{
				CompleteTransvoxelBackendCompare();
				return;
			}
			StartBackendCompareLogicalBatch();
			return;
		}

		if ( _gpuTransvoxelProof is null )
		{
			FinishTransvoxelBackendCompareWithFailure( "GPU backend compare lost a running pass handle." );
			return;
		}

		if ( !_gpuTransvoxelProof.TryTakeBatchResults( out var batchResults, out var result ) ) return;
		UpdateBackendComparePassResult( result, batchResults );
		if ( !result.Passed )
		{
			FinishTransvoxelBackendCompareWithFailure( string.IsNullOrWhiteSpace( result.Failure ) ? "GPU backend compare pass failed." : result.Failure );
			return;
		}

		_backendCompareLogicalBatchPassStartIndex += _backendCompareLogicalBatchPassSize;
		_backendCompareLogicalBatchPassesRemaining--;
		if ( _backendCompareLogicalBatchPassesRemaining > 0 )
		{
			StartBackendComparePass();
			return;
		}

		FinalizeBackendCompareLogicalBatch();
	}

	private void StartBackendCompareLogicalBatch()
	{
		if ( _backendCompareRequestedBatchSizeIndex >= _backendCompareBatchSizes.Length )
		{
			CompleteTransvoxelBackendCompare();
			return;
		}

		_backendCompareLogicalBatchSize = _backendCompareBatchSizes[_backendCompareRequestedBatchSizeIndex];
		if ( _backendCompareLogicalBatchSize <= 0 )
		{
			FinalizeBackendCompareLogicalBatch();
			return;
		}

		_backendCompareLogicalBatchPassStartIndex = 0;
		_backendCompareLogicalBatchPassesRemaining = GetRequiredSliceCount( _backendCompareLogicalBatchSize );
		_backendCompareLogicalBatchTotalPasses = _backendCompareLogicalBatchPassesRemaining;
		_backendCompareCurrentGpuDensityMilliseconds = 0.0;
		_backendCompareCurrentGpuSubmissionMilliseconds = 0.0;
		_backendCompareCurrentGpuCompletionMilliseconds = 0.0;
		_backendCompareCurrentGpuPublicationMilliseconds = 0.0;
		_backendCompareCurrentGpuVertexCount = 0;
		_backendCompareCurrentGpuIndexCount = 0;
		_backendCompareCurrentGpuDispatchCount = 0;
		_backendCompareCurrentGpuPassed = true;
		_backendCompareCurrentGpuFailure = null;
		_backendComparePassStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		StartBackendComparePass();
	}

	private void StartBackendComparePass()
	{
		if ( _backendCompareLogicalBatchPassesRemaining <= 0 || _backendCompareLogicalBatchPassStartIndex >= _backendCompareLogicalBatchSize )
		{
			return;
		}
		_backendCompareLogicalBatchPassSize = System.Math.Min( MaximumBackendCompareGpuBatchSize, _backendCompareLogicalBatchSize - _backendCompareLogicalBatchPassStartIndex );
		if ( _backendCompareLogicalBatchPassSize <= 0 ) return;

		var coordinate = Vector3Int.Zero;
		VoxelChunk chunk;
		float[] halo;
		lock ( _sdfLock )
		{
			if ( !_chunks.TryGetValue( coordinate, out chunk ) )
			{
				Log.Error( "Voxel transvoxel backend compare requires the origin chunk to be loaded." );
				FinishTransvoxelBackendCompareWithFailure( "origin chunk missing for reference generation" );
				return;
			}
			halo = CreateSdfHalo( chunk );
		}

		var cpuReference = VoxelTransvoxelMesher.Build( halo, ChunkSize, VoxelSize );
		var sampleOrigin = GetChunkVoxelOrigin( coordinate );
		var drawOrigin = new Vector3( sampleOrigin.x, sampleOrigin.y, sampleOrigin.z ) * VoxelSize +
			Vector3.Up * ChunkSize * VoxelSize * 2.0f;
		var passStart = _backendCompareLogicalBatchPassStartIndex;
		try
		{
			DisposeTransvoxelBackendCompareGpuProof();
			_gpuTransvoxelProof = new VoxelGpuTransvoxelProof(
				Scene.SceneWorld,
				Scene.Camera,
				cpuReference,
				sampleOrigin,
				drawOrigin,
				ChunkSize,
				VoxelSize,
				SdfClampDistance,
				new[] { _backendCompareLogicalBatchPassSize },
				passStart
			);
			_gpuTransvoxelProof.Run();
		}
		catch ( System.Exception exception )
		{
			FinishTransvoxelBackendCompareWithFailure( $"GPU pass start failed: {exception.Message}" );
		}
	}

	private void UpdateBackendComparePassResult( VoxelGpuTransvoxelProofResult result, VoxelGpuTransvoxelBatchResult[] batchResults )
	{
		_backendCompareCurrentGpuDensityMilliseconds += result.DensityGenerationMilliseconds;
		_backendCompareCurrentGpuSubmissionMilliseconds += result.SubmissionMilliseconds;
		_backendCompareCurrentGpuCompletionMilliseconds += result.CompletionMilliseconds;
		_backendCompareCurrentGpuPublicationMilliseconds += result.CpuCountPublicationMilliseconds + result.GpuCountPublicationMilliseconds + result.GeometryReadbackMilliseconds;
		_backendCompareCurrentGpuVertexCount += result.VertexCount;
		_backendCompareCurrentGpuIndexCount += result.IndexCount;
		_backendCompareCurrentGpuDispatchCount += result.DispatchCount;
		_backendCompareCurrentGpuPassed &= result.Passed;
		if ( !result.Passed && string.IsNullOrWhiteSpace( _backendCompareCurrentGpuFailure ) )
		{
			_backendCompareCurrentGpuFailure = result.Failure;
		}
		if ( batchResults is null ) return;
		foreach ( var batchResult in batchResults )
		{
			_backendCompareCurrentGpuDensityMilliseconds += batchResult.DensityGenerationMilliseconds;
			_backendCompareCurrentGpuSubmissionMilliseconds += batchResult.SubmissionMilliseconds;
			_backendCompareCurrentGpuCompletionMilliseconds += batchResult.CompletionMilliseconds;
			_backendCompareCurrentGpuPublicationMilliseconds += batchResult.PublicationMilliseconds + batchResult.GeometryReadbackMilliseconds;
			_backendCompareCurrentGpuDispatchCount += batchResult.DispatchCount;
		}
	}

	private void FinalizeBackendCompareLogicalBatch()
	{
		var cpuBatch = _backendCompareCurrentCpuBatchResults is null ? null : FindCpuBatchResult( _backendCompareLogicalBatchSize );
		var gpuWallMilliseconds = ComputeBackendCompareGpuWallMilliseconds();
		var gpuSurfaceBlockCount = CountBackendCompareSurfaceBlocks( _backendCompareLogicalBatchSize );
		if ( cpuBatch is null )
		{
			_backendCompareLogicalBatchResults.Add( new VoxelTransvoxelBackendCompareBatchResult(
				_backendCompareLogicalBatchSize,
				0.0,
				0.0,
				0.0,
				0.0,
				0.0,
				0.0,
				false,
				"missing CPU batch metrics",
				0,
				0,
				0,
				_backendCompareCurrentGpuDensityMilliseconds,
				_backendCompareCurrentGpuSubmissionMilliseconds,
				_backendCompareCurrentGpuCompletionMilliseconds,
				_backendCompareCurrentGpuPublicationMilliseconds,
				gpuWallMilliseconds,
				ComputeBackendCompareGpuChunksPerSecond( gpuWallMilliseconds ),
				_backendCompareCurrentGpuPassed,
				_backendCompareCurrentGpuFailure ?? string.Empty,
				_backendCompareCurrentGpuVertexCount,
				_backendCompareCurrentGpuIndexCount,
				gpuSurfaceBlockCount,
				_backendCompareCurrentGpuDispatchCount
			) );
		}
		else
		{
			_backendCompareLogicalBatchResults.Add( new VoxelTransvoxelBackendCompareBatchResult(
				cpuBatch.BatchSize,
				cpuBatch.CpuDensityGenerationMilliseconds,
				cpuBatch.CpuSnapshotPreparationMilliseconds,
				cpuBatch.CpuMeshMilliseconds,
				cpuBatch.CpuPublicationMilliseconds,
				cpuBatch.CpuTotalWallMilliseconds,
				cpuBatch.CpuChunksPerSecond,
				cpuBatch.CpuPassed,
				cpuBatch.CpuFailure,
				cpuBatch.CpuVertexCount,
				cpuBatch.CpuIndexCount,
				cpuBatch.CpuSurfaceBlockCount,
				_backendCompareCurrentGpuDensityMilliseconds,
				_backendCompareCurrentGpuSubmissionMilliseconds,
				_backendCompareCurrentGpuCompletionMilliseconds,
				_backendCompareCurrentGpuPublicationMilliseconds,
				gpuWallMilliseconds,
				ComputeBackendCompareGpuChunksPerSecond( gpuWallMilliseconds ),
				_backendCompareCurrentGpuPassed,
				_backendCompareCurrentGpuFailure ?? string.Empty,
				_backendCompareCurrentGpuVertexCount,
				_backendCompareCurrentGpuIndexCount,
				gpuSurfaceBlockCount,
				_backendCompareCurrentGpuDispatchCount
			) );
		}

		_backendCompareRequestedBatchSizeIndex++;
		_backendCompareLogicalBatchSize = 0;
		_backendCompareLogicalBatchPassSize = 0;
		_backendCompareLogicalBatchPassStartIndex = 0;
		_backendCompareLogicalBatchPassesRemaining = 0;
		_backendCompareLogicalBatchTotalPasses = 0;
		_backendCompareCurrentGpuDensityMilliseconds = 0.0;
		_backendCompareCurrentGpuSubmissionMilliseconds = 0.0;
		_backendCompareCurrentGpuCompletionMilliseconds = 0.0;
		_backendCompareCurrentGpuPublicationMilliseconds = 0.0;
		_backendCompareCurrentGpuVertexCount = 0;
		_backendCompareCurrentGpuIndexCount = 0;
		_backendCompareCurrentGpuDispatchCount = 0;
		_backendCompareCurrentGpuPassed = true;
		_backendCompareCurrentGpuFailure = null;

		DisposeTransvoxelBackendCompareGpuProof();
		if ( _backendCompareRequestedBatchSizeIndex < _backendCompareBatchSizes.Length )
		{
			StartBackendCompareLogicalBatch();
		}
		else
		{
			CompleteTransvoxelBackendCompare();
		}
	}

	private void CompleteTransvoxelBackendCompare()
	{
		if ( _backendCompareCpuTask is not null && !_backendCompareCpuTask.IsCompleted )
		{
			return;
		}
		if ( _backendCompareCurrentCpuBatchResults is null )
		{
			var message = "CPU comparison missing results.";
			_lastTransvoxelBackendCompareResult = new VoxelTransvoxelBackendCompareResult( false, message, System.Array.Empty<VoxelTransvoxelBackendCompareBatchResult>() );
			_hasTransvoxelBackendCompareResult = true;
			_awaitingTransvoxelBackendCompareResult = false;
			DisposeTransvoxelBackendCompareGpuProof();
			Log.Error( $"Voxel transvoxel backend compare failed: {message}" );
			return;
		}

		_lastTransvoxelBackendCompareResult = new VoxelTransvoxelBackendCompareResult(
			backendComparePassed: _backendCompareLogicalBatchResults.TrueForAll( r => r.CpuPassed && r.GpuPassed ),
			failure: string.Empty,
			batchResults: _backendCompareLogicalBatchResults.ToArray()
		);
		_hasTransvoxelBackendCompareResult = true;
		_awaitingTransvoxelBackendCompareResult = false;
		DisposeTransvoxelBackendCompareGpuProof();
		Log.Info( $"Voxel transvoxel backend compare complete: result={(_lastTransvoxelBackendCompareResult.Passed ? "PASS" : "FAIL")}." );
	}

	private void FinishTransvoxelBackendCompareWithFailure( string failure )
	{
		_lastTransvoxelBackendCompareResult = new VoxelTransvoxelBackendCompareResult( false, failure, System.Array.Empty<VoxelTransvoxelBackendCompareBatchResult>() );
		_hasTransvoxelBackendCompareResult = true;
		_awaitingTransvoxelBackendCompareResult = false;
		DisposeTransvoxelBackendCompareGpuProof();
		Log.Error( $"Voxel transvoxel backend compare failed: {failure}" );
	}

	private int GetRequiredSliceCount( int batchSize ) => (batchSize + MaximumBackendCompareGpuBatchSize - 1) / MaximumBackendCompareGpuBatchSize;

	private double ComputeBackendCompareGpuWallMilliseconds() =>
		_backendComparePassStartTimestamp == 0 ? 0.0 : System.Diagnostics.Stopwatch.GetElapsedTime( (long)_backendComparePassStartTimestamp ).TotalMilliseconds;

	private double ComputeBackendCompareGpuChunksPerSecond( double wallMilliseconds ) => wallMilliseconds > 0.0 && _backendCompareLogicalBatchSize > 0
		? _backendCompareLogicalBatchSize * 1000.0 / wallMilliseconds
		: 0.0;

	private int CountBackendCompareSurfaceBlocks( int batchSize )
	{
		var count = 0;
		for ( var block = 0; block < batchSize; block++ )
		{
			if ( block == 0 || block % 4 is 0 or 3 ) count++;
		}
		return count;
	}

	private VoxelTransvoxelBackendCompareBatchResult FindCpuBatchResult( int batchSize )
	{
		if ( _backendCompareCurrentCpuBatchResults is null ) return default;
		foreach ( var result in _backendCompareCurrentCpuBatchResults )
		{
			if ( result.BatchSize == batchSize ) return result;
		}
		return default;
	}

	private void DisposeTransvoxelBackendCompareGpuProof()
	{
		_gpuTransvoxelProof?.Dispose();
		_gpuTransvoxelProof = null;
	}
private VoxelTransvoxelBackendCompareBatchResult[] ComputeTransvoxelBackendCompareCpuResults()
	{
		var results = new VoxelTransvoxelBackendCompareBatchResult[_backendCompareBatchSizes.Length];
		for ( var index = 0; index < _backendCompareBatchSizes.Length; index++ )
		{
			var batchSize = _backendCompareBatchSizes[index];
			results[index] = ComputeTransvoxelBackendCompareCpuBatch( batchSize );
		}
		return results;
	}

	private VoxelTransvoxelBackendCompareBatchResult ComputeTransvoxelBackendCompareCpuBatch( int batchSize )
	{
		var densityStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var densityGenerationMilliseconds = 0.0;
		var snapshotPreparationMilliseconds = 0.0;
		var meshMilliseconds = 0.0;
		var publicationMilliseconds = 0.0;
		long vertexCount = 0;
		long indexCount = 0;
		var passed = true;
		string failure = string.Empty;
		var sampleOrigins = GetBackendCompareBlockSamples( batchSize );
		for ( var blockIndex = 0; blockIndex < batchSize; blockIndex++ )
		{
			var sampleOrigin = sampleOrigins[blockIndex];
			var densityCompletion = System.Diagnostics.Stopwatch.GetTimestamp();
			var chunk = new VoxelChunk( new Vector3Int( 0, 0, 0 ), ChunkSize, SdfClampDistance );
			FillChunkFromSdfFunction( chunk, sampleOrigin, VoxelSize, SdfClampDistance );
			densityGenerationMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime( densityCompletion ).TotalMilliseconds;
			var snapshotStart = System.Diagnostics.Stopwatch.GetTimestamp();
			var halo = CreateBackendCompareSdfHalo( chunk, sampleOrigin );
			snapshotPreparationMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime( snapshotStart ).TotalMilliseconds;
			var meshStart = System.Diagnostics.Stopwatch.GetTimestamp();
			var mesh = VoxelTransvoxelMesher.Build( halo, ChunkSize, VoxelSize );
			meshMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime( meshStart ).TotalMilliseconds;
			var publishStart = System.Diagnostics.Stopwatch.GetTimestamp();
			vertexCount += mesh.Vertices.Count;
			indexCount += mesh.Indices.Count;
			publicationMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime( publishStart ).TotalMilliseconds;
		}
		var totalMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( densityStart ).TotalMilliseconds;
		return new VoxelTransvoxelBackendCompareBatchResult(
			batchSize,
			densityGenerationMilliseconds,
			snapshotPreparationMilliseconds,
			meshMilliseconds,
			publicationMilliseconds,
			totalMilliseconds,
			batchSize > 0 && totalMilliseconds > 0.0 ? (batchSize * 1000.0 / totalMilliseconds) : 0.0,
			true,
			string.Empty,
			(uint)vertexCount,
			(uint)indexCount,
			(uint)CountBackendCompareSurfaceBlocks( batchSize ),
			0.0,
			0.0,
			0.0,
			0.0,
			0.0,
			true,
			string.Empty,
			0,
			0,
			0,
			0
		);
	}

	private Vector3[] GetBackendCompareBlockSamples( int batchSize )
	{
		var samples = new Vector3[batchSize];
		for ( var block = 0; block < batchSize; block++ )
		{
			var absoluteBlock = block;
			var gridX = absoluteBlock % 16;
			var gridY = absoluteBlock / 16;
			var topologyOffset = absoluteBlock > 0 && absoluteBlock % 4 == 1 ? 10_000.0f : absoluteBlock > 0 && absoluteBlock % 4 == 2 ? -10_000.0f : 0.0f;
			var sampleOrigin = new Vector3(
				gridX * ChunkSize * VoxelSize,
				gridY * ChunkSize * VoxelSize,
				-ChunkSize * VoxelSize + topologyOffset
			);
			samples[block] = sampleOrigin;
		}
		return samples;
	}

	private void FillChunkFromSdfFunction( VoxelChunk chunk, Vector3 sampleOrigin, float voxelSize, float sdfClampDistance )
	{
		var sampleSize = chunk.SampleSize;
		for ( var z = 0; z < sampleSize; z++ )
		{
			for ( var y = 0; y < sampleSize; y++ )
			{
				for ( var x = 0; x < sampleSize; x++ )
				{
					var worldZ = sampleOrigin.z + z * voxelSize;
					chunk.SetVoxelByIndex( x + sampleSize * (y + sampleSize * z ), new Voxel( System.Math.Clamp( worldZ, -sdfClampDistance, sdfClampDistance ) ) );
				}
			}
		}
	}

	private float[] CreateBackendCompareSdfHalo( VoxelChunk chunk, Vector3 sampleOrigin )
	{
		var sampleSize = chunk.Size + 3;
		var halo = new float[sampleSize * sampleSize * sampleSize];
		for ( var z = -1; z <= chunk.Size + 1; z++ )
		{
			for ( var y = -1; y <= chunk.Size + 1; y++ )
			{
				for ( var x = -1; x <= chunk.Size + 1; x++ )
				{
					var rowStart = sampleSize * ((y + 1) + sampleSize * (z + 1));
					var localWorldZ = sampleOrigin.z + z;
					halo[rowStart + x + 1] = System.Math.Clamp( localWorldZ, -SdfClampDistance, SdfClampDistance );
				}
			}
		}
		return halo;
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

	private bool AreDesiredChunksLoaded()
	{
		lock ( _sdfLock )
		{
			foreach ( var coordinate in _desiredChunkCoordinates )
			{
				if ( !_chunks.ContainsKey( coordinate ) ) return false;
			}
		}
		return true;
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
			_totalMainThreadUploadMilliseconds,
			_playerSafetyActive
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
			System.Threading.Interlocked.Read( ref _callCollisionUploads ),
			System.Threading.Interlocked.Read( ref _callPlayerSafetyActivations ),
			System.Threading.Interlocked.Read( ref _callPlayerSafetyUpdates ),
			System.Threading.Interlocked.Read( ref _callPlayersRepositioned ),
			System.Threading.Interlocked.Read( ref _callPlayerTraversalUpdates )
		);
	}

	public VoxelChunkStreamingDiagnostics CaptureChunkStreamingDiagnostics( long afterChunkSequence, long afterBatchSequence )
	{
		var chunkEvents = new List<ChunkStreamTimingEvent>();
		foreach ( var timing in _chunkTimingHistory )
		{
			if ( timing.Sequence > afterChunkSequence ) chunkEvents.Add( timing );
		}

		var batchMilliseconds = new List<double>();
		foreach ( var timing in _batchTimingHistory )
		{
			if ( timing.Sequence > afterBatchSequence ) batchMilliseconds.Add( timing.ElapsedMilliseconds );
		}

		var sdfGeneration = new List<double>( chunkEvents.Count );
		var meshQueue = new List<double>( chunkEvents.Count );
		var sdfSnapshot = new List<double>( chunkEvents.Count );
		var workerMesh = new List<double>( chunkEvents.Count );
		var publicationWait = new List<double>( chunkEvents.Count );
		var upload = new List<double>( chunkEvents.Count );
		var requestToRender = new List<double>( chunkEvents.Count );
		var fresh = 0;
		foreach ( var timing in chunkEvents )
		{
			if ( timing.SdfGenerationMilliseconds > 0.0 )
			{
				fresh++;
				sdfGeneration.Add( timing.SdfGenerationMilliseconds );
			}
			meshQueue.Add( timing.MeshQueueMilliseconds );
			sdfSnapshot.Add( timing.SdfSnapshotMilliseconds );
			workerMesh.Add( timing.WorkerMeshMilliseconds );
			publicationWait.Add( timing.PublicationWaitMilliseconds );
			upload.Add( timing.UploadMilliseconds );
			requestToRender.Add( timing.RequestToRenderMilliseconds );
		}

		return new VoxelChunkStreamingDiagnostics(
			chunkEvents.Count,
			fresh,
			chunkEvents.Count - fresh,
			batchMilliseconds.Count,
			SummarizeTiming( sdfGeneration ),
			SummarizeTiming( meshQueue ),
			SummarizeTiming( sdfSnapshot ),
			SummarizeTiming( workerMesh ),
			SummarizeTiming( publicationWait ),
			SummarizeTiming( upload ),
			SummarizeTiming( requestToRender ),
			SummarizeTiming( batchMilliseconds )
		);
	}

	private static VoxelTimingDistribution SummarizeTiming( List<double> values )
	{
		if ( values.Count == 0 ) return default;
		values.Sort();
		double total = 0.0;
		foreach ( var value in values ) total += value;
		var p95Index = (int)System.Math.Clamp( System.Math.Ceiling( values.Count * 0.95 ) - 1, 0, values.Count - 1 );
		return new VoxelTimingDistribution( values.Count, total / values.Count, values[p95Index], values[^1] );
	}

	internal void RecordPlayerTraversalUpdate()
	{
		CountCall( ref _callPlayerTraversalUpdates );
	}

	private void CountCall( ref long counter, long amount = 1 )
	{
		if ( !CaptureCallCounts || amount <= 0 ) return;
		System.Threading.Interlocked.Add( ref counter, amount );
	}

	private void ActivatePlayerSafety()
	{
		if ( !_benchmarkPlayerProtectionEnabled )
		{
			_playerSafetyActive = false;
			_protectAllPlayers = false;
			_protectedPlayerIds.Clear();
			return;
		}

		if ( !_playerSafetyActive ) CountCall( ref _callPlayerSafetyActivations );
		_playerSafetyActive = true;
		_protectAllPlayers = true;
		_protectedPlayerIds.Clear();
		RepositionPlayersAtOrigin();
	}

	internal void SetBenchmarkPlayerProtection( bool active )
	{
		_benchmarkPlayerProtectionEnabled = active;
		if ( active )
		{
			ActivatePlayerSafety();
			return;
		}

		_playerSafetyActive = false;
		_protectAllPlayers = false;
		_protectedPlayerIds.Clear();
	}

	private void ActivatePlayerSafetyForEdit( List<Vector3Int> changedChunks )
	{
		if ( !_benchmarkPlayerProtectionEnabled || changedChunks.Count == 0 ) return;
		foreach ( var controller in Scene.GetAllComponents<PlayerController>() )
		{
			if ( !CanControlPlayer( controller ) ) continue;
			var playerChunk = GetCollisionObserverChunk( controller.WorldPosition );
			foreach ( var changedChunk in changedChunks )
			{
				if ( System.Math.Abs( changedChunk.x - playerChunk.x ) > 1 || System.Math.Abs( changedChunk.y - playerChunk.y ) > 1 ) continue;
				_protectedPlayerIds.Add( controller.GameObject.Id );
				break;
			}
		}

		if ( _protectedPlayerIds.Count == 0 && !_protectAllPlayers ) return;
		if ( !_playerSafetyActive ) CountCall( ref _callPlayerSafetyActivations );
		_playerSafetyActive = true;
		RepositionPlayersAtOrigin();
	}

	private void UpdatePlayerSafety()
	{
		if ( !_playerSafetyActive ) return;
		CountCall( ref _callPlayerSafetyUpdates );
		if ( !_benchmarkPlayerProtectionEnabled || IsTerrainSettled )
		{
			_playerSafetyActive = false;
			_protectAllPlayers = false;
			_protectedPlayerIds.Clear();
			return;
		}

		RepositionPlayersAtOrigin();
	}

	private void RepositionPlayersAtOrigin()
	{
		var repositioned = 0;
		foreach ( var controller in Scene.GetAllComponents<PlayerController>() )
		{
			if ( !CanControlPlayer( controller ) ) continue;
			if ( !_protectAllPlayers && !_protectedPlayerIds.Contains( controller.GameObject.Id ) ) continue;

			controller.WorldPosition = Vector3.Zero;
			if ( controller.Body is not null )
			{
				controller.Body.Velocity = Vector3.Zero;
				controller.Body.AngularVelocity = Vector3.Zero;
			}
			repositioned++;
		}
		CountCall( ref _callPlayersRepositioned, repositioned );
	}

	private static bool CanControlPlayer( PlayerController controller )
	{
		return Application.IsDedicatedServer || !controller.GameObject.Network.Active || controller.GameObject.Network.IsOwner;
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
			Log.Info( $"Voxel mesh topology: visualPath=CPU-Transvoxel-regular/worker-pool, visualChunks={active:N0}/{_cpuChunkStates.Count:N0}, failed={failed:N0}, vertices={vertices:N0}, triangles={triangles:N0}, snapshotWaitTotal={snapshotWaitMilliseconds:F2}ms, snapshotCopyTotal={snapshotMilliseconds:F2}ms, workerMeshTotal={meshingMilliseconds:F2}ms, mainUploadTotal={uploadMilliseconds:F2}ms; collisionPath=CPU-Transvoxel-regular/exact-visual-mesh." );
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
		MergeCoherentVisualEditBatch( changedChunks );
		ActivatePlayerSafetyForEdit( changedChunks );

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

		var chunkObject = GetOrCreateChunkGameObject( coordinate );
		var collider = chunkObject.Components.Get<ModelCollider>() ?? chunkObject.AddComponent<ModelCollider>();
		collider.Static = true;
		collider.Enabled = false;
		var state = new ChunkCollisionState( coordinate, chunkObject, collider );
		_chunkColliders.Add( coordinate, state );
		return state;
	}

	private GameObject GetOrCreateChunkGameObject( Vector3Int coordinate )
	{
		if ( _chunkGameObjects.TryGetValue( coordinate, out var existing ) ) return existing;

		var chunkObject = new GameObject( true, $"Voxel Chunk {coordinate}" );
		chunkObject.Flags |= GameObjectFlags.NotSaved;
		chunkObject.Parent = GameObject;
		chunkObject.Tags.Add( ChunkTag );
		var chunkOrigin = GetChunkVoxelOrigin( coordinate );
		chunkObject.LocalPosition = new Vector3( chunkOrigin.x, chunkOrigin.y, chunkOrigin.z ) * VoxelSize;
		_chunkGameObjects.Add( coordinate, chunkObject );
		return chunkObject;
	}

	private void DestroyChunkGameObject( Vector3Int coordinate )
	{
		if ( !_chunkGameObjects.Remove( coordinate, out var chunkObject ) ) return;
		chunkObject.Destroy();
	}

	private void ClearChunkGameObjects()
	{
		foreach ( var chunkObject in _chunkGameObjects.Values ) chunkObject.Destroy();
		_chunkGameObjects.Clear();
	}

	private void UploadChunkCollider( ChunkCollisionState state, CollisionBuildResult result )
	{
		CountCall( ref _callCollisionBuildsCompleted );
		UploadChunkCollider( state, result.Mesh, result.Generation, result.SnapshotWaitTime, result.SnapshotTime, result.MeshingTime );
	}

	private void UploadChunkCollider( ChunkCollisionState state, VoxelMeshData meshData, int generation, System.TimeSpan snapshotWaitTime, System.TimeSpan snapshotTime, System.TimeSpan meshingTime )
	{
		var modelStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var collisionModel = meshData.Indices.Count > 0 ? BuildCollisionModel( meshData ) : null;
		state.Collider.Enabled = false;
		state.Collider.Model = collisionModel;
		state.Collider.Enabled = meshData.Indices.Count > 0;
		state.Built = true;
		state.Dirty = false;
		state.Queued = false;
		state.PinnedByEdit = false;
		state.CompletedGeneration = generation;
		state.VertexCount = meshData.Vertices.Count;
		state.TriangleCount = meshData.Indices.Count / 3;
		state.SnapshotWaitTime = snapshotWaitTime;
		state.SnapshotTime = snapshotTime;
		state.MeshingTime = meshingTime;
		state.ModelBuildTime = System.Diagnostics.Stopwatch.GetElapsedTime( modelStart );
		CountCall( ref _callCollisionUploads );
		if ( LogGeneration )
		{
			Log.Info(
				$"Voxel CPU collision: chunk={state.Coordinate}, topology=exact-visual-mesh, " +
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
			state.Collider.Enabled = false;
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

	private void UpdateChunkStreaming()
	{
		if ( _lastChunkStreamingInterestTimestamp == 0 ||
			System.Diagnostics.Stopwatch.GetElapsedTime( _lastChunkStreamingInterestTimestamp ).TotalSeconds >= 0.1 )
		{
			RefreshChunkStreamingInterests();
		}

		PumpChunkStreamingGenerationQueue();
	}

	private void RefreshChunkStreamingInterests()
	{
		_lastChunkStreamingInterestTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		var observers = GetStreamingObserverChunks();
		var desired = new HashSet<Vector3Int>();
		PopulateDesiredChunkCoordinates( observers, desired );
		if ( _desiredChunkCoordinates.SetEquals( desired ) ) return;
		var previousDesiredCount = _desiredChunkCoordinates.Count;
		var leavingVisualCount = 0;
		foreach ( var coordinate in _cpuChunkStates.Keys ) leavingVisualCount += desired.Contains( coordinate ) ? 0 : 1;
		var enteringCount = 0;
		foreach ( var coordinate in desired ) enteringCount += _desiredChunkCoordinates.Contains( coordinate ) ? 0 : 1;

		_desiredChunkCoordinates.Clear();
		_desiredChunkCoordinates.UnionWith( desired );

		var visualCoordinates = new List<Vector3Int>( _cpuChunkStates.Keys );
		foreach ( var coordinate in visualCoordinates )
		{
			if ( _desiredChunkCoordinates.Contains( coordinate ) ) continue;
			var state = _cpuChunkStates[coordinate];
			if ( state.Task?.IsFaulted == true ) _ = state.Task.Exception;
			_cpuChunkStates.Remove( coordinate );
		}

		var collisionCoordinates = new List<Vector3Int>( _chunkColliders.Keys );
		foreach ( var coordinate in collisionCoordinates )
		{
			if ( _desiredChunkCoordinates.Contains( coordinate ) ) continue;
			var state = _chunkColliders[coordinate];
			if ( state.Task?.IsFaulted == true ) _ = state.Task.Exception;
			_chunkColliders.Remove( coordinate );
			_collisionDesiredChunks.Remove( coordinate );
		}

		var chunkObjectCoordinates = new List<Vector3Int>( _chunkGameObjects.Keys );
		foreach ( var coordinate in chunkObjectCoordinates )
		{
			if ( !_desiredChunkCoordinates.Contains( coordinate ) ) DestroyChunkGameObject( coordinate );
		}

		for ( var index = _coherentVisualEditBatches.Count - 1; index >= 0; index-- )
		{
			_coherentVisualEditBatches[index].IntersectWith( _desiredChunkCoordinates );
			if ( _coherentVisualEditBatches[index].Count == 0 ) _coherentVisualEditBatches.RemoveAt( index );
		}

		_cpuChunkBuildQueue.Clear();
		_cpuQueuedChunks.Clear();
		_chunkStreamingGenerationQueue.Clear();
		_chunkStreamingQueuedCoordinates.Clear();
		_chunkStreamingRequestTimestamps.Clear();
		var streamingRefreshTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		if ( enteringCount > 0 ) StartCpuBatchIfNeeded( streamingRefreshTimestamp );

		var ordered = new List<Vector3Int>( _desiredChunkCoordinates );
		ordered.Sort( (left, right) => GetStreamingPriority( left, observers ).CompareTo( GetStreamingPriority( right, observers ) ) );
		foreach ( var coordinate in ordered )
		{
			if ( _chunks.ContainsKey( coordinate ) )
			{
				EnsureCpuVisualState( coordinate, streamingRefreshTimestamp );
				continue;
			}
			_chunkStreamingGenerationQueue.Enqueue( coordinate );
			_chunkStreamingQueuedCoordinates.Add( coordinate );
			_chunkStreamingRequestTimestamps[coordinate] = streamingRefreshTimestamp;
		}

		foreach ( var state in _cpuChunkStates.Values ) QueueCpuChunkBuild( state );
		RefreshCollisionInterests();
		Log.Info(
			$"Voxel mesh streaming updated: observers={observers.Count:N0}, desired={desired.Count:N0}, previous={previousDesiredCount:N0}, " +
			$"entering={enteringCount:N0}, deconstructed={leavingVisualCount:N0}, cachedSdf={_chunks.Count:N0}."
		);
	}

	private List<Vector3Int> GetStreamingObserverChunks()
	{
		var observers = new List<Vector3Int>();
		foreach ( var controller in Scene.GetAllComponents<PlayerController>() )
		{
			var coordinate = GetCollisionObserverChunk( controller.WorldPosition );
			if ( !observers.Contains( coordinate ) ) observers.Add( coordinate );
		}

		if ( observers.Count == 0 && Scene.Camera is not null )
		{
			observers.Add( GetCollisionObserverChunk( Scene.Camera.WorldPosition ) );
		}
		if ( observers.Count == 0 ) observers.Add( Vector3Int.Zero );
		return observers;
	}

	private void PopulateDesiredChunkCoordinates( List<Vector3Int> observers, HashSet<Vector3Int> destination )
	{
		destination.Clear();
		foreach ( var observer in observers )
		{
			for ( var y = -ChunkRadius; y < ChunkRadius; y++ )
			for ( var x = -ChunkRadius; x < ChunkRadius; x++ )
			{
				destination.Add( new Vector3Int( observer.x + x, observer.y + y, 0 ) );
			}
		}
	}

	private static long GetStreamingPriority( Vector3Int coordinate, List<Vector3Int> observers )
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

	private void PumpChunkStreamingGenerationQueue()
	{
		var start = System.Diagnostics.Stopwatch.GetTimestamp();
		var generated = 0;
		while ( _chunkStreamingGenerationQueue.TryDequeue( out var coordinate ) )
		{
			_chunkStreamingQueuedCoordinates.Remove( coordinate );
			if ( !_desiredChunkCoordinates.Contains( coordinate ) || _chunks.ContainsKey( coordinate ) ) continue;

			var generationStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
			GenerateChunk( coordinate );
			var generationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( generationStartTimestamp ).TotalMilliseconds;
			var requestTimestamp = _chunkStreamingRequestTimestamps.Remove( coordinate, out var queuedTimestamp ) ? queuedTimestamp : generationStartTimestamp;
			EnsureCpuVisualState( coordinate, requestTimestamp, generationMilliseconds );
			generated++;
			if ( generated > 0 && System.Diagnostics.Stopwatch.GetElapsedTime( start ).TotalMilliseconds >= CpuMainThreadBudgetMilliseconds ) break;
		}
	}

	private void EnsureCpuVisualState( Vector3Int coordinate, long requestTimestamp = 0, double sdfGenerationMilliseconds = 0.0 )
	{
		if ( Application.IsDedicatedServer || _cpuChunkStates.ContainsKey( coordinate ) ) return;
		StartCpuBatchIfNeeded( requestTimestamp );
		var state = new CpuChunkRuntime( coordinate )
		{
			DesiredGeneration = 1,
			TrackStreamingLifecycle = true,
			StreamRequestTimestamp = requestTimestamp == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : requestTimestamp,
			SdfGenerationMilliseconds = sdfGenerationMilliseconds
		};
		_cpuChunkStates.Add( coordinate, state );
		QueueCpuChunkBuild( state );
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
				if ( !Application.IsDedicatedServer && TryUploadPublishedVisualCollider( state ) )
				{
					continue;
				}
				if ( state.Built && !state.Dirty )
				{
					state.Collider.Enabled = state.TriangleCount > 0;
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
		if ( !Application.IsDedicatedServer )
		{
			TryUploadPublishedVisualCollider( state );
			return;
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
			if ( !_chunks.TryGetValue( coordinate, out var chunk ) )
			{
				state.Queued = false;
				continue;
			}

			var generation = state.DesiredGeneration;
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
					distanceSnapshot = CreateSdfHalo( chunk );
					CountCall( ref _callCollisionSnapshotSamplesCopied, distanceSnapshot.Length );
					snapshotElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( snapshotStart );
				}
				var meshStart = System.Diagnostics.Stopwatch.GetTimestamp();
				var mesh = VoxelTransvoxelMesher.Build( distanceSnapshot, chunkSize, voxelSize );
				return new CollisionBuildResult( generation, mesh, snapshotWaitElapsed, snapshotElapsed, System.Diagnostics.Stopwatch.GetElapsedTime( meshStart ) );
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

		_cpuBatchStartTimestamp = _worldGenerationStartTimestamp == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : _worldGenerationStartTimestamp;
		_cpuBatchStartChunkTimingSequence = _nextChunkTimingSequence;
		_cpuBatchStartBatchTimingSequence = _nextBatchTimingSequence;
		_cpuBatchSummaryPending = true;
		_cpuBatchFrameMilliseconds.Clear();
		ResetCpuBatchMeasurements();
		var observers = GetStreamingObserverChunks();
		var orderedCoordinates = new List<Vector3Int>( _desiredChunkCoordinates );
		orderedCoordinates.Sort( (left, right) => GetStreamingPriority( left, observers ).CompareTo( GetStreamingPriority( right, observers ) ) );
		foreach ( var coordinate in orderedCoordinates )
		{
			if ( !_chunks.ContainsKey( coordinate ) ) continue;
			_initialSdfGenerationMilliseconds.Remove( coordinate, out var sdfGenerationMilliseconds );
			var state = new CpuChunkRuntime( coordinate )
			{
				DesiredGeneration = 1,
				TrackStreamingLifecycle = true,
				StreamRequestTimestamp = _worldGenerationStartTimestamp == 0 ? _cpuBatchStartTimestamp : _worldGenerationStartTimestamp,
				SdfGenerationMilliseconds = sdfGenerationMilliseconds
			};
			_cpuChunkStates.Add( coordinate, state );
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
		state.TrackStreamingLifecycle = false;
		state.ReadyResult = null;
		state.Failed = false;
		StartCpuBatchIfNeeded( System.Diagnostics.Stopwatch.GetTimestamp() );
		QueueCpuChunkBuild( state );
	}

	private void MergeCoherentVisualEditBatch( List<Vector3Int> changedChunks )
	{
		var mergedBatch = new HashSet<Vector3Int>();
		foreach ( var coordinate in changedChunks )
		{
			if ( _cpuChunkStates.ContainsKey( coordinate ) ) mergedBatch.Add( coordinate );
		}
		if ( mergedBatch.Count == 0 ) return;

		for ( var index = _coherentVisualEditBatches.Count - 1; index >= 0; index-- )
		{
			var existingBatch = _coherentVisualEditBatches[index];
			if ( !existingBatch.Overlaps( mergedBatch ) ) continue;
			mergedBatch.UnionWith( existingBatch );
			_coherentVisualEditBatches.RemoveAt( index );
		}
		_coherentVisualEditBatches.Add( mergedBatch );
	}

	private void QueueCpuChunkBuild( CpuChunkRuntime state )
	{
		if ( state.Task is not null || state.ReadyResult.HasValue || state.CompletedGeneration >= state.DesiredGeneration || !_cpuQueuedChunks.Add( state.Coordinate ) ) return;
		if ( state.TrackStreamingLifecycle && state.MeshQueuedTimestamp == 0 ) state.MeshQueuedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
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
			var meshStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
			var meshQueueMilliseconds = state.TrackStreamingLifecycle && state.MeshQueuedTimestamp != 0
				? System.Diagnostics.Stopwatch.GetElapsedTime( state.MeshQueuedTimestamp, meshStartedTimestamp ).TotalMilliseconds
				: 0.0;
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
				var meshingTime = System.Diagnostics.Stopwatch.GetElapsedTime( meshStart );
				return new CpuBuildResult( generation, mesh, snapshotWaitElapsed, snapshotCopyElapsed, meshingTime, meshQueueMilliseconds, System.Diagnostics.Stopwatch.GetTimestamp() );
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
		foreach ( var state in _cpuChunkStates.Values )
		{
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
			state.ReadyResult = result;
		}
		var coherentUploads = UploadCoherentVisualEditBatches( mainThreadStart );
		UploadReadyCpuChunkMeshes( mainThreadStart, System.Math.Max( 0, CpuMeshUploadsPerFrame - coherentUploads ) );
		PumpCpuChunkBuildQueue();
		TryLogCpuBatchSummary();
	}

	private int UploadCoherentVisualEditBatches( long mainThreadStart )
	{
		var uploads = 0;
		for ( var batchIndex = _coherentVisualEditBatches.Count - 1; batchIndex >= 0; batchIndex-- )
		{
			var batch = _coherentVisualEditBatches[batchIndex];
			var ready = true;
			foreach ( var coordinate in batch )
			{
				if ( _cpuChunkStates.TryGetValue( coordinate, out var state ) &&
					state.ReadyResult.HasValue && state.ReadyResult.Value.Generation == state.DesiredGeneration ) continue;
				ready = false;
				break;
			}
			if ( !ready ) continue;
			if ( uploads > 0 && (uploads + batch.Count > CpuMeshUploadsPerFrame ||
				System.Diagnostics.Stopwatch.GetElapsedTime( mainThreadStart ).TotalMilliseconds >= CpuMainThreadBudgetMilliseconds) ) continue;

			foreach ( var coordinate in batch )
			{
				var state = _cpuChunkStates[coordinate];
				UploadCpuChunkMesh( state, state.ReadyResult.Value );
				state.ReadyResult = null;
				uploads++;
			}
			_coherentVisualEditBatches.RemoveAt( batchIndex );
		}
		return uploads;
	}

	private void UploadReadyCpuChunkMeshes( long mainThreadStart, int uploadsRemaining )
	{
		foreach ( var state in _cpuChunkStates.Values )
		{
			if ( uploadsRemaining <= 0 ) break;
			if ( uploadsRemaining < CpuMeshUploadsPerFrame && System.Diagnostics.Stopwatch.GetElapsedTime( mainThreadStart ).TotalMilliseconds >= CpuMainThreadBudgetMilliseconds ) break;
			if ( IsInCoherentVisualEditBatch( state.Coordinate ) || !state.ReadyResult.HasValue ) continue;
			UploadCpuChunkMesh( state, state.ReadyResult.Value );
			state.ReadyResult = null;
			uploadsRemaining--;
		}
	}

	private bool IsInCoherentVisualEditBatch( Vector3Int coordinate )
	{
		foreach ( var batch in _coherentVisualEditBatches )
		{
			if ( batch.Contains( coordinate ) ) return true;
		}
		return false;
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
		state.PublishedMeshData = result.Mesh;
		if ( _chunkColliders.TryGetValue( state.Coordinate, out var collisionState ) &&
			(_collisionDesiredChunks.Contains( state.Coordinate ) || collisionState.PinnedByEdit) )
		{
			UploadChunkCollider( collisionState, result.Mesh, result.Generation, result.SnapshotWaitTime, result.SnapshotTime, result.MeshingTime );
		}

		if ( state.GameObject is null )
		{
			state.GameObject = GetOrCreateChunkGameObject( state.Coordinate );
			state.Renderer = state.GameObject.Components.Get<ModelRenderer>() ?? state.GameObject.AddComponent<ModelRenderer>();
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
		if ( state.TrackStreamingLifecycle )
		{
			var publicationWaitMilliseconds = result.WorkerCompletedTimestamp == 0
				? 0.0
				: System.Diagnostics.Stopwatch.GetElapsedTime( result.WorkerCompletedTimestamp, uploadStart ).TotalMilliseconds;
			var requestToRenderMilliseconds = state.StreamRequestTimestamp == 0
				? 0.0
				: System.Diagnostics.Stopwatch.GetElapsedTime( state.StreamRequestTimestamp ).TotalMilliseconds;
			AppendChunkTiming( new ChunkStreamTimingEvent(
				++_nextChunkTimingSequence,
				state.SdfGenerationMilliseconds,
				result.MeshQueueMilliseconds,
				result.SnapshotWaitTime.TotalMilliseconds + result.SnapshotTime.TotalMilliseconds,
				result.MeshingTime.TotalMilliseconds,
				publicationWaitMilliseconds,
				state.UploadTime.TotalMilliseconds,
				requestToRenderMilliseconds
			) );
			state.TrackStreamingLifecycle = false;
			_cpuBatchCompletedStreamBuilds++;
		}
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

	private bool TryUploadPublishedVisualCollider( ChunkCollisionState collisionState )
	{
		if ( !_cpuChunkStates.TryGetValue( collisionState.Coordinate, out var visualState ) ||
			visualState.PublishedMeshData is null || visualState.CompletedGeneration < visualState.DesiredGeneration ||
			collisionState.CompletedGeneration >= visualState.CompletedGeneration )
		{
			return false;
		}

		collisionState.DesiredGeneration = visualState.CompletedGeneration;
		UploadChunkCollider( collisionState, visualState.PublishedMeshData, visualState.CompletedGeneration,
			visualState.SnapshotWaitTime, visualState.SnapshotTime, visualState.MeshingTime );
		return true;
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
		if ( _cpuBatchCompletedStreamBuilds > 0 )
		{
			AppendBatchTiming( new BatchTimingEvent( ++_nextBatchTimingSequence, elapsed.TotalMilliseconds ) );
		}
		var streaming = CaptureChunkStreamingDiagnostics( _cpuBatchStartChunkTimingSequence, _cpuBatchStartBatchTimingSequence );
		Log.Info(
			$"Voxel CPU Transvoxel batch: result={(failed == 0 ? "PASS" : "FAIL")}, worldChunks={_cpuChunkStates.Count:N0}, builtChunks={_cpuBatchCompletedBuilds:N0}, failed={failed:N0}, workers={CpuChunkBuildConcurrency:N0}, " +
			$"vertices={vertices:N0}, triangles={triangles:N0}, snapshotWaitTotal={_cpuBatchSnapshotWaitMilliseconds:F2}ms, snapshotCopyTotal={_cpuBatchSnapshotCopyMilliseconds:F2}ms, workerMeshTotal={_cpuBatchWorkerMeshMilliseconds:F2}ms, " +
			$"mainUploadTotal={_cpuBatchUploadMilliseconds:F2}ms, batchElapsed={elapsed.TotalMilliseconds:F2}ms, " +
			$"frames={_cpuBatchFrameMilliseconds.Count:N0}, frameMs(avg/p95/max)={averageFrameMilliseconds:F2}/{p95FrameMilliseconds:F2}/{maximumFrameMilliseconds:F2}, " +
			$"streamedChunks(fresh/cached)={streaming.FreshGeneratedChunks:N0}/{streaming.CachedChunks:N0}, sdfGenerationMs(avg/p95/max)={FormatTiming( streaming.SdfGeneration )}, " +
			$"meshQueueMs(avg/p95/max)={FormatTiming( streaming.MeshQueue )}, snapshotMs(avg/p95/max)={FormatTiming( streaming.SdfSnapshot )}, workerMeshMs(avg/p95/max)={FormatTiming( streaming.WorkerMesh )}, " +
			$"publicationWaitMs(avg/p95/max)={FormatTiming( streaming.PublicationWait )}, uploadMs(avg/p95/max)={FormatTiming( streaming.MainThreadUpload )}, " +
			$"requestToRenderMs(avg/p95/max)={FormatTiming( streaming.RequestToRender )}, batchMs(avg/p95/max)={FormatTiming( streaming.BatchCompletion )}, topology=CPU, rendering=GPU-rasterized."
		);
		_cpuBatchSummaryPending = false;
	}

	private void ResetCpuBatchMeasurements()
	{
		_cpuBatchCompletedBuilds = 0;
		_cpuBatchCompletedStreamBuilds = 0;
		_cpuBatchSnapshotWaitMilliseconds = 0.0;
		_cpuBatchSnapshotCopyMilliseconds = 0.0;
		_cpuBatchWorkerMeshMilliseconds = 0.0;
		_cpuBatchUploadMilliseconds = 0.0;
	}

	private void StartCpuBatchIfNeeded( long startTimestamp )
	{
		if ( _cpuBatchSummaryPending ) return;
		_cpuBatchSummaryPending = true;
		_cpuBatchStartTimestamp = startTimestamp == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : startTimestamp;
		_cpuBatchStartChunkTimingSequence = _nextChunkTimingSequence;
		_cpuBatchStartBatchTimingSequence = _nextBatchTimingSequence;
		_cpuBatchFrameMilliseconds.Clear();
		ResetCpuBatchMeasurements();
	}

	private static string FormatTiming( VoxelTimingDistribution timing ) => timing.Count == 0
		? "n/a"
		: $"{timing.AverageMilliseconds:F2}/{timing.P95Milliseconds:F2}/{timing.MaximumMilliseconds:F2}";

	private void AppendChunkTiming( ChunkStreamTimingEvent timing )
	{
		if ( _chunkTimingHistory.Count >= MaximumChunkTimingHistory )
		{
			_chunkTimingHistory.RemoveRange( 0, MaximumChunkTimingHistory / 4 );
		}
		_chunkTimingHistory.Add( timing );
	}

	private void AppendBatchTiming( BatchTimingEvent timing )
	{
		if ( _batchTimingHistory.Count >= MaximumBatchTimingHistory )
		{
			_batchTimingHistory.RemoveRange( 0, MaximumBatchTimingHistory / 4 );
		}
		_batchTimingHistory.Add( timing );
	}

	private void DisposeCpuVisualWorld()
	{
		foreach ( var state in _cpuChunkStates.Values )
		{
			if ( state.Task?.IsFaulted == true ) _ = state.Task.Exception;
		}
		_cpuChunkStates.Clear();
		_cpuChunkBuildQueue.Clear();
		_cpuQueuedChunks.Clear();
		_coherentVisualEditBatches.Clear();
		_cpuBatchFrameMilliseconds.Clear();
		_cpuBatchSummaryPending = false;
	}

	private WorldConfiguration CaptureWorldConfiguration()
	{
		return new WorldConfiguration( ChunkSize, ChunkRadius, VoxelSize, SdfClampDistance, TerrainMaterial );
	}

	private void ResetWorldGeneration()
	{
		_worldGenerationTasks.Clear();
		_worldGenerationFrameMilliseconds.Clear();
		_worldGenerationPending = false;
		_worldRegenerationRequested = false;
		_generationConfiguration = default;
	}


	private sealed class CpuChunkRuntime
	{
		public Vector3Int Coordinate { get; }
		public GameObject GameObject { get; set; }
		public ModelRenderer Renderer { get; set; }
		public Mesh Mesh { get; set; }
		public VoxelMeshData PublishedMeshData { get; set; }
		public System.Threading.Tasks.Task<CpuBuildResult> Task { get; set; }
		public CpuBuildResult? ReadyResult { get; set; }
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
		public bool TrackStreamingLifecycle { get; set; }
		public long StreamRequestTimestamp { get; set; }
		public long MeshQueuedTimestamp { get; set; }
		public double SdfGenerationMilliseconds { get; set; }

		public CpuChunkRuntime( Vector3Int coordinate )
		{
			Coordinate = coordinate;
		}
	}

	private readonly record struct CpuBuildResult( int Generation, VoxelMeshData Mesh, System.TimeSpan SnapshotWaitTime, System.TimeSpan SnapshotTime, System.TimeSpan MeshingTime, double MeshQueueMilliseconds, long WorkerCompletedTimestamp );
	private readonly record struct CollisionBuildResult( int Generation, VoxelMeshData Mesh, System.TimeSpan SnapshotWaitTime, System.TimeSpan SnapshotTime, System.TimeSpan MeshingTime );
	private readonly record struct ChunkStreamTimingEvent( long Sequence, double SdfGenerationMilliseconds, double MeshQueueMilliseconds, double SdfSnapshotMilliseconds, double WorkerMeshMilliseconds, double PublicationWaitMilliseconds, double UploadMilliseconds, double RequestToRenderMilliseconds );
	private readonly record struct BatchTimingEvent( long Sequence, double ElapsedMilliseconds );
	private readonly record struct GeneratedChunkResult( VoxelChunk Chunk, ChunkTopologyReport Report, System.TimeSpan BuildTime );
	private readonly record struct WorldConfiguration( int ChunkSize, int ChunkRadius, float VoxelSize, float SdfClampDistance, Material TerrainMaterial );

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
		if ( coordinate.z != 0 )
		{
			throw new System.ArgumentOutOfRangeException( nameof( coordinate ), $"Chunk coordinate {coordinate} is not on terrain layer Z=0." );
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
