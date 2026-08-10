public sealed class VoxelManager : Component, Component.ExecuteInEditor
{
	private const int GpuStreamingStagingResidentCapacity = VoxelGpuScratchArena.MaximumBatchSize * 2;
	public const string ChunkTag = "voxel_chunk";

	private const int MinimumChunkSize = 4;
	private const int MaximumChunkSize = 128;
	private const int MaximumChunkRadius = 128;
	private const int MaximumDetailedChunkLogs = 256;
	private const int MaximumConcurrentCpuChunkBuilds = 32;
	private const int MaximumCpuMeshUploadsPerFrame = 16;
	private const int MaximumCollisionChunkRadius = 16;
	private const int MaximumCollisionBuildsPerFrame = 1;
	private const int MaximumConcurrentCollisionBuilds = 8;
	private const int MaximumChunkTimingHistory = 65536;
	private const int MaximumBatchTimingHistory = 4096;
	private const int GpuPoolReferenceRadius = 32;
	private const int GpuPoolMaximumRadius = 64;
	private const int GpuPoolHeadroomPercent = 15;
	private const int DefaultGpuVertexPoolCapacity = 8388608;
	private const int DefaultGpuIndexPoolCapacity = 50331648;
	private const int MaximumGpuVertexPoolCapacity = 33554432;
	private const int MaximumGpuIndexPoolCapacity = 201326592;
	private const long GpuReferenceResidentCount = 5979;
	private const long GpuReferenceVertexCount = 8388556;
	private const long GpuReferenceIndexCount = 47657688;

	private readonly Dictionary<Vector3Int, VoxelChunk> _chunks = new();
	private readonly VoxelEditJournal _editJournal = new();
	private readonly Dictionary<Vector3Int, GameObject> _chunkGameObjects = new();
	private readonly HashSet<Vector3Int> _desiredChunkCoordinates = new();
	private readonly List<Vector3Int> _gpuStreamingObservers = new( 4 );
	private readonly List<Vector3Int> _gpuPreviousStreamingObservers = new( 4 );
	private readonly HashSet<Vector3Int> _gpuDesiredScratch = new();
	private readonly List<Vector3Int> _gpuOrderedScratch = new();
	private readonly List<VoxelGpuClipboxDebugBlock> _gpuClipboxDebugScratch = new( 2048 );
	private readonly List<VoxelGpuClipboxDebugTransition> _gpuClipboxTransitionDebugScratch = new( 2048 );
	private VoxelClipboxRuntimePlanner _editorClipboxDebugPlanner;
	private VoxelClipboxConfig _editorClipboxDebugConfiguration;
	private Vector3Int _editorClipboxDebugObserver;
	private readonly Queue<Vector3Int> _chunkStreamingGenerationQueue = new();
	private readonly HashSet<Vector3Int> _chunkStreamingQueuedCoordinates = new();
	private readonly Dictionary<Vector3Int, long> _chunkStreamingRequestTimestamps = new();
	private readonly object _sdfLock = new();
	private readonly Dictionary<Vector3Int, ChunkCollisionState> _chunkColliders = new();
	private readonly Queue<Vector3Int> _collisionGenerationQueue = new();
	private readonly HashSet<Vector3Int> _collisionGenerationQueuedChunks = new();
	private readonly Queue<Vector3Int> _collisionBuildQueue = new();
	private readonly HashSet<Vector3Int> _collisionQueuedChunks = new();
	private readonly HashSet<Vector3Int> _collisionDesiredChunks = new();
	private readonly Dictionary<Vector3Int, CpuChunkRuntime> _cpuChunkStates = new();
	private readonly Queue<Vector3Int> _cpuChunkBuildQueue = new();
	private readonly HashSet<Vector3Int> _cpuQueuedChunks = new();
	private readonly List<HashSet<Vector3Int>> _coherentVisualEditBatches = new();
	private readonly List<HashSet<Vector3Int>> _coherentCollisionEditBatches = new();
	private readonly HashSet<System.Guid> _protectedPlayerIds = new();
	private readonly List<double> _cpuBatchFrameMilliseconds = new( 4096 );
	private readonly List<System.Threading.Tasks.Task<WorldGenerationWorkerResult>> _worldGenerationTasks = new();
	private readonly List<double> _worldGenerationFrameMilliseconds = new( 512 );
	private readonly List<ChunkStreamTimingEvent> _chunkTimingHistory = new( MaximumChunkTimingHistory );
	private readonly List<BatchTimingEvent> _batchTimingHistory = new( MaximumBatchTimingHistory );
	private readonly Dictionary<Vector3Int, double> _initialSdfGenerationMilliseconds = new();
	private VoxelGpuTransvoxelProof _gpuTransvoxelProof;
	private VoxelGpuTransitionCaseProof _gpuTransitionCaseProof;
	private VoxelGpuTerrainBackend _gpuTerrainBackend;
	private VoxelGpuTransvoxelProofResult _lastGpuTransvoxelProofResult;
	private bool _hasGpuTransvoxelProofResult;
	private VoxelGpuTransitionCaseProofResult _lastGpuTransitionCaseProofResult;
	private bool _hasGpuTransitionCaseProofResult;
	private VoxelGpuClipboxSeamProofReport _lastGpuClipboxSeamProofResult;
	private bool _hasGpuClipboxSeamProofResult;
	private long _nextChunkTimingSequence;
	private long _nextBatchTimingSequence;
	private long _cpuBatchStartTimestamp;
	private long _cpuBatchStartChunkTimingSequence;
	private long _cpuBatchStartBatchTimingSequence;
	private bool _cpuBatchSummaryPending;
	private long _lastCollisionInterestTimestamp;
	private long _lastChunkStreamingInterestTimestamp;
	private long _lastGpuStreamingLogTimestamp;
	private bool _gpuStreamingObserversInitialized;
	private bool _gpuClipboxObserverInitialized;
	private Vector3Int _gpuClipboxObserverBaseBlock;
	private int _gpuDesiredBlockCount;
	private int _slowGpuStreamingLogCount;
	private int _idleStutterDiagnosticCount;
	private string _gpuTerrainLiveDiagnostics = "available=False; requested=0; residents=0; pendingCount=0; pendingEmit=0; readbacks=0; visibleDraws=0; poolUsed=0; requestToVisibleP95Ms=0.00; failure=";
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

	[Property, Group( "World" )]
	public bool GenerateInEditor { get; set; }

	[Property, Group( "World" ), Range( 1.0f, 128.0f )]
	public float VoxelSize { get; set; } = 16.0f;

	[Property, Group( "World" ), Range( 1.0f, MaximumChunkSize )]
	public float SdfClampDistance { get; set; } = 8.0f;

	[Property, Group( "Terrain" ), Range( 0.0001f, 0.1f )]
	public float SimplexFrequency { get; set; } = 0.01f;

	[Property, Group( "Terrain" ), Range( 0.0f, 128.0f )]
	public float SimplexAmplitude { get; set; } = 24.0f;

	[Property, Group( "Terrain" ), Range( -128.0f, 128.0f )]
	public float SimplexBaseHeight { get; set; }

	[Property, Group( "Terrain" )]
	public int SimplexSeed { get; set; } = 1337;

	[Property, Group( "Rendering" )]
	public Material TerrainMaterial { get; set; }

	[Property, Group( "Rendering" )]
	public VoxelVisualBackendMode VisualBackend { get; set; } = VoxelVisualBackendMode.CpuChunks;

	[Property, Group( "Rendering" )]
	public VoxelGpuTerrainLodPolicy GpuTerrainLodPolicy { get; set; } = VoxelGpuTerrainLodPolicy.FixedLod0;

	[Property, Group( "Rendering" ), Range( 4, 8 )]
	public int GpuClipboxBlocksPerAxis { get; set; } = 8;

	[Property, Group( "Rendering" ), Range( 1, 7 )]
	public int GpuClipboxLevelCount { get; set; } = 4;

	[Property, Group( "Rendering" )]
	public bool GpuClipboxMatchChunkRadius { get; set; } = true;

	[Property, Group( "Rendering" )]
	public bool GpuAutoScalePoolToChunkRadius { get; set; } = true;

	[Property, Group( "Rendering" ), Range( 65536, MaximumGpuVertexPoolCapacity )]
	public int GpuVertexPoolCapacity { get; set; } = 8388608;

	[Property, Group( "Rendering" ), Range( 196608, MaximumGpuIndexPoolCapacity )]
	public int GpuIndexPoolCapacity { get; set; } = 50331648;

	public int EffectiveGpuVertexPoolCapacity { get; private set; }

	public int EffectiveGpuIndexPoolCapacity { get; private set; }

	[Property, Group( "Rendering" ), Range( 0, 1 )]
	public int GpuTerrainRuleVersion { get; set; }

	[Property, Group( "Rendering" )]
	public bool GpuTerrainRenderingEnabled { get; set; } = true;

	[Property, Group( "Diagnostics" )]
	public bool GpuTerrainProcessingEnabled { get; set; } = true;

	[Property, Group( "Rendering" ), Range( 0, 4 )]
	public int GpuFrustumPaddingChunks { get; set; } = 1;

	[Property, Group( "Meshing" ), Range( 1, MaximumConcurrentCpuChunkBuilds )]
	public int CpuChunkBuildConcurrency { get; set; } = 4;

	[Property, Group( "Rendering" ), Range( 1, MaximumCpuMeshUploadsPerFrame )]
	public int CpuMeshUploadsPerFrame { get; set; } = 4;

	[Property, Group( "Rendering" ), Range( 0.25f, 12.0f )]
	public float CpuMainThreadBudgetMilliseconds { get; set; } = 4.0f;

	[Property, Group( "Collision" ), Range( 1, MaximumCollisionChunkRadius )]
	public int CollisionChunkRadius { get; set; } = 2;

	[Property, Group( "Collision" ), Range( 1, MaximumCollisionBuildsPerFrame )]
	public int CollisionBuildsPerFrame { get; set; } = 1;

	[Property, Group( "Collision" ), Range( 1, MaximumConcurrentCollisionBuilds )]
	public int CollisionBuildConcurrency { get; set; } = 2;

	[Property, Group( "Diagnostics" )]
	public bool LogGeneration { get; set; }

	[Property, Group( "Diagnostics" )]
	public bool CaptureCallCounts { get; set; }

	[Property, Group( "Diagnostics" )]
	public bool RequestGpuTransvoxelProof { get; set; }

	[Property, Group( "Diagnostics" )]
	public bool RequestGpuTransitionCaseProof { get; set; }

	[Property, Group( "Diagnostics" )]
	public bool RequestGpuClipboxSeamProof { get; set; }

	[Property, Group( "Diagnostics" )]
	public bool RequestGpuTerrainDiagnosticsLog { get; set; }

	[Property, Group( "Diagnostics" )]
	public bool CaptureFrameStutterDiagnostics { get; set; }

	[Property, Group( "Diagnostics" )]
	public VoxelGpuClipboxDebugMode GpuClipboxDebugMode { get; set; } = VoxelGpuClipboxDebugMode.Off;

	[Property, Group( "Diagnostics" )]
	public bool GpuClipboxDebugLabels { get; set; } = true;

	[Property, Group( "Diagnostics" ), Range( 1, 4096 )]
	public int GpuClipboxDebugMaxBlocks { get; set; } = 4096;

	[Property, Group( "Diagnostics" ), Range( 1, 4096 )]
	public int GpuClipboxDebugMaxTransitions { get; set; } = 4096;

	[Property, ReadOnly, Group( "Diagnostics" )]
	public string GpuTerrainLiveDiagnostics => _gpuTerrainLiveDiagnostics;

	[Property, ReadOnly, Group( "Diagnostics" )]
	public string GpuTerrainStructuredDebugReport => _gpuTerrainBackend?.StructuredDebugReportJson ?? "{}";

	[Property, ReadOnly, Group( "Diagnostics" )]
	public string GpuClipboxSeamProof => !_hasGpuClipboxSeamProofResult ? "not-run" : FormatGpuClipboxSeamProof( _lastGpuClipboxSeamProofResult );

	[Property, Group( "Diagnostics" ), Range( 0, MaximumDetailedChunkLogs )]
	public int DetailedChunkLogLimit { get; set; } = 64;

	public IReadOnlyDictionary<Vector3Int, VoxelChunk> Chunks => _chunks;
	public int LoadedChunkCount => _chunks.Count;
	public int DesiredChunkCount => GpuTerrainLodPolicy == VoxelGpuTerrainLodPolicy.RegularClipbox && VisualBackend == VoxelVisualBackendMode.GpuPersistentFixedLod ? _gpuDesiredBlockCount : _desiredChunkCoordinates.Count;
	public int ActiveChunkGameObjectCount => _chunkGameObjects.Count;
	public uint WorldEditRevision => _editJournal.WorldRevision;
	public int ActiveEditOperationCount => _editJournal.Count;
	public int ChunkDiameter => ChunkRadius * 2;
	public int ConfiguredChunkCount => GpuTerrainLodPolicy == VoxelGpuTerrainLodPolicy.RegularClipbox && VisualBackend == VoxelVisualBackendMode.GpuPersistentFixedLod ? GpuClipboxConfig.ExpectedActiveRegularCount : checked( ChunkDiameter * ChunkDiameter );
	[Property, ReadOnly, Group( "Rendering" )]
	public int EffectiveGpuClipboxLevelCount => ResolveGpuClipboxLevelCount();

	[Property, ReadOnly, Group( "Rendering" )]
	public int GpuClipboxCoverageRadius => checked( GpuClipboxBlocksPerAxis * (1 << (ResolveGpuClipboxLevelCount() - 1)) / 2 );

	private VoxelClipboxConfig GpuClipboxConfig => new( GpuClipboxBlocksPerAxis, ResolveGpuClipboxLevelCount(), checked( (ushort)GpuTerrainRuleVersion ) );
	public bool IsWorldGenerationPending => _worldGenerationPending;
	public bool IsPlayerSafetyActive => _playerSafetyActive;
	internal bool IsGpuTransvoxelProofRunning => _gpuTransvoxelProof?.IsRunning == true;
	internal bool HasGpuTransvoxelProofResult => _hasGpuTransvoxelProofResult;
	internal VoxelGpuTransvoxelProofResult LastGpuTransvoxelProofResult => _lastGpuTransvoxelProofResult;
	internal bool HasGpuTransitionCaseProofResult => _hasGpuTransitionCaseProofResult;
	internal VoxelGpuTransitionCaseProofResult LastGpuTransitionCaseProofResult => _lastGpuTransitionCaseProofResult;
	public long LatestChunkTimingSequence => _nextChunkTimingSequence;
	public long LatestBatchTimingSequence => _nextBatchTimingSequence;
	public bool IsTerrainSettled => (VisualBackend == VoxelVisualBackendMode.GpuPersistentFixedLod || AreDesiredChunksLoaded()) && _chunkStreamingGenerationQueue.Count == 0 &&
		(Application.IsDedicatedServer || (VisualBackend == VoxelVisualBackendMode.GpuPersistentFixedLod
			? _gpuTerrainBackend?.IsSettled == true
			: _cpuChunkStates.Count == DesiredChunkCount && ActiveChunkGameObjectCount == DesiredChunkCount)) &&
		GetPendingVisualBuildCount() == 0 && GetPendingCollisionBuildCount() == 0 &&
		!_worldGenerationPending && !_cpuBatchSummaryPending;
	[Property, ReadOnly, Group( "Diagnostics" )]
	public string TerrainSettleDiagnostics =>
		$"desiredLoaded={AreDesiredChunksLoaded()}; generationQueue={_chunkStreamingGenerationQueue.Count}; gpuSettled={_gpuTerrainBackend?.IsSettled}; cpuStates={_cpuChunkStates.Count}/{DesiredChunkCount}; activeObjects={ActiveChunkGameObjectCount}; pendingVisual={GetPendingVisualBuildCount()}; pendingCollision={GetPendingCollisionBuildCount()}; worldPending={_worldGenerationPending}; batchSummaryPending={_cpuBatchSummaryPending}; safety={_playerSafetyActive}";
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
		SimplexFrequency = System.Math.Clamp( SimplexFrequency, 0.0001f, 0.1f );
		SimplexAmplitude = System.Math.Clamp( SimplexAmplitude, 0.0f, 128.0f );
		SimplexBaseHeight = System.Math.Clamp( SimplexBaseHeight, -128.0f, 128.0f );
		CpuChunkBuildConcurrency = System.Math.Clamp( CpuChunkBuildConcurrency, 1, MaximumConcurrentCpuChunkBuilds );
		CpuMeshUploadsPerFrame = System.Math.Clamp( CpuMeshUploadsPerFrame, 1, MaximumCpuMeshUploadsPerFrame );
		CpuMainThreadBudgetMilliseconds = System.Math.Clamp( CpuMainThreadBudgetMilliseconds, 0.25f, 12.0f );
		GpuVertexPoolCapacity = System.Math.Clamp( GpuVertexPoolCapacity, 65536, MaximumGpuVertexPoolCapacity );
		GpuIndexPoolCapacity = System.Math.Clamp( GpuIndexPoolCapacity, 196608, MaximumGpuIndexPoolCapacity );
		GpuTerrainRuleVersion = System.Math.Clamp( GpuTerrainRuleVersion, 0, 1 );
		GpuClipboxBlocksPerAxis = GpuClipboxBlocksPerAxis <= 4 ? 4 : 8;
		GpuClipboxLevelCount = System.Math.Clamp( GpuClipboxLevelCount, 1, VoxelClipboxConfig.MaximumLevelCount );
		GpuClipboxDebugMaxBlocks = System.Math.Clamp( GpuClipboxDebugMaxBlocks, 1, 4096 );
		GpuClipboxDebugMaxTransitions = System.Math.Clamp( GpuClipboxDebugMaxTransitions, 1, 4096 );
		GpuFrustumPaddingChunks = System.Math.Clamp( GpuFrustumPaddingChunks, 0, 4 );
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
		if ( Scene?.IsEditor == true && !GenerateInEditor ) return;
		if ( CaptureFrameStutterDiagnostics && Sandbox.Diagnostics.PerformanceStats.FrameTime * 1000.0 >= 33.3333 && _idleStutterDiagnosticCount < 16 )
		{
			_idleStutterDiagnosticCount++;
			var updateTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Update.GetMetric( 1 );
			var renderTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Render.GetMetric( 1 );
			var physicsTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Physics.GetMetric( 1 );
			var idleTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Idle.GetMetric( 1 );
			var asyncTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Async.GetMetric( 1 );
			var gcTiming = Sandbox.Diagnostics.PerformanceStats.Timings.GcPause.GetMetric( 1 );
			var frameMilliseconds = Sandbox.Diagnostics.PerformanceStats.FrameTime * 1000.0;
			var unaccountedFrameMilliseconds = ComputeUnaccountedFrameMilliseconds( frameMilliseconds, updateTiming.Max, renderTiming.Max, physicsTiming.Max, idleTiming.Max, asyncTiming.Max, gcTiming.Max );
			Log.Info( $"Voxel idle stutter diagnostic: frameMs={frameMilliseconds:F2}, unaccountedFrameMs={unaccountedFrameMilliseconds:F2}, updateMs={updateTiming.Max:F2}, renderMs={renderTiming.Max:F2}, physicsMs={physicsTiming.Max:F2}, idleMs={idleTiming.Max:F2}, asyncMs={asyncTiming.Max:F2}, gcTimingMs={gcTiming.Max:F2}, gpuMs={Sandbox.Diagnostics.PerformanceStats.GpuFrametime:F2}, allocated={Sandbox.Diagnostics.PerformanceStats.BytesAllocated}, gcPause={Sandbox.Diagnostics.PerformanceStats.GcPause}." );
		}
		CountCall( ref _callManagerUpdates );
		UpdateGpuTransvoxelProof();
		UpdateGpuTransitionCaseProof();
		if ( RequestGpuTransvoxelProof )
		{
			RequestGpuTransvoxelProof = false;
			RunGpuTransvoxelProof();
		}
		if ( RequestGpuTransitionCaseProof )
		{
			RequestGpuTransitionCaseProof = false;
			RunGpuTransitionCaseProof();
		}
		UpdateGpuClipboxSeamProof();
		if ( RequestGpuClipboxSeamProof && _gpuTerrainBackend?.IsSettled == true )
		{
			RequestGpuClipboxSeamProof = false;
			RunGpuClipboxSeamProof();
		}
		if ( CaptureWorldConfiguration() != _generationConfiguration )
		{
			_worldRegenerationRequested = true;
		}
		if ( _worldRegenerationRequested && !_worldGenerationPending )
		{
			if ( VisualBackend == VoxelVisualBackendMode.GpuPersistentFixedLod ) GenerateGpuTerrainWorld();
			else GenerateWorld();
		}
		UpdateWorldGeneration();
		UpdatePlayerSafety();
		if ( _worldGenerationPending || _worldRegenerationRequested )
		{
			return;
		}
		if ( VisualBackend == VoxelVisualBackendMode.CpuChunks ) UpdateChunkStreaming();
		else UpdateGpuChunkStreaming();
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
		DisposeGpuTransvoxelProof();
		DisposeGpuTransitionCaseProof();
		DisposeGpuTerrainBackend();
		DisposeCpuVisualWorld();
		ClearChunkColliders();
		ClearChunkGameObjects();
		ResetWorldGeneration();
	}

	protected override void OnDestroy()
	{
		DisposeGpuTransvoxelProof();
		DisposeGpuTransitionCaseProof();
		DisposeGpuTerrainBackend();
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
		if ( VisualBackend == VoxelVisualBackendMode.GpuPersistentFixedLod )
		{
			GenerateGpuTerrainWorld();
			return;
		}
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
		DisposeGpuTerrainBackend();
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

	internal void GenerateGpuTerrainWorld()
	{
		CountCall( ref _callWorldGenerationRequests );
		ResolveGpuPoolCapacity();
		_generationConfiguration = CaptureWorldConfiguration();
		_worldGenerationPending = false;
		_worldRegenerationRequested = false;
		_worldGenerationTasks.Clear();
		DisposeCpuVisualWorld();
		DisposeGpuTerrainBackend();
		ClearChunkColliders();
		ClearChunkGameObjects();
		lock ( _sdfLock ) _chunks.Clear();
		_initialSdfGenerationMilliseconds.Clear();
		_chunkStreamingGenerationQueue.Clear();
		_chunkStreamingQueuedCoordinates.Clear();
		_chunkStreamingRequestTimestamps.Clear();
		_lastChunkStreamingInterestTimestamp = 0;
		_desiredChunkCoordinates.Clear();
		_gpuStreamingObserversInitialized = false;
		_gpuClipboxObserverInitialized = false;
		_gpuDesiredBlockCount = 0;
		_gpuStreamingObservers.Clear();
		_gpuPreviousStreamingObservers.Clear();
		var observers = GetStreamingObserverChunks();
		if ( GpuTerrainLodPolicy == VoxelGpuTerrainLodPolicy.FixedLod0 ) PopulateDesiredChunkCoordinates( observers, _desiredChunkCoordinates );
		_gpuStreamingObservers.AddRange( observers );
		StartGpuTerrainWorld();
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
		StartVisualWorld();
	}

	[Button]
	public void RebuildVisualWorld()
	{
		DisposeCpuVisualWorld();
		DisposeGpuTerrainBackend();
		StartVisualWorld();
	}

	internal void RebuildCollisionWorldForBenchmark()
	{
		ActivatePlayerSafety();
		ClearChunkColliders();
		RefreshCollisionInterests();
	}

	private void StartVisualWorld()
	{
		if ( VisualBackend == VoxelVisualBackendMode.GpuPersistentFixedLod ) StartGpuTerrainWorld();
		else StartCpuChunkWorld();
	}

	private void StartGpuTerrainWorld()
	{
		DisposeCpuVisualWorld();
		DisposeGpuTerrainBackend();
		if ( Application.IsDedicatedServer )
		{
			Log.Info( "Voxel dedicated-server world active: GPU visual backend is inert and owns no rendering resources." );
			RefreshCollisionInterests();
			PumpCollisionBuildQueue();
			return;
		}
		if ( RequestGpuTerrainDiagnosticsLog )
		{
			RequestGpuTerrainDiagnosticsLog = false;
			LogGpuTerrainDiagnostics();
		}
		if ( Scene.Camera is null )
		{
			Log.Error( "Voxel persistent GPU terrain requires an active scene camera." );
			return;
		}
		try
		{
			var clipboxConfig = GpuTerrainLodPolicy == VoxelGpuTerrainLodPolicy.RegularClipbox ? GpuClipboxConfig : (VoxelClipboxConfig?)null;
			var clipboxResidentCapacity = clipboxConfig.HasValue
				// A moving clipbox retains the committed rings while the replacement rings and
				// their transition seams are generated. Reserve both generations explicitly.
				? checked( 2 * (clipboxConfig.Value.StableRegularSlotCount + clipboxConfig.Value.StableTransitionSlotCount) )
				: 0;
			var residentCapacity = checked( System.Math.Max( DesiredChunkCount, clipboxResidentCapacity ) + GpuStreamingStagingResidentCapacity );
			_gpuTerrainBackend = new VoxelGpuTerrainBackend(
				Scene.SceneWorld,
				Scene.Camera,
				ChunkSize,
				VoxelSize,
				SdfClampDistance,
				SimplexFrequency,
				SimplexAmplitude,
				SimplexBaseHeight,
				SimplexSeed,
				GpuFrustumPaddingChunks,
				System.Math.Max( 1, residentCapacity ),
				EffectiveGpuVertexPoolCapacity,
				EffectiveGpuIndexPoolCapacity,
				clipboxConfig );
			var editSnapshot = _editJournal.CreateGpuSnapshot( out var editCount );
			_gpuTerrainBackend.SetEditOperations( editSnapshot, editCount );
			if ( clipboxConfig.HasValue )
			{
				_gpuDesiredBlockCount = clipboxConfig.Value.ExpectedActiveRegularCount;
				var observer = GetGpuObserverCanonicalSample();
				_gpuTerrainBackend.QueueClipboxObserver( observer );
				_gpuClipboxObserverInitialized = true;
				_gpuClipboxObserverBaseBlock = VoxelClipboxCoordinates.GetObserverBaseBlock( observer );
			}
			else
			{
				var orderedCoordinates = new List<Vector3Int>( _desiredChunkCoordinates );
				orderedCoordinates.Sort( (left, right) => GetStreamingPriority( left, _gpuStreamingObservers ).CompareTo( GetStreamingPriority( right, _gpuStreamingObservers ) ) );
				_gpuTerrainBackend.QueueStaticSet( orderedCoordinates, GpuTerrainRuleVersion );
			}
			_gpuTerrainBackend.SetRenderingEnabled( GpuTerrainRenderingEnabled );
			_gpuTerrainBackend.SetProcessingEnabled( GpuTerrainProcessingEnabled );
			Log.Info( $"Voxel persistent GPU terrain world scheduled: policy={GpuTerrainLodPolicy}, chunks={DesiredChunkCount:N0}, residentCapacity={residentCapacity:N0}, staging={GpuStreamingStagingResidentCapacity:N0}, frustumPadding={GpuFrustumPaddingChunks:N0} chunk(s), clipbox={GpuClipboxBlocksPerAxis}x{GpuClipboxBlocksPerAxis}x{GpuClipboxBlocksPerAxis} levels={EffectiveGpuClipboxLevelCount}, coverageRadius={GpuClipboxCoverageRadius}, matchRadius={GpuClipboxMatchChunkRadius}, batchMax={VoxelGpuScratchArena.MaximumBatchSize:N0}, autoPool={GpuAutoScalePoolToChunkRadius}, vertexPool={FormatBytes( (long)EffectiveGpuVertexPoolCapacity * 44 )}, indexPool={FormatBytes( (long)EffectiveGpuIndexPoolCapacity * sizeof( uint ) )}, simplexFrequency={SimplexFrequency:F4}, simplexAmplitude={SimplexAmplitude:F1}, simplexBaseHeight={SimplexBaseHeight:F1}, simplexSeed={SimplexSeed}, rule={GpuTerrainRuleVersion}, geometryReadback=disabled." );
		}
		catch ( System.Exception exception )
		{
			DisposeGpuTerrainBackend();
			Log.Error( $"Voxel persistent GPU terrain failed to start: {exception.Message}" );
		}
		if ( _gpuTerrainBackend is not null && _gpuTerrainBackend.IsTerrainRenderingEnabled != GpuTerrainRenderingEnabled )
			_gpuTerrainBackend.SetRenderingEnabled( GpuTerrainRenderingEnabled );
		if ( _gpuTerrainBackend is not null && _gpuTerrainBackend.IsProcessingEnabled != GpuTerrainProcessingEnabled )
			_gpuTerrainBackend.SetProcessingEnabled( GpuTerrainProcessingEnabled );
	}

	internal VoxelGpuTerrainDiagnostics CaptureGpuTerrainDiagnostics() =>
		_gpuTerrainBackend?.CaptureDiagnostics() ?? default;

	private void RefreshGpuTerrainLiveDiagnostics( VoxelGpuTerrainDiagnostics diagnostics )
	{
		_gpuTerrainLiveDiagnostics = $"available={diagnostics.Available}; policy={diagnostics.LodPolicy}; indirectGroup={diagnostics.IndirectCommandGroupSize}; requested={diagnostics.RequestedBlocks}; residents={diagnostics.ResidentBlocks}; blocked={diagnostics.BlockedRequests}; capacityLimited={diagnostics.CapacityLimited}; pendingCount={diagnostics.PendingCountBatches}; pendingEmit={diagnostics.PendingEmitBatches}; clipboxRevision={diagnostics.ClipboxRevision}; clipboxChangedSlots={diagnostics.ClipboxChangedSlots}; clipboxPendingRevisions={diagnostics.ClipboxPendingRevisionCount}/{diagnostics.ClipboxMaximumPendingRevisionCount}; clipboxStable/active={diagnostics.ClipboxStableSlotCount}/{diagnostics.ClipboxActiveSlotCount}; clipboxDropped={diagnostics.ClipboxDroppedWork}; clipboxStationary={diagnostics.ClipboxStationaryUpdates}; transitionCapacity={diagnostics.ClipboxTransitionCapacity}; transitionActive/changed/pending={diagnostics.ClipboxTransitionActiveSlotCount}/{diagnostics.ClipboxTransitionChangedSlots}/{diagnostics.ClipboxTransitionPendingSlots}; transitionResidents/renderable/visible={diagnostics.TransitionResidentBlocks}/{diagnostics.TransitionRenderableResidents}/{diagnostics.TransitionVisibleDrawCommands}; transitionPending/blocked={diagnostics.TransitionPendingRequests}/{diagnostics.TransitionBlockedRequests}; transitionAllocBytes={diagnostics.TransitionAllocatedBytes}; transitionDependencyMismatches={diagnostics.ClipboxTransitionDependencyMismatches}; transitionStationary={diagnostics.ClipboxTransitionStationaryUpdates}; readbacks={diagnostics.CountReadbackCount}; visibleDraws={diagnostics.VisibleDrawCommands}; poolUsed={diagnostics.PoolUsedBytes}; vertexFree={diagnostics.VertexFree}; indexFree={diagnostics.IndexFree}; vertexLargest={diagnostics.VertexLargestFree}; indexLargest={diagnostics.IndexLargestFree}; requestToVisibleP95Ms={diagnostics.RequestToVisible.P95Milliseconds:F2}; failure={diagnostics.Failure}";
	}

	internal static double ComputeUnaccountedFrameMilliseconds( double frameMilliseconds, params double[] timingMilliseconds )
	{
		double accountedMilliseconds = 0.0;
		foreach ( var timing in timingMilliseconds ) accountedMilliseconds += System.Math.Max( 0.0, timing );
		return System.Math.Max( 0.0, frameMilliseconds - accountedMilliseconds );
	}

	[Button]
	public void LogGpuTerrainDiagnostics()
	{
		var diagnostics = CaptureGpuTerrainDiagnostics();
		RefreshGpuTerrainLiveDiagnostics( diagnostics );
		Log.Info( $"Voxel GPU terrain diagnostics: backend={diagnostics.Backend}, policy={diagnostics.LodPolicy}, available={diagnostics.Available}, requested={diagnostics.RequestedBlocks:N0}, residents={diagnostics.ResidentBlocks:N0}, blocked={diagnostics.BlockedRequests:N0}, capacityLimited={diagnostics.CapacityLimited}, pendingCount={diagnostics.PendingCountBatches:N0}, pendingEmit={diagnostics.PendingEmitBatches:N0}, visibleDraws={diagnostics.VisibleDrawCommands:N0}, transitionResidents/renderable/visible={diagnostics.TransitionResidentBlocks:N0}/{diagnostics.TransitionRenderableResidents:N0}/{diagnostics.TransitionVisibleDrawCommands:N0}, transitionPending/blocked={diagnostics.TransitionPendingRequests:N0}/{diagnostics.TransitionBlockedRequests:N0}, transitionAllocBytes={diagnostics.TransitionAllocatedBytes:N0}, clipboxRevision={diagnostics.ClipboxRevision}, clipboxChangedSlots={diagnostics.ClipboxChangedSlots:N0}, clipboxPendingRevisions={diagnostics.ClipboxPendingRevisionCount}/{diagnostics.ClipboxMaximumPendingRevisionCount}, clipboxStable/active={diagnostics.ClipboxStableSlotCount:N0}/{diagnostics.ClipboxActiveSlotCount:N0}, clipboxDropped={diagnostics.ClipboxDroppedWork:N0}, clipboxStationary={diagnostics.ClipboxStationaryUpdates:N0}, backpressure={diagnostics.BackpressureEvents:N0}, allocationFailures={diagnostics.AllocationFailures:N0}, deferrals={diagnostics.CapacityDeferrals:N0}, evictions={diagnostics.CapacityEvictions:N0}, staleRejected={diagnostics.StalePublicationsRejected:N0}, scratch={FormatBytes( diagnostics.ScratchBytes )}, poolUsed/peak/capacity={FormatBytes( diagnostics.PoolUsedBytes )}/{FormatBytes( diagnostics.PeakPoolUsedBytes )}/{FormatBytes( diagnostics.PoolCapacityBytes )}, vertexFree/largest={diagnostics.VertexFree:N0}/{diagnostics.VertexLargestFree:N0}, indexFree/largest={diagnostics.IndexFree:N0}/{diagnostics.IndexLargestFree:N0}, countSubmit={diagnostics.CountSubmissionMilliseconds:F3}ms total/{diagnostics.CountSubmissionPerBlockMilliseconds:F4}ms per block, countReadback={diagnostics.CountReadbackCount:N0} batches at {diagnostics.CountReadbackAverageMilliseconds:F3}ms avg, emitSubmit={diagnostics.EmitSubmissionMilliseconds:F3}ms total/{diagnostics.EmitSubmissionPerBlockMilliseconds:F4}ms per block, requestToVisible(avg/p95/max)={diagnostics.RequestToVisible.AverageMilliseconds:F2}/{diagnostics.RequestToVisible.P95Milliseconds:F2}/{diagnostics.RequestToVisible.MaximumMilliseconds:F2}ms, batchComplete(avg/p95/max)={diagnostics.BatchCompletion.AverageMilliseconds:F2}/{diagnostics.BatchCompletion.P95Milliseconds:F2}/{diagnostics.BatchCompletion.MaximumMilliseconds:F2}ms, geometryReadback={diagnostics.GeometryReadbackBytes:N0}B, transitionGeometryReadback={diagnostics.TransitionGeometryReadbackBytes:N0}B, transitionCpuSdf={diagnostics.TransitionCpuSdfEvaluations:N0}, failure={diagnostics.Failure}." );
	}

	private void DisposeGpuTerrainBackend()
	{
		_gpuTerrainBackend?.Dispose();
		_gpuTerrainBackend = null;
		_gpuTerrainLiveDiagnostics = "available=False; requested=0; residents=0; pendingCount=0; pendingEmit=0; readbacks=0; visibleDraws=0; poolUsed=0; requestToVisibleP95Ms=0.00; failure=";
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
		if ( !TryGetChunk( coordinate, out _ ) )
		{
			GenerateChunk( coordinate );
			Log.Info( "Voxel GPU Transvoxel proof generated its origin chunk for the proof-only CPU reference." );
		}
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

	public void ClearGpuTransvoxelProof()
	{
		DisposeGpuTransvoxelProof();
	}

	[Button]
	public void RunGpuTransitionCaseProof()
	{
		DisposeGpuTransitionCaseProof();
		_hasGpuTransitionCaseProofResult = false;
		_lastGpuTransitionCaseProofResult = default;
		if ( Application.IsDedicatedServer )
		{
			Log.Error( "Voxel GPU transition case proof requires a rendering client." );
			return;
		}
		if ( Scene.SceneWorld is null )
		{
			Log.Error( "Voxel GPU transition case proof requires an active scene world." );
			return;
		}
		try
		{
			_gpuTransitionCaseProof = new VoxelGpuTransitionCaseProof( Scene.SceneWorld );
			_gpuTransitionCaseProof.Run();
			Log.Info( "Voxel GPU transition case proof scheduled: variants=6144, cases=512, orientations=6, winding=2, diagnosticReadback=enabled." );
		}
		catch ( System.Exception exception )
		{
			DisposeGpuTransitionCaseProof();
			_lastGpuTransitionCaseProofResult = new VoxelGpuTransitionCaseProofResult( false, exception.Message, 6144, 0, 0, 0, 0, 0.0, 0.0, 0.0 );
			_hasGpuTransitionCaseProofResult = true;
			Log.Error( $"Voxel GPU transition case proof failed to start: {exception.Message}" );
		}
	}

	public void ClearGpuTransitionCaseProof() => DisposeGpuTransitionCaseProof();

	private void UpdateGpuTransitionCaseProof()
	{
		if ( _gpuTransitionCaseProof is null || !_gpuTransitionCaseProof.TryTakeResult( out var result ) ) return;
		_lastGpuTransitionCaseProofResult = result;
		_hasGpuTransitionCaseProofResult = true;
		Log.Info( $"Voxel GPU transition case proof: result={(result.Passed ? "PASS" : "FAIL")}, variants={result.Variants:N0}, mismatches={result.MismatchedCases:N0}, vertices={result.VertexCount:N0}, triangles={result.TriangleCount:N0}, gpuBuffers={FormatBytes( result.GpuBufferBytes )}, submission={result.SubmissionMilliseconds:F3}ms, completion={result.CompletionMilliseconds:F3}ms, diagnosticReadback={result.ReadbackMilliseconds:F3}ms, validation={result.Failure}." );
	}

	private void DisposeGpuTransitionCaseProof()
	{
		_gpuTransitionCaseProof?.Dispose();
		_gpuTransitionCaseProof = null;
	}

	[Button]
	public void RunGpuClipboxSeamProof()
	{
		if ( Application.IsDedicatedServer )
		{
			Log.Error( "Voxel GPU clipbox seam proof requires a rendering client." );
			return;
		}
		if ( _gpuTerrainBackend is null )
		{
			Log.Error( "Voxel GPU clipbox seam proof requires the persistent regular clipbox backend." );
			return;
		}
		if ( !_gpuTerrainBackend.IsSettled )
		{
			Log.Warning( "Voxel GPU clipbox seam proof is waiting for terrain settlement." );
			RequestGpuClipboxSeamProof = true;
			return;
		}
		_lastGpuClipboxSeamProofResult = _gpuTerrainBackend.RunTestOnlySeamProof();
		_hasGpuClipboxSeamProofResult = true;
		Log.Info( $"Voxel GPU production clipbox seam proof: result={(_lastGpuClipboxSeamProofResult.Passed ? "PASS" : "FAIL")}, transitions={_lastGpuClipboxSeamProofResult.PublishedTransitions:N0}/{_lastGpuClipboxSeamProofResult.ActiveTransitions:N0}, missing={_lastGpuClipboxSeamProofResult.MissingTransitions:N0}, dependencies={_lastGpuClipboxSeamProofResult.DependencyMismatches:N0}, faceMasks={_lastGpuClipboxSeamProofResult.FaceMaskMismatches:N0}, boundary={_lastGpuClipboxSeamProofResult.BoundaryVertices:N0}/{_lastGpuClipboxSeamProofResult.UnmatchedBoundaryVertices:N0}, maxSeamError={_lastGpuClipboxSeamProofResult.MaximumSeamPositionError:F6}, readback={FormatBytes( _lastGpuClipboxSeamProofResult.GeometryReadbackBytes )} in {_lastGpuClipboxSeamProofResult.GeometryReadbackMilliseconds:F3}ms, validation={_lastGpuClipboxSeamProofResult.Failure}." );
	}

	public void ClearGpuClipboxSeamProof()
	{
		_hasGpuClipboxSeamProofResult = false;
		_lastGpuClipboxSeamProofResult = default;
	}

	private void UpdateGpuClipboxSeamProof()
	{
		if ( !RequestGpuClipboxSeamProof || _hasGpuClipboxSeamProofResult || _gpuTerrainBackend?.IsSettled != true ) return;
		RequestGpuClipboxSeamProof = false;
		RunGpuClipboxSeamProof();
	}

	private static string FormatGpuClipboxSeamProof( VoxelGpuClipboxSeamProofReport report ) =>
		$"result={(report.Passed ? "PASS" : "FAIL")}; transitions={report.PublishedTransitions}/{report.ActiveTransitions}; missing={report.MissingTransitions}; dependencies={report.DependencyMismatches}; faceMasks={report.FaceMaskMismatches}; invalidIndices={report.InvalidIndices}; degenerate={report.DegenerateTriangles}; boundary={report.BoundaryVertices}/{report.UnmatchedBoundaryVertices}; maxSeamError={report.MaximumSeamPositionError:F6}; readback={FormatBytes( report.GeometryReadbackBytes )}; failure={report.Failure}";

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
		var pending = _collisionGenerationQueue.Count + _collisionBuildQueue.Count;
		foreach ( var state in _chunkColliders.Values )
		{
			if ( state.Task is not null || state.ReadyResult.HasValue || state.Dirty ||
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
		if ( radius <= 0.0f || System.MathF.Abs( displacement ) <= 0.0001f )
		{
			return 0;
		}

		var brushCenter = GameObject.WorldTransform.PointToLocal( worldPosition ) / VoxelSize;
		var brushRadius = radius / VoxelSize;
		return ApplyEdit( new VoxelEditOp
		{
			Shape = VoxelEditShape.Sphere,
			Operation = displacement > 0.0f ? VoxelCsgOperation.SmoothSubtract : VoxelCsgOperation.SmoothAdd,
			Position = brushCenter,
			Rotation = Rotation.Identity,
			Size = new Vector3( brushRadius, 0.0f, 0.0f ),
			Smoothness = System.MathF.Max( 0.25f, System.MathF.Abs( displacement / VoxelSize ) * 0.25f ),
			MaterialId = (ushort)VoxelMaterial.Terrain
		} );
	}

	public int ApplyEdit( VoxelEditOp requestedOperation )
	{
		CountCall( ref _callBrushRequests );
		var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		var changedChunks = new List<Vector3Int>();
		var writeWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
		System.TimeSpan writeWaitElapsed;
		System.TimeSpan sdfMutationElapsed;
		VoxelEditOp operation;
		long testedSamples = 0;
		long changedSamples = 0;
		lock ( _sdfLock )
		{
			writeWaitElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( writeWaitStart );
			var sdfMutationStart = System.Diagnostics.Stopwatch.GetTimestamp();
			operation = _editJournal.Append( requestedOperation );
			var affectedChunks = VoxelEditInvalidation.GetCpuChunks( operation, ChunkSize );
			CountCall( ref _callBrushChunkTests, affectedChunks.Count );
			foreach ( var coordinate in affectedChunks )
			{
				if ( !_chunks.TryGetValue( coordinate, out var chunk ) ) continue;
				var changed = ApplyEditToChunk( chunk, operation, out var tested );
				testedSamples += tested;
				changedSamples += changed;
				if ( changed > 0 ) changedChunks.Add( coordinate );
			}
			sdfMutationElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( sdfMutationStart );
		}

		var cpuQueueStart = System.Diagnostics.Stopwatch.GetTimestamp();
		foreach ( var coordinate in changedChunks )
		{
			if ( _cpuChunkStates.TryGetValue( coordinate, out var cpuState ) )
			{
				MarkCpuChunkDirty( cpuState );
			}
			MarkCollisionDirty( coordinate );
		}
		MergeCoherentVisualEditBatch( changedChunks );
		MergeCoherentCollisionEditBatch( changedChunks );
		ActivatePlayerSafetyForEdit( changedChunks );
		PumpCpuChunkBuildQueue();
		var cpuQueueElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( cpuQueueStart );
		var gpuDirtyBlocks = 0;
		var gpuQueueStart = System.Diagnostics.Stopwatch.GetTimestamp();
		if ( _gpuTerrainBackend is not null )
		{
			var snapshot = _editJournal.CreateGpuSnapshot( out var editCount );
			gpuDirtyBlocks = _gpuTerrainBackend.QueueEdit( snapshot, editCount, operation, operation.WorldRevision );
		}
		var gpuQueueElapsed = System.Diagnostics.Stopwatch.GetElapsedTime( gpuQueueStart );

		var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime( startTimestamp );
		Log.Info(
			$"Voxel edit: id={operation.EditId}, revision={operation.WorldRevision}, shape={operation.Shape}, operation={operation.Operation}, " +
			$"dirtyCpuChunks={changedChunks.Count:N0}, dirtyGpuBlocks={gpuDirtyBlocks:N0}, activeJournal={_editJournal.Count:N0}, " +
			$"cpuCollisionQueued={changedChunks.Count:N0}, samplesTested/changed={testedSamples:N0}/{changedSamples:N0}, " +
			$"sdfWriteWait={writeWaitElapsed.TotalMilliseconds:F2}ms, sdfMutation={sdfMutationElapsed.TotalMilliseconds:F2}ms, " +
			$"cpuQueue={cpuQueueElapsed.TotalMilliseconds:F2}ms, gpuQueue={gpuQueueElapsed.TotalMilliseconds:F2}ms, total={elapsed.TotalMilliseconds:F2}ms."
		);

		return System.Math.Max( changedChunks.Count, gpuDirtyBlocks );
	}

	private int ApplyEditToChunk( VoxelChunk chunk, VoxelEditOp operation, out long testedSampleCount )
	{
		var chunkOrigin = GetChunkVoxelOrigin( chunk.Coordinate );
		var bounds = VoxelEditJournal.GetBounds( operation );
		var chunkMaximum = chunkOrigin + new Vector3( chunk.Size );
		if ( bounds.Maxs.x < chunkOrigin.x || bounds.Mins.x > chunkMaximum.x ||
			 bounds.Maxs.y < chunkOrigin.y || bounds.Mins.y > chunkMaximum.y ||
			 bounds.Maxs.z < chunkOrigin.z || bounds.Mins.z > chunkMaximum.z )
		{
			testedSampleCount = 0;
			return 0;
		}

		var minimumX = System.Math.Clamp( (int)System.MathF.Floor( bounds.Mins.x - chunkOrigin.x ), 0, chunk.Size );
		var maximumX = System.Math.Clamp( (int)System.MathF.Ceiling( bounds.Maxs.x - chunkOrigin.x ), 0, chunk.Size );
		var minimumY = System.Math.Clamp( (int)System.MathF.Floor( bounds.Mins.y - chunkOrigin.y ), 0, chunk.Size );
		var maximumY = System.Math.Clamp( (int)System.MathF.Ceiling( bounds.Maxs.y - chunkOrigin.y ), 0, chunk.Size );
		var minimumZ = System.Math.Clamp( (int)System.MathF.Floor( bounds.Mins.z - chunkOrigin.z ), 0, chunk.Size );
		var maximumZ = System.Math.Clamp( (int)System.MathF.Ceiling( bounds.Maxs.z - chunkOrigin.z ), 0, chunk.Size );
		testedSampleCount = checked( (long)(maximumX - minimumX + 1) * (maximumY - minimumY + 1) * (maximumZ - minimumZ + 1) );
		CountCall( ref _callBrushSamplesTested, testedSampleCount );
		var changedSampleCount = 0;
		for ( var z = minimumZ; z <= maximumZ; z++ )
		for ( var y = minimumY; y <= maximumY; y++ )
		for ( var x = minimumX; x <= maximumX; x++ )
		{
			var canonicalSample = new Vector3( chunkOrigin.x + x, chunkOrigin.y + y, chunkOrigin.z + z );
			var existing = chunk.GetVoxel( x, y, z );
			var distance = VoxelEditJournal.ApplyDistance( operation, canonicalSample, existing.Distance );
			var sourceMaterial = existing.Material == VoxelMaterial.Air ? VoxelMaterial.Terrain : existing.Material;
			var material = VoxelEditJournal.ApplyMaterial( operation, canonicalSample, distance, sourceMaterial );
			if ( chunk.SetVoxelIfChanged( x, y, z, new Voxel( distance, material ) ) ) changedSampleCount++;
		}
		CountCall( ref _callBrushSamplesChanged, changedSampleCount );
		return changedSampleCount;
	}

	public float QuerySdf( Vector3 worldPosition )
	{
		var canonicalSample = GameObject.WorldTransform.PointToLocal( worldPosition ) / VoxelSize;
		lock ( _sdfLock ) return _editJournal.EvaluateDistance( canonicalSample, EvaluateProceduralDistance( canonicalSample ) );
	}

	public bool TryRaycastSdf( Vector3 worldStart, Vector3 worldDirection, float maximumDistance, out Vector3 hitPosition )
	{
		var operations = _editJournal.CreateOperationSnapshot();
		float EvaluateWorldDistance( Vector3 worldPosition )
		{
			var canonicalSample = GameObject.WorldTransform.PointToLocal( worldPosition ) / VoxelSize;
			return VoxelEditJournal.EvaluateDistance( operations, canonicalSample, EvaluateProceduralDistance( canonicalSample ) ) * VoxelSize;
		}
		return VoxelSdfRaycast.TryTrace( worldStart, worldDirection, maximumDistance, System.MathF.Max( 1.0f, VoxelSize * 0.125f ), EvaluateWorldDistance, out hitPosition );
	}

	private void FillChunk( VoxelChunk chunk, int chunkSize )
	{
		var sampleSize = chunk.SampleSize;
		var chunkOrigin = new Vector3Int(
			chunk.Coordinate.x * chunkSize,
			chunk.Coordinate.y * chunkSize,
			(chunk.Coordinate.z - 1) * chunkSize
		);
		var proceduralTerrain = VisualBackend == VoxelVisualBackendMode.GpuPersistentFixedLod;
		var minimumSurfaceHeight = proceduralTerrain ? SimplexBaseHeight - SimplexAmplitude : 0.0f;
		var maximumSurfaceHeight = proceduralTerrain ? SimplexBaseHeight + SimplexAmplitude : 0.0f;
		var minimumDistance = chunkOrigin.z - maximumSurfaceHeight;
		var maximumDistance = chunkOrigin.z + chunk.Size - minimumSurfaceHeight;
		if ( minimumDistance >= chunk.DistanceClamp )
		{
			chunk.Fill( new Voxel( chunk.DistanceClamp, VoxelMaterial.Air ) );
		}
		else if ( maximumDistance <= -chunk.DistanceClamp )
		{
			chunk.Fill( new Voxel( -chunk.DistanceClamp, VoxelMaterial.Terrain ) );
		}
		else if ( !proceduralTerrain ) for ( var z = 0; z < sampleSize; z++ )
		{
			var distance = chunkOrigin.z + z;
			var material = distance < 0.0f ? VoxelMaterial.Terrain : VoxelMaterial.Air;
			chunk.FillLayer( z, new Voxel( distance, material ) );
		}
		else for ( var y = 0; y < sampleSize; y++ )
		for ( var x = 0; x < sampleSize; x++ )
		{
			var sampleX = chunkOrigin.x + x;
			var sampleY = chunkOrigin.y + y;
			var surfaceHeight = SimplexBaseHeight + TerrainSimplexNoise( new Vector2( sampleX, sampleY ) * SimplexFrequency, SimplexSeed ) * SimplexAmplitude;
			for ( var z = 0; z < sampleSize; z++ )
			{
				var distance = System.Math.Clamp( chunkOrigin.z + z - surfaceHeight, -chunk.DistanceClamp, chunk.DistanceClamp );
				chunk.SetVoxel( x, y, z, new Voxel( distance, distance < 0.0f ? VoxelMaterial.Terrain : VoxelMaterial.Air ) );
			}
		}
		var operations = _editJournal.CreateOperationSnapshot();
		if ( operations.Length == 0 ) return;
		for ( var z = 0; z < sampleSize; z++ )
		for ( var y = 0; y < sampleSize; y++ )
		for ( var x = 0; x < sampleSize; x++ )
		{
			var canonicalSample = new Vector3( chunkOrigin.x + x, chunkOrigin.y + y, chunkOrigin.z + z );
			var existing = chunk.GetVoxel( x, y, z );
			var distance = VoxelEditJournal.EvaluateDistance( operations, canonicalSample, existing.Distance );
			var material = VoxelEditJournal.EvaluateMaterial( operations, canonicalSample, distance, existing.Material == VoxelMaterial.Air ? VoxelMaterial.Terrain : existing.Material );
			chunk.SetVoxel( x, y, z, new Voxel( distance, material ) );
		}
	}

	protected override void DrawGizmos()
	{
		base.DrawGizmos();
		if ( GpuClipboxDebugMode == VoxelGpuClipboxDebugMode.Off || !TryPrepareClipboxDebugSnapshot( out var editorPreview ) ) return;
		var count = _gpuTerrainBackend is not null
			? _gpuTerrainBackend.CopyClipboxDebugBlocks( _gpuClipboxDebugScratch )
			: CopyEditorClipboxDebugBlocks();
		var drawLimit = System.Math.Max( 1, GpuClipboxDebugMaxBlocks );
		using ( Gizmo.Scope( "Voxel GPU Clipbox", GameObject.WorldTransform ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 1.0f;
			var drawCount = 0;
			for ( var index = 0; index < count && drawCount < drawLimit; index++ )
			{
				var block = _gpuClipboxDebugScratch[index];
				if ( GpuClipboxDebugMode is VoxelGpuClipboxDebugMode.TransitionOnly or VoxelGpuClipboxDebugMode.DependencyMismatches or VoxelGpuClipboxDebugMode.SeamErrors ) continue;
				if ( !ShouldDrawClipboxDebugBlock( block ) ) continue;
				drawCount++;
				var scale = 1 << block.Lod;
				var minimum = VoxelGpuCanonicalCoordinates.WorldFromCanonicalSamples( new Vector3( VoxelGpuCanonicalCoordinates.CanonicalBlockOriginSamples( block.Coordinate, block.Lod, ChunkSize ) ), VoxelSize );
				var extent = ChunkSize * scale * VoxelSize;
				var bounds = new BBox( minimum, minimum + Vector3.One * extent );
				Gizmo.Draw.Color = GetClipboxDebugColor( block, editorPreview );
				Gizmo.Draw.LineBBox( bounds );
				if ( !GpuClipboxDebugLabels || GpuClipboxDebugMode == VoxelGpuClipboxDebugMode.Lod )
				{
					if ( GpuClipboxDebugMode == VoxelGpuClipboxDebugMode.Lod ) Gizmo.Draw.Text( $"L{block.Lod}", new Transform( bounds.Center + Vector3.Up * extent * 0.05f ) );
					continue;
				}
				var label = $"L{block.Lod} slot={block.StableSlotId} mask=0x{block.TransitionFaceMask:X2} {block.State}";
				if ( GpuClipboxDebugMode == VoxelGpuClipboxDebugMode.Full ) label += $"\ncoord={block.Coordinate} gen={block.Generation} mesh=V{block.VertexOffset}+{block.VertexCapacity}/I{block.IndexOffset}+{block.IndexCapacity} idx={block.IndexCount}";
				if ( editorPreview ) label += "\neditor preview: not resident";
				Gizmo.Draw.Text( label, new Transform( bounds.Center + Vector3.Up * extent * 0.05f ) );
			}
			if ( ShouldDrawTransitionDebug() )
			{
				var transitionCount = _gpuTerrainBackend is not null
					? _gpuTerrainBackend.CopyClipboxDebugTransitions( _gpuClipboxTransitionDebugScratch )
					: CopyEditorClipboxDebugTransitions();
				var transitionDrawCount = System.Math.Min( transitionCount, System.Math.Max( 1, GpuClipboxDebugMaxTransitions ) );
				for ( var index = 0; index < transitionDrawCount; index++ )
				{
					var transition = _gpuClipboxTransitionDebugScratch[index];
					var scale = 1 << transition.FineLod;
					var minimum = VoxelGpuCanonicalCoordinates.WorldFromCanonicalSamples( new Vector3( VoxelGpuCanonicalCoordinates.CanonicalBlockOriginSamples( transition.FineCoordinate, transition.FineLod, ChunkSize ) ), VoxelSize );
					var extent = ChunkSize * scale * VoxelSize;
					var thickness = System.MathF.Max( VoxelSize, extent * 0.025f );
					var bounds = GetTransitionDebugBounds( minimum, extent, thickness, transition.Face );
					Gizmo.Draw.Color = GetTransitionDebugColor( transition );
					Gizmo.Draw.LineBBox( bounds );
					if ( GpuClipboxDebugLabels )
					{
						var label = $"T{transition.FineLod}->{transition.CoarseLod} {transition.Face} owner={transition.StableSlotId} gen={transition.FineGeneration}/{transition.CoarseGeneration} dep={transition.DependenciesValid}\nmesh=V{transition.VertexOffset}+{transition.VertexCapacity}/I{transition.IndexOffset}+{transition.IndexCapacity} idx={transition.IndexCount}";
						if ( editorPreview ) label += "\neditor preview: not resident";
						Gizmo.Draw.Text( label, new Transform( bounds.Center ) );
					}
				}
			}
		}
	}

	private float EvaluateProceduralDistance( Vector3 canonicalSample )
	{
		if ( VisualBackend != VoxelVisualBackendMode.GpuPersistentFixedLod ) return System.Math.Clamp( canonicalSample.z, -SdfClampDistance, SdfClampDistance );
		var surfaceHeight = SimplexBaseHeight + TerrainSimplexNoise( new Vector2( canonicalSample.x, canonicalSample.y ) * SimplexFrequency, SimplexSeed ) * SimplexAmplitude;
		return System.Math.Clamp( canonicalSample.z - surfaceHeight, -SdfClampDistance, SdfClampDistance );
	}

	private static float TerrainSimplexNoise( Vector2 point, int seed )
	{
		const float skewFactor = 0.3660254038f;
		const float unskewFactor = 0.2113248654f;
		var skewed = (point.x + point.y) * skewFactor;
		var cellX = (int)System.MathF.Floor( point.x + skewed );
		var cellY = (int)System.MathF.Floor( point.y + skewed );
		var cellOffset = (cellX + cellY) * unskewFactor;
		var offset = point - (new Vector2( cellX, cellY ) - cellOffset);
		var secondCornerX = offset.x > offset.y ? 1 : 0;
		var secondCornerY = offset.x > offset.y ? 0 : 1;
		var second = offset - new Vector2( secondCornerX, secondCornerY ) + unskewFactor;
		var third = offset - 1.0f + 2.0f * unskewFactor;
		var value = 0.0f;
		var radius = 0.5f - offset.Dot( offset );
		if ( radius > 0.0f ) value += radius * radius * radius * radius * TerrainGradient( TerrainHash( cellX, cellY, seed ) & 7 ).Dot( offset );
		radius = 0.5f - second.Dot( second );
		if ( radius > 0.0f ) value += radius * radius * radius * radius * TerrainGradient( TerrainHash( cellX + secondCornerX, cellY + secondCornerY, seed ) & 7 ).Dot( second );
		radius = 0.5f - third.Dot( third );
		if ( radius > 0.0f ) value += radius * radius * radius * radius * TerrainGradient( TerrainHash( cellX + 1, cellY + 1, seed ) & 7 ).Dot( third );
		return 70.0f * value;
	}

	private static int TerrainHash( int x, int y, int seed )
	{
		unchecked
		{
			var value = seed + x * 374761393 + y * 668265263;
			value = (value ^ (value >> 13)) * 1274126177;
			return value ^ (value >> 16);
		}
	}

	private static Vector2 TerrainGradient( int index ) => index switch
	{
		0 => new Vector2( 1.0f, 0.0f ),
		1 => new Vector2( -1.0f, 0.0f ),
		2 => new Vector2( 0.0f, 1.0f ),
		3 => new Vector2( 0.0f, -1.0f ),
		4 => new Vector2( 0.70710677f, 0.70710677f ),
		5 => new Vector2( -0.70710677f, 0.70710677f ),
		6 => new Vector2( 0.70710677f, -0.70710677f ),
		_ => new Vector2( -0.70710677f, -0.70710677f )
	};

	private bool ShouldDrawClipboxDebugBlock( VoxelGpuClipboxDebugBlock block ) => GpuClipboxDebugMode switch
	{
		VoxelGpuClipboxDebugMode.Lod => block.IndexCount > 0,
		VoxelGpuClipboxDebugMode.TransitionMasks => block.TransitionFaceMask != 0,
		VoxelGpuClipboxDebugMode.MissingTransitions => block.TransitionFaceMask != 0,
		_ => true
	};

	private bool ShouldDrawTransitionDebug() => GpuClipboxDebugMode is VoxelGpuClipboxDebugMode.Full or VoxelGpuClipboxDebugMode.TransitionOnly or VoxelGpuClipboxDebugMode.MissingTransitions or VoxelGpuClipboxDebugMode.DependencyMismatches or VoxelGpuClipboxDebugMode.Revision or VoxelGpuClipboxDebugMode.Wireframe or VoxelGpuClipboxDebugMode.SeamErrors;

	private Color GetTransitionDebugColor( VoxelGpuClipboxDebugTransition transition )
	{
		if ( GpuClipboxDebugMode == VoxelGpuClipboxDebugMode.DependencyMismatches && !transition.DependenciesValid ) return Color.Orange;
		if ( GpuClipboxDebugMode is VoxelGpuClipboxDebugMode.MissingTransitions or VoxelGpuClipboxDebugMode.SeamErrors )
			return transition.State == VoxelGpuDebugBlockState.Resident && transition.IndexCount > 0 ? Color.Green : Color.Red;
		return transition.State switch
		{
			VoxelGpuDebugBlockState.Pending => Color.Yellow,
			VoxelGpuDebugBlockState.Deferred => Color.Red,
			_ => Color.White
		};
	}

	private bool TryPrepareClipboxDebugSnapshot( out bool editorPreview )
	{
		editorPreview = false;
		if ( _gpuTerrainBackend is not null ) return _gpuTerrainBackend.UsesRegularClipbox;
		if ( Scene?.IsEditor != true || VisualBackend != VoxelVisualBackendMode.GpuPersistentFixedLod || GpuTerrainLodPolicy != VoxelGpuTerrainLodPolicy.RegularClipbox ) return false;

		var configuration = GpuClipboxConfig;
		if ( _editorClipboxDebugPlanner is null || !_editorClipboxDebugConfiguration.Equals( configuration ) )
		{
			_editorClipboxDebugConfiguration = configuration;
			_editorClipboxDebugPlanner = new VoxelClipboxRuntimePlanner( configuration );
			_editorClipboxDebugObserver = default;
		}

		var observer = GetGpuObserverCanonicalSample();
		if ( !_editorClipboxDebugPlanner.HasCurrentPlan || observer != _editorClipboxDebugObserver )
		{
			_editorClipboxDebugPlanner.Update( observer );
			_editorClipboxDebugPlanner.Commit();
			_editorClipboxDebugObserver = observer;
		}

		editorPreview = true;
		return true;
	}

	private int CopyEditorClipboxDebugBlocks()
	{
		_gpuClipboxDebugScratch.Clear();
		if ( _editorClipboxDebugPlanner is null ) return 0;
		foreach ( var assignment in _editorClipboxDebugPlanner.DesiredSlots )
		{
			if ( !assignment.Active ) continue;
			_gpuClipboxDebugScratch.Add( new VoxelGpuClipboxDebugBlock(
				assignment.Coordinate,
				assignment.Lod,
				assignment.Key.TransitionFaceMask,
				assignment.StableSlotId,
				VoxelGpuDebugBlockState.Missing,
				0,
				0,
				0,
				0,
				0,
				0,
				Vector3.Zero,
				Vector3.Zero,
				Vector3.Zero ) );
		}
		return _gpuClipboxDebugScratch.Count;
	}

	private int CopyEditorClipboxDebugTransitions()
	{
		_gpuClipboxTransitionDebugScratch.Clear();
		if ( _editorClipboxDebugPlanner is null ) return 0;
		foreach ( var assignment in _editorClipboxDebugPlanner.DesiredTransitions )
		{
			if ( !assignment.Active ) continue;
			_gpuClipboxTransitionDebugScratch.Add( new VoxelGpuClipboxDebugTransition(
				assignment.FineCoordinate,
				assignment.CoarseCoordinate,
				assignment.FineLevel,
				assignment.CoarseLevel,
				assignment.Face,
				assignment.StableSlotId,
				VoxelGpuDebugBlockState.Missing,
				0,
				0,
				true,
				0,
				0,
				0,
				0,
				0 ) );
		}
		return _gpuClipboxTransitionDebugScratch.Count;
	}

	private static BBox GetTransitionDebugBounds( Vector3 minimum, float extent, float thickness, VoxelClipboxFaceDirection face ) => face switch
	{
		VoxelClipboxFaceDirection.NegativeX => new BBox( new Vector3( minimum.x - thickness, minimum.y, minimum.z ), new Vector3( minimum.x, minimum.y + extent, minimum.z + extent ) ),
		VoxelClipboxFaceDirection.PositiveX => new BBox( new Vector3( minimum.x + extent, minimum.y, minimum.z ), new Vector3( minimum.x + extent + thickness, minimum.y + extent, minimum.z + extent ) ),
		VoxelClipboxFaceDirection.NegativeY => new BBox( new Vector3( minimum.x, minimum.y - thickness, minimum.z ), new Vector3( minimum.x + extent, minimum.y, minimum.z + extent ) ),
		VoxelClipboxFaceDirection.PositiveY => new BBox( new Vector3( minimum.x, minimum.y + extent, minimum.z ), new Vector3( minimum.x + extent, minimum.y + extent + thickness, minimum.z + extent ) ),
		VoxelClipboxFaceDirection.NegativeZ => new BBox( new Vector3( minimum.x, minimum.y, minimum.z - thickness ), new Vector3( minimum.x + extent, minimum.y + extent, minimum.z ) ),
		VoxelClipboxFaceDirection.PositiveZ => new BBox( new Vector3( minimum.x, minimum.y, minimum.z + extent ), new Vector3( minimum.x + extent, minimum.y + extent, minimum.z + extent + thickness ) ),
		_ => new BBox( minimum, minimum + Vector3.One * extent )
	};

	private static Color GetClipboxDebugColor( VoxelGpuClipboxDebugBlock block, bool editorPreview )
	{
		if ( block.State == VoxelGpuDebugBlockState.Missing && !editorPreview ) return Color.White;
		return block.State switch
		{
			VoxelGpuDebugBlockState.Pending => Color.Yellow,
			VoxelGpuDebugBlockState.Deferred => Color.Red,
			_ => block.Lod switch
			{
				0 => Color.Cyan,
				1 => Color.Green,
				2 => Color.Blue,
				3 => Color.Magenta,
				_ => Color.White
			}
		};
	}

	private int ResolveGpuClipboxLevelCount()
	{
		if ( !GpuClipboxMatchChunkRadius ) return System.Math.Clamp( GpuClipboxLevelCount, 1, VoxelClipboxConfig.MaximumLevelCount );

		var coverageRadius = GpuClipboxBlocksPerAxis / 2;
		var levelCount = 1;
		while ( coverageRadius < ChunkRadius && levelCount < VoxelClipboxConfig.MaximumLevelCount )
		{
			coverageRadius = checked( coverageRadius * 2 );
			levelCount++;
		}

		return levelCount;
	}

	private void ResolveGpuPoolCapacity()
	{
		if ( !GpuAutoScalePoolToChunkRadius )
		{
			EffectiveGpuVertexPoolCapacity = GpuVertexPoolCapacity;
			EffectiveGpuIndexPoolCapacity = GpuIndexPoolCapacity;
			return;
		}

		var targetRadius = System.Math.Min( GpuPoolMaximumRadius, System.Math.Max( 1, ChunkRadius ) );
		var targetChunks = (long)targetRadius * 2L * targetRadius * 2L;
		var chunkScaleNumerator = (long)ChunkSize * ChunkSize;
		var chunkScaleDenominator = (long)GpuPoolReferenceRadius * GpuPoolReferenceRadius;
		var vertexNumerator = checked( GpuReferenceVertexCount * targetChunks * (100L + GpuPoolHeadroomPercent) * chunkScaleNumerator );
		var indexNumerator = checked( GpuReferenceIndexCount * targetChunks * (100L + GpuPoolHeadroomPercent) * chunkScaleNumerator );
		var denominator = checked( GpuReferenceResidentCount * 100L * chunkScaleDenominator );
		var scaledVertices = checked( (int)System.Math.Min( MaximumGpuVertexPoolCapacity, (vertexNumerator + denominator - 1) / denominator ) );
		var scaledIndices = checked( (int)System.Math.Min( MaximumGpuIndexPoolCapacity, (indexNumerator + denominator - 1) / denominator ) );
		EffectiveGpuVertexPoolCapacity = System.Math.Clamp( System.Math.Max( DefaultGpuVertexPoolCapacity, scaledVertices ), 65536, MaximumGpuVertexPoolCapacity );
		EffectiveGpuIndexPoolCapacity = System.Math.Clamp( System.Math.Max( DefaultGpuIndexPoolCapacity, scaledIndices ), 196608, MaximumGpuIndexPoolCapacity );
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

		var canonicalSample = new Vector3( worldSample.x, worldSample.y, worldSample.z );
		return _editJournal.EvaluateDistance( canonicalSample, EvaluateProceduralDistance( canonicalSample ) );
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
		state.ReadyResult = null;
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
		_coherentCollisionEditBatches.Clear();
		_collisionGenerationQueue.Clear();
		_collisionGenerationQueuedChunks.Clear();
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

	private void UpdateGpuChunkStreaming()
	{
		if ( _gpuTerrainBackend is null ) return;
		if ( GpuTerrainLodPolicy == VoxelGpuTerrainLodPolicy.RegularClipbox )
		{
			UpdateGpuClipboxStreaming();
			return;
		}
		var updateStart = System.Diagnostics.Stopwatch.GetTimestamp();
		_lastChunkStreamingInterestTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		_gpuStreamingObservers.Clear();
		PopulateStreamingObserverChunks( _gpuStreamingObservers );
		var observersChanged = !_gpuStreamingObserversInitialized || !AreObserverChunksEqual( _gpuStreamingObservers );
		// The observer is already quantized to chunk coordinates, so unchanged
		// observers need no desired-set work. When a boundary is crossed, update
		// in this frame rather than introducing a fixed streaming polling delay.
		if ( !observersChanged ) return;
		_gpuStreamingObserversInitialized = true;
		_gpuPreviousStreamingObservers.Clear();
		_gpuPreviousStreamingObservers.AddRange( _gpuStreamingObservers );
		PopulateDesiredChunkCoordinates( _gpuStreamingObservers, _gpuDesiredScratch );
		var desiredChanged = !_desiredChunkCoordinates.SetEquals( _gpuDesiredScratch );
		if ( !desiredChanged ) return;
		var previousDesiredCount = _desiredChunkCoordinates.Count;
		_desiredChunkCoordinates.Clear();
		_desiredChunkCoordinates.UnionWith( _gpuDesiredScratch );
		_gpuOrderedScratch.Clear();
		_gpuOrderedScratch.AddRange( _gpuDesiredScratch );
		_gpuOrderedScratch.Sort( (left, right) => GetStreamingPriority( left, _gpuStreamingObservers ).CompareTo( GetStreamingPriority( right, _gpuStreamingObservers ) ) );
		_gpuTerrainBackend.UpdateDesiredSet( _gpuOrderedScratch, GpuTerrainRuleVersion );
		if ( desiredChanged && ( _lastGpuStreamingLogTimestamp == 0 || System.Diagnostics.Stopwatch.GetElapsedTime( _lastGpuStreamingLogTimestamp ).TotalSeconds >= 5.0 ) )
		{
			_lastGpuStreamingLogTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
			Log.Info( $"Voxel GPU fixed-LOD streaming updated: observers={_gpuStreamingObservers.Count:N0}, desired={_gpuDesiredScratch.Count:N0}, previous={previousDesiredCount:N0}, boundedResidentCapacity=backend." );
		}
		var updateMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( updateStart ).TotalMilliseconds;
		if ( updateMilliseconds >= 8.0 && System.Threading.Interlocked.Increment( ref _slowGpuStreamingLogCount ) <= 32 )
			Log.Info( $"Voxel GPU streaming hitch trace: {updateMilliseconds:F2}ms, observers={_gpuStreamingObservers.Count:N0}, desired={_gpuDesiredScratch.Count:N0}, changed={desiredChanged}." );
	}

	private void UpdateGpuClipboxStreaming()
	{
		var observer = GetGpuObserverCanonicalSample();
		var observerBaseBlock = GetStableGpuClipboxObserverBaseBlock( observer );
		// A clipbox plan changes only when its quantized base block changes. Tracking
		// raw voxel samples restarted the same pending plan every frame while a
		// player moved inside one block, duplicating seam generations and readbacks.
		if ( _gpuClipboxObserverInitialized && observerBaseBlock == _gpuClipboxObserverBaseBlock ) return;
		_gpuClipboxObserverInitialized = true;
		_gpuClipboxObserverBaseBlock = observerBaseBlock;
		var updateStart = System.Diagnostics.Stopwatch.GetTimestamp();
		if ( !_gpuTerrainBackend.QueueClipboxObserver( observer ) ) return;
		_gpuDesiredBlockCount = _gpuTerrainBackend.DesiredBlockCount;
		if ( _lastGpuStreamingLogTimestamp == 0 || System.Diagnostics.Stopwatch.GetElapsedTime( _lastGpuStreamingLogTimestamp ).TotalSeconds >= 5.0 )
		{
			_lastGpuStreamingLogTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
			Log.Info( $"Voxel GPU regular clipbox streaming updated: observer={observer}, active={_gpuDesiredBlockCount:N0}, policy={GpuTerrainLodPolicy}." );
		}
		var updateMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( updateStart ).TotalMilliseconds;
		if ( updateMilliseconds >= 8.0 && System.Threading.Interlocked.Increment( ref _slowGpuStreamingLogCount ) <= 32 )
			Log.Info( $"Voxel GPU clipbox streaming hitch trace: {updateMilliseconds:F2}ms, observer={observer}, active={_gpuDesiredBlockCount:N0}." );
	}

	private Vector3Int GetStableGpuClipboxObserverBaseBlock( Vector3Int observer )
	{
		if ( !_gpuClipboxObserverInitialized ) return VoxelClipboxCoordinates.GetObserverBaseBlock( observer );
		return new Vector3Int(
			GetStableGpuClipboxObserverAxis( observer.x, _gpuClipboxObserverBaseBlock.x ),
			GetStableGpuClipboxObserverAxis( observer.y, _gpuClipboxObserverBaseBlock.y ),
			GetStableGpuClipboxObserverAxis( observer.z, _gpuClipboxObserverBaseBlock.z ) );
	}

	private static int GetStableGpuClipboxObserverAxis( int sample, int currentBlock )
	{
		const int hysteresisSamples = 1;
		var minimum = (long)currentBlock * VoxelClipboxConfig.CellsPerBlock;
		var maximumExclusive = minimum + VoxelClipboxConfig.CellsPerBlock;
		if ( sample >= minimum - hysteresisSamples && sample < maximumExclusive + hysteresisSamples ) return currentBlock;
		return VoxelClipboxCoordinates.FloorDiv( sample, VoxelClipboxConfig.CellsPerBlock );
	}

	private Vector3Int GetGpuObserverCanonicalSample()
	{
		foreach ( var controller in Scene.GetAllComponents<PlayerController>() ) return GetCanonicalSample( controller.WorldPosition );
		return Scene.Camera is null ? Vector3Int.Zero : GetCanonicalSample( Scene.Camera.WorldPosition );
	}

	private Vector3Int GetCanonicalSample( Vector3 worldPosition )
	{
		var localVoxelPosition = GameObject.WorldTransform.PointToLocal( worldPosition ) / VoxelSize;
		return new Vector3Int(
			(int)System.MathF.Floor( localVoxelPosition.x ),
			(int)System.MathF.Floor( localVoxelPosition.y ),
			(int)System.MathF.Floor( localVoxelPosition.z ) );
	}

	private void PopulateStreamingObserverChunks( List<Vector3Int> destination )
	{
		destination.Clear();
		foreach ( var controller in Scene.GetAllComponents<PlayerController>() )
		{
			var coordinate = GetCollisionObserverChunk( controller.WorldPosition );
			if ( !destination.Contains( coordinate ) ) destination.Add( coordinate );
		}

		if ( destination.Count == 0 && Scene.Camera is not null )
			destination.Add( GetCollisionObserverChunk( Scene.Camera.WorldPosition ) );
		if ( destination.Count == 0 ) destination.Add( Vector3Int.Zero );
	}

	private bool AreObserverChunksEqual( List<Vector3Int> observers )
	{
		if ( observers.Count != _gpuPreviousStreamingObservers.Count ) return false;
		for ( var index = 0; index < observers.Count; index++ )
		{
			if ( observers[index] != _gpuPreviousStreamingObservers[index] ) return false;
		}
		return true;
	}

	private void RefreshChunkStreamingInterests()
	{
		_lastChunkStreamingInterestTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		var observers = GetStreamingObserverChunks();
		var desired = new HashSet<Vector3Int>();
		PopulateDesiredChunkCoordinates( observers, desired );
		if ( _desiredChunkCoordinates.SetEquals( desired ) )
		{
			if ( _chunkGameObjects.Count > desired.Count )
			{
				var staleChunkObjects = new List<Vector3Int>();
				foreach ( var coordinate in _chunkGameObjects.Keys )
					if ( !desired.Contains( coordinate ) ) staleChunkObjects.Add( coordinate );
				foreach ( var coordinate in staleChunkObjects ) DestroyChunkGameObject( coordinate );
			}
			return;
		}
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

		PumpCollisionGenerationQueue();
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
				_collisionDesiredChunks.Add( coordinate );
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
			if ( !_chunks.ContainsKey( coordinate ) )
			{
				QueueCollisionChunkGeneration( coordinate );
				continue;
			}
			if ( _chunkColliders.TryGetValue( coordinate, out var state ) )
			{
				if ( !Application.IsDedicatedServer && TryStagePublishedVisualCollider( state ) )
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

	private void QueueCollisionChunkGeneration( Vector3Int coordinate )
	{
		if ( _collisionGenerationQueuedChunks.Add( coordinate ) ) _collisionGenerationQueue.Enqueue( coordinate );
	}

	private void PumpCollisionGenerationQueue()
	{
		var start = System.Diagnostics.Stopwatch.GetTimestamp();
		var generated = 0;
		while ( _collisionGenerationQueue.TryDequeue( out var coordinate ) )
		{
			_collisionGenerationQueuedChunks.Remove( coordinate );
			if ( !_collisionDesiredChunks.Contains( coordinate ) || _chunks.ContainsKey( coordinate ) ) continue;
			GenerateChunk( coordinate );
			QueueCollisionBuild( coordinate );
			generated++;
			if ( generated >= 1 || System.Diagnostics.Stopwatch.GetElapsedTime( start ).TotalMilliseconds >= CpuMainThreadBudgetMilliseconds ) break;
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
		state.ReadyResult = null;
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
			if ( TryStagePublishedVisualCollider( state ) ) return;
		}
		if ( _cpuChunkStates.TryGetValue( coordinate, out var visualState ) &&
			(visualState.Task is not null || visualState.ReadyResult.HasValue || visualState.CompletedGeneration < visualState.DesiredGeneration) )
		{
			return;
		}
		if ( state.Task is not null || state.ReadyResult.HasValue )
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
		var publicationLimit = System.Math.Clamp( CollisionBuildsPerFrame, 1, MaximumCollisionBuildsPerFrame );

		// Worker completion is cheap to harvest. Physics model creation is not, so keep
		// completed meshes staged until the bounded publication pass below.
		foreach ( var state in _chunkColliders.Values )
		{
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
				if ( state.Dirty && _collisionDesiredChunks.Contains( state.Coordinate ) ) QueueCollisionBuild( state.Coordinate );
				continue;
			}
			if ( task.IsCanceled )
			{
				if ( state.Dirty && _collisionDesiredChunks.Contains( state.Coordinate ) ) QueueCollisionBuild( state.Coordinate );
				continue;
			}

			var result = task.Result;
			if ( result.Generation != state.DesiredGeneration || result.Generation <= state.CompletedGeneration )
			{
				if ( state.Dirty && _collisionDesiredChunks.Contains( state.Coordinate ) ) QueueCollisionBuild( state.Coordinate );
				continue;
			}
			if ( !_collisionDesiredChunks.Contains( state.Coordinate ) )
			{
				continue;
			}
			state.ReadyResult = result;
		}

		var mainThreadStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var uploads = UploadCoherentCollisionEditBatches();
		foreach ( var state in _chunkColliders.Values )
		{
			if ( uploads >= publicationLimit ||
				(uploads > 0 && System.Diagnostics.Stopwatch.GetElapsedTime( mainThreadStart ).TotalMilliseconds >= CpuMainThreadBudgetMilliseconds) )
			{
				break;
			}
			if ( !state.ReadyResult.HasValue ) continue;
			if ( IsInCoherentCollisionEditBatch( state.Coordinate ) ) continue;

			var result = state.ReadyResult.Value;
			if ( !_collisionDesiredChunks.Contains( state.Coordinate ) || result.Generation != state.DesiredGeneration ||
				result.Generation <= state.CompletedGeneration )
			{
				state.ReadyResult = null;
				if ( state.Dirty && _collisionDesiredChunks.Contains( state.Coordinate ) ) QueueCollisionBuild( state.Coordinate );
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

			if ( !_chunkColliders.TryGetValue( coordinate, out var state ) || state.Task is not null || state.ReadyResult.HasValue )
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

	private void MergeCoherentCollisionEditBatch( List<Vector3Int> changedChunks )
	{
		var mergedBatch = new HashSet<Vector3Int>();
		foreach ( var coordinate in changedChunks )
		{
			if ( _cpuChunkStates.ContainsKey( coordinate ) && _chunkColliders.ContainsKey( coordinate ) ) mergedBatch.Add( coordinate );
		}
		if ( mergedBatch.Count == 0 ) return;

		for ( var index = _coherentCollisionEditBatches.Count - 1; index >= 0; index-- )
		{
			var existingBatch = _coherentCollisionEditBatches[index];
			if ( !existingBatch.Overlaps( mergedBatch ) ) continue;
			mergedBatch.UnionWith( existingBatch );
			_coherentCollisionEditBatches.RemoveAt( index );
		}
		_coherentCollisionEditBatches.Add( mergedBatch );
	}

	private int UploadCoherentCollisionEditBatches()
	{
		var uploads = 0;
		for ( var batchIndex = _coherentCollisionEditBatches.Count - 1; batchIndex >= 0; batchIndex-- )
		{
			var batch = _coherentCollisionEditBatches[batchIndex];
			var ready = true;
			foreach ( var coordinate in batch )
			{
				if ( !_chunkColliders.TryGetValue( coordinate, out var state ) ||
					(state.CompletedGeneration < state.DesiredGeneration &&
					(!state.ReadyResult.HasValue || state.ReadyResult.Value.Generation != state.DesiredGeneration)) )
				{
					ready = false;
					break;
				}
			}
			if ( !ready ) continue;

			foreach ( var coordinate in batch )
			{
				var state = _chunkColliders[coordinate];
				if ( !state.ReadyResult.HasValue ) continue;
				UploadChunkCollider( state, state.ReadyResult.Value );
				uploads++;
			}
			_coherentCollisionEditBatches.RemoveAt( batchIndex );
		}
		return uploads;
	}

	private bool IsInCoherentCollisionEditBatch( Vector3Int coordinate )
	{
		foreach ( var batch in _coherentCollisionEditBatches )
		{
			if ( batch.Contains( coordinate ) ) return true;
		}
		return false;
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
			StageChunkCollider( collisionState, result.Mesh, result.Generation, result.SnapshotWaitTime, result.SnapshotTime, result.MeshingTime );
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

	private bool TryStagePublishedVisualCollider( ChunkCollisionState collisionState )
	{
		if ( !_cpuChunkStates.TryGetValue( collisionState.Coordinate, out var visualState ) ||
			visualState.PublishedMeshData is null || visualState.CompletedGeneration < visualState.DesiredGeneration ||
			collisionState.CompletedGeneration >= visualState.CompletedGeneration )
		{
			return false;
		}

		collisionState.DesiredGeneration = visualState.CompletedGeneration;
		StageChunkCollider( collisionState, visualState.PublishedMeshData, visualState.CompletedGeneration,
			visualState.SnapshotWaitTime, visualState.SnapshotTime, visualState.MeshingTime );
		return true;
	}

	private static void StageChunkCollider( ChunkCollisionState state, VoxelMeshData meshData, int generation,
		System.TimeSpan snapshotWaitTime, System.TimeSpan snapshotTime, System.TimeSpan meshingTime )
	{
		if ( generation < state.DesiredGeneration || generation <= state.CompletedGeneration ) return;
		state.DesiredGeneration = generation;
		state.Dirty = true;
		state.Queued = false;
		state.ReadyResult = new CollisionBuildResult( generation, meshData, snapshotWaitTime, snapshotTime, meshingTime );
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
		return new WorldConfiguration( ChunkSize, ChunkRadius, VoxelSize, SdfClampDistance, CaptureTerrainConfiguration(), TerrainMaterial, VisualBackend, GpuTerrainLodPolicy, GpuClipboxBlocksPerAxis, GpuClipboxLevelCount, GpuClipboxMatchChunkRadius, GpuAutoScalePoolToChunkRadius, GpuVertexPoolCapacity, GpuIndexPoolCapacity, GpuTerrainRuleVersion );
	}

	private TerrainConfiguration CaptureTerrainConfiguration() => new( SimplexFrequency, SimplexAmplitude, SimplexBaseHeight, SimplexSeed );

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
	private readonly record struct TerrainConfiguration( float Frequency, float Amplitude, float BaseHeight, int Seed );
	private readonly record struct WorldConfiguration( int ChunkSize, int ChunkRadius, float VoxelSize, float SdfClampDistance, TerrainConfiguration Terrain, Material TerrainMaterial, VoxelVisualBackendMode VisualBackend, VoxelGpuTerrainLodPolicy GpuTerrainLodPolicy, int GpuClipboxBlocksPerAxis, int GpuClipboxLevelCount, bool GpuClipboxMatchChunkRadius, bool GpuAutoScalePoolToChunkRadius, int GpuVertexPoolCapacity, int GpuIndexPoolCapacity, int GpuTerrainRuleVersion );

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
		public CollisionBuildResult? ReadyResult { get; set; }
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
