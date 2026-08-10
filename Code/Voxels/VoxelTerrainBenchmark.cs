public enum VoxelTerrainBenchmarkMode
{
	Full,
	GpuOnly,
	CpuOnly
}

public sealed class VoxelTerrainBenchmark : Component
{
	private const string ReportDirectory = "voxel-terrain-benchmarks";
	private const string HistoryCsvPath = ReportDirectory + "/history.csv";
	private const string HistoryJsonLinesPath = ReportDirectory + "/history.jsonl";
	private const string LatestMarkdownPath = ReportDirectory + "/latest-report.md";
	private const string LatestJsonPath = ReportDirectory + "/latest-report.json";
	private const string DashboardPath = ReportDirectory + "/dashboard.html";
	private const int SuiteVersion = 38;
	private const int InfinityPathSampleCount = 1024;
	private const int RealtimeSurfaceEditCount = 80;
	private const float HighSpeedCollisionTraversalSpeed = 20000.0f;
	private static string[] AllRequiredScenarios => new[]
	{
		"cold_generation",
		"collision_backlog_frame_budget",
		"collision_proximity_edit_filter",
		"phase4_planner_counts",
		"phase4_planner_reference_equivalence",
		"phase4_negative_coordinates",
		"phase4_vertical_movement",
		"phase4_regular_coverage",
		"phase4_no_lod_overlap",
		"phase4_neighbor_difference",
		"phase4_four_level_b4_movement",
		"phase4_four_level_b8_movement",
		"phase4_four_level_stationary_soak",
		"phase4_transition_ownership",
		"phase5_sparse_edit_contract",
		"phase5_deterministic_invalidation",
		"phase5_stale_edit_generations",
		"phase5_edit_eviction_reentry",
		"phase5_voxel_brush_raycast",
		"phase5_gpu_edit_revision_binding",
		"phase5_incremental_edit_replay",
		"phase4_transition_all_512_cases",
		"phase4_transition_six_orientations",
		"phase4_transition_plane",
		"phase4_transition_sphere",
		"phase4_transition_cave",
		"phase4_transition_tangent_surface",
		"phase4_transition_watertight_edges",
		"phase4_transition_no_duplicate_faces",
		"phase4_indirect_1_to_1024",
		"phase4_indirect_boundary_49",
		"phase4_depth_opaque_parity",
		"phase4_command_list_active_range",
		"phase4_regular_b4_l2_stationary",
		"phase4_regular_b4_l4_stationary",
		"phase4_regular_radius64_match",
		"gpu_lod5_transition_ownership",
		"gpu_realtime_surface_edits_20hz",
		"gpu_accumulated_surface_edits_20hz",
		"gpu_aimed_brush_edits_20hz",
		"gpu_cpu_collision_rebuild_cost",
		"gpu_transvoxel_regular_proof",
		"gpu_persistent_static_set",
		"gpu_camera_sweep_generation",
		"gpu_production_render_integration",
		"gpu_player_infinity_streaming",
		"gpu_player_line_streaming",
		"gpu_player_diagonal_streaming",
		"gpu_player_clipbox_oscillation",
		"high_speed_collision_streaming",
		"gpu_allocator_churn",
		"gpu_replacement_failure",
		"gpu_pool_exhaustion",
		"gpu_return_origin_stability",
		"gpu_async_readback_saturation",
		"gpu_resource_recreation",
		"gpu_dedicated_server_startup",
		"live_chunk_radius_reconfiguration",
		"player_infinity_streaming",
		"player_line_streaming",
		"player_diagonal_streaming",
		"chunk_seam_edit_coherence",
		"varied_edits",
		"bulk_edit",
		"sustained_world_sweep_and_depth_dig_20hz",
		"sustained_world_spiral_place_20hz",
		"player_post_edit_line_streaming",
		"phase4_regular_b8_l4_stationary"
	};
	private static ComparisonMetric[] ComparisonMetrics => new ComparisonMetric[]
	{
		new( "avg_fps", true ),
		new( "one_percent_low_fps", true ),
		new( "frame_p95_ms", true ),
		new( "frame_max_ms", false ),
		new( "gpu_p95_ms", true ),
		new( "edit_call_p95_ms", true ),
		new( "post_edit_settle_ms", false ),
		new( "allocated_bytes", true ),
		new( "visual_batch_ms", true ),
		new( "worker_mesh_ms", true ),
		new( "upload_ms", true ),
		new( "collision_worker_mesh_ms", true ),
		new( "collision_model_build_ms", true ),
		new( "collision_publication_ms", true ),
		new( "stream_chunk_ready_p95_ms", true ),
		new( "stream_batch_p95_ms", true )
	};

	private readonly List<ScenarioResult> _results = new();
	private readonly List<EditCommand> _variedEdits = new();
	private readonly Dictionary<string, ScenarioComparison> _comparisons = new();
	private VoxelManager _manager;
	private bool _initializationComplete;
	private int _initializationAttempts;
	private FrameSampler _sampler;
	private BenchmarkPhase _phase;
	private long _phaseStartTimestamp;
	private long _nextEditTimestamp;
	private string _runId;
	private int _warmupFramesRemaining;
	private int _editIndex;
	private bool _runRequested;
	private BenchmarkPhase _phaseAfterReset;
	private bool _originalCaptureCallCounts;
	private bool _callCountSettingCaptured;
	private GameObject _playerSafetyFixture;
	private readonly List<BenchmarkGravityState> _benchmarkGravityStates = new();
	private bool _holdBenchmarkPlayersAtOrigin;
	private bool _isReproductionRun;
	private string _reproductionOfRunId;
	private Dictionary<string, PreviousScenarioMeasurement> _reproductionBaselines;
	private Dictionary<string, HashSet<string>> _reproductionTargets;
	private readonly Vector3[] _infinityPathPositions = new Vector3[InfinityPathSampleCount + 1];
	private readonly float[] _infinityPathDistances = new float[InfinityPathSampleCount + 1];
	private PlayerController _traversalPlayer;
	private Vector3 _traversalStartWorldPosition;
	private Vector3 _traversalStartLocalPosition;
	private TraversalPath _traversalPath;
	private float _traversalLoopLength;
	private float _traversalDistanceTravelled;
	private float _activeTraversalSpeed;
	private Vector3 _traversalLastWorldPosition;
	private bool _requireTraversalCollision;
	private BenchmarkPhase _phaseAfterHighSpeedCollisionTraversal;
	private int _traversalInitialCachedChunkCount;
	private int _originalChunkRadius;
	private int _liveConfigurationChunkRadius;
	private VoxelVisualBackendMode _originalVisualBackend;
	private int _originalGpuTerrainRuleVersion;
	private VoxelGpuTerrainLodPolicy _originalGpuTerrainLodPolicy;
	private int _originalGpuClipboxBlocksPerAxis;
	private int _originalGpuClipboxLevelCount;
	private bool _originalGpuClipboxMatchChunkRadius;
	private bool _worldSettingsCaptured;
	private int _gpuLifecycleScenarioIndex;
	private VoxelGpuPhase2BProofResult _pendingGpuPhase2BProof;
	private VoxelGpuPhase3BProofResult _pendingGpuPhase3BProof;
	private VoxelClipboxPlannerProofReport _pendingClipboxPlannerProof;
	private VoxelClipboxReferenceEquivalenceRunner _referenceEquivalenceRunner;
	private int _phase4PlannerScenarioIndex;
	private int _phase5EditScenarioIndex;
	private VoxelEditProofReport _pendingPhase5EditProof;
	private VoxelTransvoxelTransitionProofResult _pendingTransitionProof;
	private VoxelGpuTransitionCaseProofResult _pendingTransitionGpuProof;
	private int _phase4TransitionScenarioIndex;
	private VoxelGpuIndirectRenderProofReport _pendingIndirectRenderProof;
	private int _phase4IndirectScenarioIndex;
	private int _phase4RegularScenarioIndex;
	private long _phase4RegularSoakStartTimestamp;
	private VoxelGpuPhase2BProofResult _pendingRegularClipboxProof;
	private VoxelGpuClipboxSeamProofReport? _pendingLod5OwnershipProof;
	private CameraComponent _cameraSweepCamera;
	private Rotation _cameraSweepOriginalRotation;
	private VoxelCallCountSnapshot _collisionProximityBaseline;
	private VoxelCallCountSnapshot _gpuCollisionBaseline;
	private VoxelCallCountSnapshot _gpuAimedBrushBaseline;
	private static readonly string[] GpuLifecycleScenarioNames =
	{
		"gpu_allocator_churn",
		"gpu_replacement_failure",
		"gpu_pool_exhaustion",
		"gpu_return_origin_stability"
	};
	private static readonly string[] Phase4PlannerScenarioNames =
	{
		"phase4_planner_counts",
		"phase4_planner_reference_equivalence",
		"phase4_negative_coordinates",
		"phase4_vertical_movement",
		"phase4_regular_coverage",
		"phase4_no_lod_overlap",
		"phase4_neighbor_difference",
		"phase4_four_level_b4_movement",
		"phase4_four_level_b8_movement",
		"phase4_four_level_stationary_soak",
		"phase4_transition_ownership"
	};
	private static readonly string[] Phase4TransitionScenarioNames =
	{
		"phase4_transition_all_512_cases",
		"phase4_transition_six_orientations",
		"phase4_transition_plane",
		"phase4_transition_sphere",
		"phase4_transition_cave",
		"phase4_transition_tangent_surface",
		"phase4_transition_watertight_edges",
		"phase4_transition_no_duplicate_faces"
	};
	private static readonly string[] Phase5EditScenarioNames =
	{
		"phase5_sparse_edit_contract",
		"phase5_deterministic_invalidation",
		"phase5_stale_edit_generations",
		"phase5_edit_eviction_reentry",
		"phase5_voxel_brush_raycast",
		"phase5_gpu_edit_revision_binding",
		"phase5_incremental_edit_replay"
	};
	private static readonly string[] Phase4IndirectScenarioNames =
	{
		"phase4_indirect_1_to_1024",
		"phase4_indirect_boundary_49",
		"phase4_depth_opaque_parity",
		"phase4_command_list_active_range"
	};
	private static readonly string[] Phase4RegularScenarioNames =
	{
		"phase4_regular_b4_l2_stationary",
		"phase4_regular_b4_l4_stationary",
		"phase4_regular_radius64_match"
	};
	private static readonly int[] Phase4RegularBlocksPerAxis = { 4, 4, 8 };
	private static readonly int[] Phase4RegularLevelCounts = { 2, 4, 5 };
	private static readonly int[] Phase4RegularExpectedActiveCounts = { 120, 232, 2304 };
	private static readonly int[] Phase4RegularExpectedStableSlots = { 128, 256, 2560 };
	private const int FinalStationarySoakSeconds = 60;
	private const string FinalStationaryScenarioName = "phase4_regular_b8_l4_stationary";

	[Property, Group( "Run" )]
	public bool RunOnStart { get; set; } = true;

	[Property, Group( "Run" )]
	public VoxelTerrainBenchmarkMode Mode { get; set; } = VoxelTerrainBenchmarkMode.Full;

	private string[] SelectedRequiredScenarios => Mode switch
	{
		VoxelTerrainBenchmarkMode.GpuOnly => new[]
		{
			"phase4_planner_counts", "phase4_planner_reference_equivalence", "phase4_negative_coordinates", "phase4_vertical_movement", "phase4_regular_coverage", "phase4_no_lod_overlap", "phase4_neighbor_difference", "phase4_four_level_b4_movement", "phase4_four_level_b8_movement", "phase4_four_level_stationary_soak", "phase4_transition_ownership", "phase5_sparse_edit_contract", "phase5_deterministic_invalidation", "phase5_stale_edit_generations", "phase5_edit_eviction_reentry", "phase5_voxel_brush_raycast", "phase5_gpu_edit_revision_binding", "phase5_incremental_edit_replay", "phase4_transition_all_512_cases", "phase4_transition_six_orientations", "phase4_transition_plane", "phase4_transition_sphere", "phase4_transition_cave", "phase4_transition_tangent_surface", "phase4_transition_watertight_edges", "phase4_transition_no_duplicate_faces",
			"phase4_indirect_1_to_1024", "phase4_indirect_boundary_49", "phase4_depth_opaque_parity", "phase4_command_list_active_range", "phase4_regular_b4_l2_stationary", "phase4_regular_b4_l4_stationary", "phase4_regular_radius64_match", "gpu_lod5_transition_ownership", "gpu_realtime_surface_edits_20hz", "gpu_accumulated_surface_edits_20hz", "gpu_aimed_brush_edits_20hz", "gpu_cpu_collision_rebuild_cost",
			"gpu_persistent_static_set", "gpu_camera_sweep_generation", "gpu_production_render_integration",
			"gpu_player_infinity_streaming", "gpu_player_line_streaming", "gpu_player_diagonal_streaming",
			"gpu_player_clipbox_oscillation", "high_speed_collision_streaming",
			"gpu_allocator_churn", "gpu_replacement_failure", "gpu_pool_exhaustion", "gpu_return_origin_stability",
			"gpu_async_readback_saturation", "gpu_resource_recreation", "gpu_dedicated_server_startup", "phase4_regular_b8_l4_stationary"
		},
		VoxelTerrainBenchmarkMode.CpuOnly => new[]
		{
			"cold_generation", "collision_backlog_frame_budget", "collision_proximity_edit_filter", "phase4_planner_counts", "phase4_planner_reference_equivalence", "phase4_negative_coordinates", "phase4_vertical_movement", "phase4_regular_coverage", "phase4_no_lod_overlap", "phase4_neighbor_difference", "phase4_four_level_b4_movement", "phase4_four_level_b8_movement", "phase4_four_level_stationary_soak", "phase4_transition_ownership", "phase5_sparse_edit_contract", "phase5_deterministic_invalidation", "phase5_stale_edit_generations", "phase5_edit_eviction_reentry", "phase5_voxel_brush_raycast", "phase5_gpu_edit_revision_binding", "phase5_incremental_edit_replay", "phase4_transition_all_512_cases", "phase4_transition_six_orientations", "phase4_transition_plane", "phase4_transition_sphere", "phase4_transition_cave", "phase4_transition_tangent_surface", "phase4_transition_watertight_edges", "phase4_transition_no_duplicate_faces", "phase4_indirect_1_to_1024", "phase4_indirect_boundary_49", "phase4_depth_opaque_parity", "phase4_command_list_active_range", "live_chunk_radius_reconfiguration", "player_infinity_streaming", "player_line_streaming",
			"high_speed_collision_streaming", "player_diagonal_streaming", "chunk_seam_edit_coherence", "varied_edits", "bulk_edit",
			"sustained_world_sweep_and_depth_dig_20hz", "sustained_world_spiral_place_20hz", "player_post_edit_line_streaming"
		},
		_ => AllRequiredScenarios
	};

	public string Revision { get; private set; } = "unknown";
	public bool WorkingTreeDirty { get; private set; } = true;

	[Property, Group( "Run" ), Range( 0, 240 )]
	public int WarmupFrames { get; set; } = 30;

	[Property, Group( "Run" ), Range( 10.0f, 180.0f )]
	public float ScenarioTimeoutSeconds { get; set; } = 180.0f;

	[Property, Group( "Run" ), Range( 5.0f, 100.0f )]
	public float MajorOutlierThresholdPercent { get; set; } = 20.0f;

	[Property, Group( "Sustained Editing" ), Range( 10, 500 )]
	public int SustainedEditCount { get; set; } = 80;

	[Property, Group( "Sustained Editing" ), Range( 0.01f, 0.25f )]
	public float SustainedEditIntervalSeconds { get; set; } = 0.05f;

	[Property, Group( "Player Traversal" ), Range( 128.0f, 100000.0f )]
	public float TraversalDistance { get; set; } = 10000.0f;

	[Property, Group( "Player Traversal" ), Range( 1.0f, 10000.0f )]
	public float TraversalSpeed { get; set; } = 1000.0f;

	[Property, Group( "Player Traversal" ), Range( 1, 10 )]
	public int TraversalLoopCount { get; set; } = 1;

	public string LastReportPath { get; private set; }
	public bool IsRunning => _phase is not BenchmarkPhase.Idle and not BenchmarkPhase.Complete and not BenchmarkPhase.Failed;

	[Property, ReadOnly, Group( "Run" )]
	public string CurrentScenario => _sampler?.Name ?? _phase.ToString();

	[Property, ReadOnly, Group( "Run" )]
	public string CurrentScenarioDescription => _sampler?.Description ?? string.Empty;

	[Property, ReadOnly, Group( "Run" )]
	public double CurrentScenarioElapsedSeconds => _phaseStartTimestamp == 0 ? 0.0 : System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp ).TotalSeconds;

	[Property, ReadOnly, Group( "Run" )]
	public string Status => $"phase={_phase}; scenario={CurrentScenario}; elapsed={CurrentScenarioElapsedSeconds:F1}s; initialized={_initializationComplete}; requested={_runRequested}; completed={_results.Count}/{SelectedRequiredScenarios.Length}";

	protected override void OnValidate()
	{
		WarmupFrames = System.Math.Clamp( WarmupFrames, 0, 240 );
		ScenarioTimeoutSeconds = System.Math.Clamp( ScenarioTimeoutSeconds, 10.0f, 180.0f );
		MajorOutlierThresholdPercent = System.Math.Clamp( MajorOutlierThresholdPercent, 5.0f, 100.0f );
		SustainedEditCount = System.Math.Clamp( SustainedEditCount, 10, 500 );
		SustainedEditIntervalSeconds = System.Math.Clamp( SustainedEditIntervalSeconds, 0.01f, 0.25f );
		TraversalDistance = System.Math.Clamp( TraversalDistance, 128.0f, 100000.0f );
		TraversalSpeed = System.Math.Clamp( TraversalSpeed, 1.0f, 10000.0f );
		TraversalLoopCount = System.Math.Clamp( TraversalLoopCount, 1, 10 );
	}

	protected override void OnStart()
	{
		TryInitialize();
	}

	protected override void OnDisabled()
	{
		_initializationComplete = false;
		_manager = null;
		RestoreWorldSettings();
		RestoreCallCountSetting();
		RestorePlayerProtectionSetting();
		DestroyPlayerSafetyFixture();
	}

	protected override void OnDestroy()
	{
		RestoreWorldSettings();
		RestoreCallCountSetting();
		RestorePlayerProtectionSetting();
		DestroyPlayerSafetyFixture();
	}

	private void EnsurePlayerSafetyFixture()
	{
		if ( Scene.GetAllComponents<PlayerController>().Any() ) return;
		_playerSafetyFixture = new GameObject( true, "Voxel Benchmark Player Safety Fixture" );
		_playerSafetyFixture.WorldPosition = Vector3.Zero;
		var body = _playerSafetyFixture.AddComponent<Rigidbody>();
		// The fixture exists only to exercise the player-facing streaming path. A
		// gravity-driven body would fall between manager updates and continually
		// move the streaming center, preventing the initial world from settling.
		body.Gravity = false;
		body.MotionEnabled = true;
		_playerSafetyFixture.AddComponent<PlayerController>();
	}

	private void DestroyPlayerSafetyFixture()
	{
		if ( _playerSafetyFixture is null ) return;
		_playerSafetyFixture.Destroy();
		_playerSafetyFixture = null;
	}

	protected override void OnUpdate()
	{
		if ( !_initializationComplete )
		{
			if ( !TryInitialize() )
			{
				if ( ++_initializationAttempts == 120 ) Log.Error( "Voxel terrain benchmark requires a VoxelManager on the same GameObject." );
				return;
			}
		}
		if ( _runRequested && _phase == BenchmarkPhase.Idle )
		{
			BeginRun( true );
		}
		if ( _holdBenchmarkPlayersAtOrigin ) HoldBenchmarkPlayersAtOrigin();

		if ( _sampler?.Sample() == true )
		{
			var terrain = _manager.CaptureGpuTerrainDiagnostics();
			if ( _sampler.TryConsumeStutterDiagnostic() )
			{
				var updateTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Update.GetMetric( 1 );
				var renderTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Render.GetMetric( 1 );
				var physicsTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Physics.GetMetric( 1 );
				var idleTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Idle.GetMetric( 1 );
				var asyncTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Async.GetMetric( 1 );
				var gcTiming = Sandbox.Diagnostics.PerformanceStats.Timings.GcPause.GetMetric( 1 );
				var frameMilliseconds = Sandbox.Diagnostics.PerformanceStats.FrameTime * 1000.0;
				var unaccountedFrameMilliseconds = VoxelManager.ComputeUnaccountedFrameMilliseconds( frameMilliseconds, updateTiming.Max, renderTiming.Max, physicsTiming.Max, idleTiming.Max, asyncTiming.Max, gcTiming.Max );
				Log.Info( $"Voxel terrain stutter diagnostic: frameMs={frameMilliseconds:F2}, unaccountedFrameMs={unaccountedFrameMilliseconds:F2}, updateMs={updateTiming.Max:F2}, renderMs={renderTiming.Max:F2}, physicsMs={physicsTiming.Max:F2}, idleMs={idleTiming.Max:F2}, asyncMs={asyncTiming.Max:F2}, gcTimingMs={gcTiming.Max:F2}, gpuMs={Sandbox.Diagnostics.PerformanceStats.GpuFrametime:F2}, gpuPendingCount={terrain.PendingCountBatches}, gpuPendingEmit={terrain.PendingEmitBatches}, gpuResidents={terrain.ResidentBlocks}/{terrain.DesiredBlocks}, gpuVisible={terrain.VisibleDrawCommands}, gpuReadbackMs={terrain.CountReadbackAverageMilliseconds:F2}, gpuBatchP95Ms={terrain.BatchCompletion.P95Milliseconds:F2}, collisionGeneration={_manager.FormatCollisionGenerationTrace()}, allocated={Sandbox.Diagnostics.PerformanceStats.BytesAllocated}, gcPause={Sandbox.Diagnostics.PerformanceStats.GcPause}." );
			}
		}
		if ( _manager?.HasPartialVisualEditPublication == true ||
			(_phase == BenchmarkPhase.WaitSeamEdit && _manager?.HasVisualCollisionMismatch == true) )
		{
			_sampler?.RecordVisualCoherenceViolation();
		}
		if ( !IsRunning )
		{
			return;
		}
		if ( PhaseTimedOut() )
		{
			FailRun( $"phase {_phase} exceeded {ScenarioTimeoutSeconds:F0}s; scenario={CurrentScenario}; settle={_manager.TerrainSettleDiagnostics}; gpu={_manager.GpuTerrainLiveDiagnostics}" );
			return;
		}

			switch ( _phase )
		{
			case BenchmarkPhase.WaitInitialGeneration:
				// A freshly enabled manager has an empty desired set for one frame before
				// its first world-generation request is processed. Treating that empty
				// state as settled lets the GPU proof run without an origin chunk.
				if ( _manager.DesiredChunkCount == _manager.ConfiguredChunkCount && _manager.IsTerrainSettled )
					CompleteScenarioAndWarmup( BenchmarkPhase.StartCollisionBacklog );
				break;
			case BenchmarkPhase.StartCollisionBacklog:
				BeginScenario( "collision_backlog_frame_budget", "Rebuild every nearby collider through the bounded physics publication queue" );
				_manager.RebuildCollisionWorldForBenchmark();
				_phase = BenchmarkPhase.WaitCollisionBacklog;
				break;
			case BenchmarkPhase.WaitCollisionBacklog:
				if ( _manager.IsTerrainSettled )
					CompleteScenarioAndWarmup( BenchmarkPhase.StartCollisionProximityEdit );
				break;
			case BenchmarkPhase.StartCollisionProximityEdit:
				BeginScenario( "collision_proximity_edit_filter", "Edit a loaded terrain chunk outside every player collision radius without queuing CPU collision work" );
				_collisionProximityBaseline = _manager.CaptureCallCountSnapshot();
				var farChunk = _manager.ChunkRadius - 1;
				if ( farChunk <= _manager.CollisionChunkRadius )
				{
					_sampler.RecordFailure();
					CompleteScenario();
					FailRun( $"collision proximity proof requires ChunkRadius > CollisionChunkRadius + 1; configured={_manager.ChunkRadius}/{_manager.CollisionChunkRadius}" );
					break;
				}
				var farLocalPosition = new Vector3( (farChunk + 0.5f) * _manager.ChunkSize * _manager.VoxelSize, 0.0f, 0.0f );
				var editStart = System.Diagnostics.Stopwatch.GetTimestamp();
				var changedChunks = _manager.DisplaceSdf( _manager.GameObject.WorldTransform.PointToWorld( farLocalPosition ), _manager.VoxelSize * 3.0f, _manager.VoxelSize * 4.0f );
				_sampler.RecordEdit( changedChunks, System.Diagnostics.Stopwatch.GetElapsedTime( editStart ).TotalMilliseconds );
				if ( changedChunks == 0 )
				{
					_sampler.RecordFailure();
					CompleteScenario();
					FailRun( $"collision proximity proof edit changed no chunks at local position {farLocalPosition}" );
					break;
				}
				_phase = BenchmarkPhase.WaitCollisionProximityEdit;
				break;
			case BenchmarkPhase.WaitCollisionProximityEdit:
				if ( !_manager.IsTerrainSettled ) break;
				var collisionCalls = _manager.CaptureCallCountSnapshot().Subtract( _collisionProximityBaseline );
				var collisionWork = collisionCalls.CollisionBuildsQueued + collisionCalls.CollisionBuildsStarted + collisionCalls.CollisionBuildsCompleted + collisionCalls.CollisionUploads;
				if ( collisionWork > 0 ) _sampler.RecordFailure();
				CompleteScenario();
				if ( collisionWork > 0 )
				{
					FailRun( $"distant terrain edit escaped the collision proximity filter: queued={collisionCalls.CollisionBuildsQueued}, started={collisionCalls.CollisionBuildsStarted}, completed={collisionCalls.CollisionBuildsCompleted}, uploads={collisionCalls.CollisionUploads}" );
					break;
				}
				_phase = BenchmarkPhase.StartPhase4Planner;
				StartWarmup();
				break;
			case BenchmarkPhase.Warmup:
				if ( --_warmupFramesRemaining <= 0 ) AdvanceAfterWarmup();
				break;
			case BenchmarkPhase.StartPhase4Planner:
				if ( _manager.IsPlayerSafetyActive ) break;
				BeginScenario( Phase4PlannerScenarioNames[_phase4PlannerScenarioIndex], "Phase 4 mathematical clipbox planner correctness and bounded movement proof" );
				if ( Phase4PlannerScenarioNames[_phase4PlannerScenarioIndex] == "phase4_planner_reference_equivalence" )
				{
					_referenceEquivalenceRunner = VoxelClipboxDiagnostics.StartReferenceEquivalence();
					_pendingClipboxPlannerProof = default;
				}
				else
				{
					_pendingClipboxPlannerProof = VoxelClipboxDiagnostics.RunPlannerScenario( Phase4PlannerScenarioNames[_phase4PlannerScenarioIndex] );
				}
				_phase = BenchmarkPhase.WaitPhase4Planner;
				break;
			case BenchmarkPhase.WaitPhase4Planner:
				if ( _referenceEquivalenceRunner is not null )
				{
					_referenceEquivalenceRunner.Step( 4.0 );
					if ( !_referenceEquivalenceRunner.IsComplete ) break;
					_pendingClipboxPlannerProof = _referenceEquivalenceRunner.Report;
					_referenceEquivalenceRunner = null;
				}
				CompleteScenario( clipboxPlannerProof: _pendingClipboxPlannerProof );
				if ( !_pendingClipboxPlannerProof.Passed )
				{
					FailRun( $"Phase 4 planner scenario failed: {_pendingClipboxPlannerProof.Failure}" );
					break;
				}
				_phase4PlannerScenarioIndex++;
				if ( _phase4PlannerScenarioIndex < Phase4PlannerScenarioNames.Length )
				{
					_phase = BenchmarkPhase.StartPhase4Planner;
					break;
				}
				_phase5EditScenarioIndex = 0;
				_phase = BenchmarkPhase.StartPhase5EditProof;
				StartWarmup();
				break;
			case BenchmarkPhase.StartPhase5EditProof:
				BeginScenario( Phase5EditScenarioNames[_phase5EditScenarioIndex], "Phase Five sparse edit authority, deterministic invalidation, stale-generation rejection, and replay proof" );
				_pendingPhase5EditProof = VoxelEditDiagnostics.RunScenario( Phase5EditScenarioNames[_phase5EditScenarioIndex] );
				_phase = BenchmarkPhase.WaitPhase5EditProof;
				break;
			case BenchmarkPhase.WaitPhase5EditProof:
				CompleteScenario();
				if ( !_pendingPhase5EditProof.Passed )
				{
					FailRun( $"Phase Five edit proof failed: {_pendingPhase5EditProof.Failure}" );
					break;
				}
				_phase5EditScenarioIndex++;
				if ( _phase5EditScenarioIndex < Phase5EditScenarioNames.Length )
				{
					_phase = BenchmarkPhase.StartPhase5EditProof;
					break;
				}
				_phase4TransitionScenarioIndex = 0;
				_phase = BenchmarkPhase.StartPhase4Transition;
				StartWarmup();
				break;
			case BenchmarkPhase.StartPhase4Transition:
				BeginScenario( Phase4TransitionScenarioNames[_phase4TransitionScenarioIndex], "Proof-only Transvoxel transition reference: official tables, orientations, fixtures, boundary edges, and winding" );
				try
				{
					_pendingTransitionProof = VoxelTransvoxelTransitionMesher.RunProof( _manager.VoxelSize );
				}
				catch ( System.Exception exception )
				{
					_pendingTransitionProof = new VoxelTransvoxelTransitionProofResult( false, exception.Message, 0, 0, 0, 0, 0, 0, 0.0001f, false );
				}
				if ( _phase4TransitionScenarioIndex == 0 )
				{
					_manager.RunGpuTransitionCaseProof();
					_phase = BenchmarkPhase.WaitPhase4TransitionGpu;
				}
				else _phase = BenchmarkPhase.WaitPhase4Transition;
				break;
			case BenchmarkPhase.WaitPhase4TransitionGpu:
				if ( !_manager.HasGpuTransitionCaseProofResult ) break;
				_pendingTransitionGpuProof = _manager.LastGpuTransitionCaseProofResult;
				_pendingTransitionProof = _pendingTransitionProof with
				{
					GpuCaseProofAvailable = true,
					GpuCaseProofPassed = _pendingTransitionGpuProof.Passed,
					GpuCaseProofFailure = _pendingTransitionGpuProof.Failure,
					GpuCaseVariants = _pendingTransitionGpuProof.Variants,
					GpuCaseBufferBytes = _pendingTransitionGpuProof.GpuBufferBytes,
					GpuCaseSubmissionMilliseconds = _pendingTransitionGpuProof.SubmissionMilliseconds,
					GpuCaseCompletionMilliseconds = _pendingTransitionGpuProof.CompletionMilliseconds,
					GpuCaseReadbackMilliseconds = _pendingTransitionGpuProof.ReadbackMilliseconds
				};
				_manager.ClearGpuTransitionCaseProof();
				_phase = BenchmarkPhase.WaitPhase4Transition;
				break;
			case BenchmarkPhase.WaitPhase4Transition:
				CompleteScenario( transitionProof: _pendingTransitionProof );
				if ( !_pendingTransitionProof.Passed )
				{
					FailRun( $"Phase 4 transition reference failed: {_pendingTransitionProof.Failure}" );
					break;
				}
				_phase4TransitionScenarioIndex++;
				if ( _phase4TransitionScenarioIndex < Phase4TransitionScenarioNames.Length )
				{
					_phase = BenchmarkPhase.StartPhase4Transition;
					StartWarmup();
					break;
				}
				_phase4IndirectScenarioIndex = 0;
				_phase = BenchmarkPhase.StartPhase4Indirect;
				StartWarmup();
				break;
			case BenchmarkPhase.StartPhase4Indirect:
				BeginScenario( Phase4IndirectScenarioNames[_phase4IndirectScenarioIndex], "Phase 4 synthetic indexed indirect-render capacity, range, and depth/opaque parity proof" );
				_pendingIndirectRenderProof = VoxelGpuIndirectRenderDiagnostics.RunScenario( Phase4IndirectScenarioNames[_phase4IndirectScenarioIndex] );
				_phase = BenchmarkPhase.WaitPhase4Indirect;
				break;
			case BenchmarkPhase.WaitPhase4Indirect:
				CompleteScenario( indirectRenderProof: _pendingIndirectRenderProof );
				if ( !_pendingIndirectRenderProof.Passed )
				{
					FailRun( $"Phase 4 indirect renderer scenario failed: {_pendingIndirectRenderProof.Failure}" );
					break;
				}
				_phase4IndirectScenarioIndex++;
				if ( _phase4IndirectScenarioIndex < Phase4IndirectScenarioNames.Length )
				{
					_phase = BenchmarkPhase.StartPhase4Indirect;
					break;
				}
				_phase = Mode == VoxelTerrainBenchmarkMode.CpuOnly ? BenchmarkPhase.StartLiveConfiguration : BenchmarkPhase.StartPhase4Regular;
				StartWarmup();
				break;
			case BenchmarkPhase.StartPhase4Regular:
				_holdBenchmarkPlayersAtOrigin = true;
				HoldBenchmarkPlayersAtOrigin();
				_manager.SetBenchmarkPlayerProtection( true );
				_manager.GpuTerrainLodPolicy = VoxelGpuTerrainLodPolicy.RegularClipbox;
				_manager.GpuClipboxBlocksPerAxis = Phase4RegularBlocksPerAxis[_phase4RegularScenarioIndex];
				_manager.GpuClipboxLevelCount = Phase4RegularLevelCounts[_phase4RegularScenarioIndex];
				_manager.GpuClipboxMatchChunkRadius = _phase4RegularScenarioIndex == Phase4RegularScenarioNames.Length - 1;
				if ( _manager.GpuClipboxMatchChunkRadius ) _manager.ChunkRadius = 64;
				_manager.VisualBackend = VoxelVisualBackendMode.GpuPersistentFixedLod;
				BeginScenario( Phase4RegularScenarioNames[_phase4RegularScenarioIndex], $"Regular GPU clipbox B{_manager.GpuClipboxBlocksPerAxis} L{_manager.EffectiveGpuClipboxLevelCount} with transition metadata and geometry disabled" );
				_manager.GenerateGpuTerrainWorld();
				_phase4RegularSoakStartTimestamp = 0;
				_phase = BenchmarkPhase.WaitPhase4Regular;
				break;
			case BenchmarkPhase.WaitPhase4Regular:
				if ( _manager.IsTerrainSettled )
				{
					var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
					var scenarioIndex = _phase4RegularScenarioIndex;
					_pendingRegularClipboxProof = VoxelGpuPhase2BProof.ValidateRegularClipbox( Phase4RegularScenarioNames[scenarioIndex], diagnostics, Phase4RegularExpectedActiveCounts[scenarioIndex], $"regular_clipbox_b{Phase4RegularBlocksPerAxis[scenarioIndex]}_l{Phase4RegularLevelCounts[scenarioIndex]}", Phase4RegularExpectedStableSlots[scenarioIndex] );
					CompleteScenario( gpuTerrain: diagnostics, gpuPhase2BProof: _pendingRegularClipboxProof );
					_manager.GpuTerrainLodPolicy = _originalGpuTerrainLodPolicy;
					_manager.GpuClipboxBlocksPerAxis = _originalGpuClipboxBlocksPerAxis;
					_manager.GpuClipboxLevelCount = _originalGpuClipboxLevelCount;
					_manager.GpuClipboxMatchChunkRadius = _originalGpuClipboxMatchChunkRadius;
					_manager.ChunkRadius = _originalChunkRadius;
					if ( !_pendingRegularClipboxProof.Passed )
					{
						FailRun( $"Phase 4 regular clipbox failed: {_pendingRegularClipboxProof.Failure}" );
						break;
					}
					_phase4RegularScenarioIndex++;
					if ( _phase4RegularScenarioIndex < Phase4RegularScenarioNames.Length )
					{
						_phase = BenchmarkPhase.StartPhase4Regular;
						StartWarmup();
						break;
					}
					_holdBenchmarkPlayersAtOrigin = false;
					_phase = Mode == VoxelTerrainBenchmarkMode.CpuOnly ? BenchmarkPhase.StartLiveConfiguration : BenchmarkPhase.StartGpuLod5Ownership;
					StartWarmup();
				}
				break;
			case BenchmarkPhase.StartGpuLod5Ownership:
				_holdBenchmarkPlayersAtOrigin = true;
				HoldBenchmarkPlayersAtOrigin();
				_manager.SetBenchmarkPlayerProtection( true );
				_manager.GpuTerrainLodPolicy = VoxelGpuTerrainLodPolicy.RegularClipbox;
				_manager.GpuClipboxBlocksPerAxis = 8;
				_manager.GpuClipboxLevelCount = 6;
				_manager.GpuClipboxMatchChunkRadius = false;
				_manager.VisualBackend = VoxelVisualBackendMode.GpuPersistentFixedLod;
				BeginScenario( "gpu_lod5_transition_ownership", "Production B8 L6 clipbox validates unique LOD-5 geometry ownership and non-coplanar transition deformation" );
				_manager.GenerateGpuTerrainWorld();
				_phase = BenchmarkPhase.WaitGpuLod5Ownership;
				break;
			case BenchmarkPhase.WaitGpuLod5Ownership:
				if ( _manager.IsTerrainSettled )
				{
					var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
					_pendingLod5OwnershipProof = _manager.CaptureGpuClipboxSeamProofForBenchmark();
					var ownershipPassed = _pendingLod5OwnershipProof?.LodOwnershipPassed == true &&
						diagnostics.RequestedBlocks == 2752 && diagnostics.ResidentBlocks == 2752 &&
						diagnostics.ClipboxStableSlotCount == 3072 && diagnostics.ClipboxTransitionActiveSlotCount == 1920;
					if ( !ownershipPassed ) _sampler?.RecordFailure();
					CompleteScenario( gpuTerrain: diagnostics, lod5OwnershipProof: _pendingLod5OwnershipProof );
					if ( !ownershipPassed )
					{
						FailRun( $"LOD-5 transition ownership failed: {_pendingLod5OwnershipProof?.LodOwnershipFailure ?? "proof unavailable"}; regular={diagnostics.ResidentBlocks}/{diagnostics.RequestedBlocks}, stable={diagnostics.ClipboxStableSlotCount}, transitions={diagnostics.ClipboxTransitionActiveSlotCount}." );
						break;
					}
					_phase = BenchmarkPhase.StartGpuRealtimeEdits;
					StartWarmup();
				}
				break;
			case BenchmarkPhase.StartGpuRealtimeEdits:
				BeginScenario( "gpu_realtime_surface_edits_20hz", "Eighty visible surface deformations at 20 Hz with synchronous edit, frame, GPU, allocation, and clipbox phase timings" );
				_editIndex = 0;
				_nextEditTimestamp = 0;
				_phase = BenchmarkPhase.RunGpuRealtimeEdits;
				break;
			case BenchmarkPhase.RunGpuRealtimeEdits:
				RunGpuRealtimeEdits();
				break;
			case BenchmarkPhase.WaitGpuRealtimeEdits:
				if ( _manager.IsTerrainSettled )
				{
					var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
					if ( _sampler.EditCount != RealtimeSurfaceEditCount || _sampler.ChangedChunkEvents == 0 || diagnostics.EditQueue.Total.Count < RealtimeSurfaceEditCount || diagnostics.ClipboxDroppedWork != 0 || diagnostics.ClipboxTransitionDependencyMismatches != 0 ) _sampler.RecordFailure();
					CompleteScenario( gpuTerrain: diagnostics );
					_phase = BenchmarkPhase.StartGpuAccumulatedEdits;
					StartWarmup();
				}
				break;
			case BenchmarkPhase.StartGpuAccumulatedEdits:
				BeginScenario( "gpu_accumulated_surface_edits_20hz", "Eighty additional visible surface deformations at 20 Hz after an eighty-operation edit history" );
				_editIndex = 0;
				_nextEditTimestamp = 0;
				_phase = BenchmarkPhase.RunGpuAccumulatedEdits;
				break;
			case BenchmarkPhase.RunGpuAccumulatedEdits:
				RunGpuRealtimeEdits( BenchmarkPhase.WaitGpuAccumulatedEdits );
				break;
			case BenchmarkPhase.WaitGpuAccumulatedEdits:
				if ( _manager.IsTerrainSettled )
				{
					var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
					if ( _sampler.EditCount != RealtimeSurfaceEditCount || _sampler.ChangedChunkEvents == 0 || diagnostics.EditQueue.Total.Count < RealtimeSurfaceEditCount * 2 || diagnostics.ClipboxDroppedWork != 0 || diagnostics.ClipboxTransitionDependencyMismatches != 0 ) _sampler.RecordFailure();
					CompleteScenario( gpuTerrain: diagnostics );
					_phase = BenchmarkPhase.StartGpuAimedBrushEdits;
					StartWarmup();
				}
				break;
			case BenchmarkPhase.StartGpuAimedBrushEdits:
				BeginScenario( "gpu_aimed_brush_edits_20hz", "Eighty immediate aimed brush digs at 20 Hz with canonical SDF raycast and spatially bounded edit replay" );
				_gpuAimedBrushBaseline = _manager.CaptureCallCountSnapshot();
				_editIndex = 0;
				_nextEditTimestamp = 0;
				_phase = BenchmarkPhase.RunGpuAimedBrushEdits;
				break;
			case BenchmarkPhase.RunGpuAimedBrushEdits:
				RunGpuAimedBrushEdits();
				break;
			case BenchmarkPhase.WaitGpuAimedBrushEdits:
				if ( _manager.IsTerrainSettled )
				{
					var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
					var calls = _manager.CaptureCallCountSnapshot().Subtract( _gpuAimedBrushBaseline );
					var fullJournalCandidateCount = checked( (long)RealtimeSurfaceEditCount * RealtimeSurfaceEditCount * 2 );
					if ( _sampler.EditCount != RealtimeSurfaceEditCount || _sampler.ChangedChunkEvents == 0 || calls.BrushRaycasts != RealtimeSurfaceEditCount || calls.BrushRaycastSamples <= 0 || calls.BrushRaycastEditCandidates >= fullJournalCandidateCount || diagnostics.EditQueue.Total.Count < RealtimeSurfaceEditCount * 3 || diagnostics.ClipboxDroppedWork != 0 || diagnostics.ClipboxTransitionDependencyMismatches != 0 ) _sampler.RecordFailure();
					CompleteScenario( gpuTerrain: diagnostics );
					_manager.GpuTerrainLodPolicy = _originalGpuTerrainLodPolicy;
					_manager.GpuClipboxBlocksPerAxis = _originalGpuClipboxBlocksPerAxis;
					_manager.GpuClipboxLevelCount = _originalGpuClipboxLevelCount;
					_manager.GpuClipboxMatchChunkRadius = _originalGpuClipboxMatchChunkRadius;
					_manager.ChunkRadius = _originalChunkRadius;
					_holdBenchmarkPlayersAtOrigin = false;
					_phase = BenchmarkPhase.StartGpuCpuCollisionRebuild;
					StartWarmup();
				}
				break;
			case BenchmarkPhase.StartGpuCpuCollisionRebuild:
				BeginScenario( "gpu_cpu_collision_rebuild_cost", "Build and publish every nearby CPU collider while the production GPU terrain backend renders" );
				_gpuCollisionBaseline = _manager.CaptureCallCountSnapshot();
				_manager.RebuildCollisionWorldForBenchmark();
				_phase = BenchmarkPhase.WaitGpuCpuCollisionRebuild;
				break;
			case BenchmarkPhase.WaitGpuCpuCollisionRebuild:
				if ( _manager.IsTerrainSettled )
				{
					var calls = _manager.CaptureCallCountSnapshot().Subtract( _gpuCollisionBaseline );
					if ( calls.CollisionBuildsStarted == 0 || calls.CollisionUploads == 0 ) _sampler.RecordFailure();
					CompleteScenario();
					_phase = BenchmarkPhase.StartGpuTransvoxelProof;
					StartWarmup();
				}
				break;
			case BenchmarkPhase.StartGpuTransvoxelProof:
				if ( Mode == VoxelTerrainBenchmarkMode.GpuOnly )
				{
					_phase = BenchmarkPhase.StartGpuPersistentStatic;
					StartWarmup();
					break;
				}
				BeginScenario( "gpu_transvoxel_regular_proof", "GPU density generation and direct regular-cell Transvoxel meshing validated against the CPU Transvoxel reference" );
				_manager.RunGpuTransvoxelProof();
				_phase = BenchmarkPhase.WaitGpuTransvoxelProof;
				break;
			case BenchmarkPhase.WaitGpuTransvoxelProof:
				if ( _manager.HasGpuTransvoxelProofResult )
				{
					var proof = _manager.LastGpuTransvoxelProofResult;
					CompleteScenario( proof );
					_manager.ClearGpuTransvoxelProof();
					if ( !proof.Passed )
					{
						FailRun( $"GPU Transvoxel conformance failed: {proof.Failure}" );
						break;
					}
					_phase = BenchmarkPhase.StartGpuPersistentStatic;
					StartWarmup();
				}
				break;
			case BenchmarkPhase.StartGpuPersistentStatic:
				_holdBenchmarkPlayersAtOrigin = true;
				HoldBenchmarkPlayersAtOrigin();
				BeginScenario( "gpu_persistent_static_set", "Static fixed-LOD blocks count asynchronously, emit into persistent pools, and publish through one multi-draw renderer" );
				_manager.VisualBackend = VoxelVisualBackendMode.GpuPersistentFixedLod;
				_manager.GpuTerrainRuleVersion = 1;
				_manager.ChunkRadius = _originalChunkRadius;
				if ( Mode == VoxelTerrainBenchmarkMode.GpuOnly ) _manager.GenerateGpuTerrainWorld();
				else _manager.GenerateWorld();
				_phase = BenchmarkPhase.WaitGpuPersistentStatic;
				break;
			case BenchmarkPhase.WaitGpuPersistentStatic:
				if ( _manager.IsTerrainSettled )
				{
					var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
					var proof = VoxelGpuPhase2BProof.ValidateStatic( "static_persistent_set", diagnostics, _manager.ConfiguredChunkCount );
					CompleteScenario( gpuTerrain: diagnostics, gpuPhase2BProof: proof );
					if ( !proof.Passed ) { FailRun( $"Phase 2B static backend failed: {proof.Failure}" ); break; }
					_gpuLifecycleScenarioIndex = 0;
					_phase = BenchmarkPhase.StartGpuCameraSweepGeneration;
				}
				break;
			case BenchmarkPhase.StartGpuCameraSweepGeneration:
				BeginScenario( "gpu_camera_sweep_generation", "Player camera rapidly sweeps every direction while a cold GPU terrain set generates and becomes visible" );
				_cameraSweepCamera = Scene.Camera;
				if ( _cameraSweepCamera is null )
				{
					FailRun( "gpu_camera_sweep_generation requires the player camera" );
					break;
				}
				_cameraSweepOriginalRotation = _cameraSweepCamera.WorldRotation;
				_manager.GenerateGpuTerrainWorld();
				_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
				_phase = BenchmarkPhase.RunGpuCameraSweepGeneration;
				break;
			case BenchmarkPhase.RunGpuCameraSweepGeneration:
				{
					var elapsedSeconds = System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp ).TotalSeconds;
					_cameraSweepCamera.WorldRotation = new Angles( 12.0f * System.MathF.Sin( (float)elapsedSeconds * 5.0f ), (float)elapsedSeconds * 1080.0f, 0.0f );
					if ( !_manager.IsTerrainSettled || elapsedSeconds < 1.0 ) break;
					_cameraSweepCamera.WorldRotation = _cameraSweepOriginalRotation;
					_cameraSweepCamera = null;
					var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
					var proof = VoxelGpuPhase2BProof.ValidateStatic( "camera_sweep_generation", diagnostics, _manager.ConfiguredChunkCount );
					CompleteScenario( gpuTerrain: diagnostics, gpuPhase2BProof: proof );
					if ( !proof.Passed ) { FailRun( $"GPU camera sweep generation failed: {proof.Failure}" ); break; }
					_phase = BenchmarkPhase.StartGpuProductionRender;
				}
				break;
			case BenchmarkPhase.StartGpuProductionRender:
				BeginScenario( "gpu_production_render_integration", "Standard lit GPU terrain material is attached to depth-prepass and opaque bounded multi-draw command lists" );
				_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
				_phase = BenchmarkPhase.WaitGpuProductionRender;
				break;
			case BenchmarkPhase.WaitGpuProductionRender:
				if ( _manager.IsTerrainSettled && System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp ).TotalSeconds >= 0.5 )
				{
					var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
					var proof = VoxelGpuPhase3AProof.ValidateProductionRender( "production_render_integration", diagnostics );
					CompleteScenario( gpuTerrain: diagnostics, gpuPhase3AProof: proof );
					if ( !proof.Passed ) { FailRun( $"Phase 3A production render integration failed: {proof.Failure}" ); break; }
					_phase = BenchmarkPhase.StartGpuMovementInfinity;
				}
				break;
			case BenchmarkPhase.StartGpuMovementInfinity:
				_holdBenchmarkPlayersAtOrigin = false;
				BeginPlayerTraversal( TraversalPath.Infinity, "gpu_player_infinity_streaming", "Actual player flies an infinity loop while the fixed-LOD GPU backend replaces residents" );
				_phase = BenchmarkPhase.RunGpuMovementInfinity;
				break;
			case BenchmarkPhase.RunGpuMovementInfinity:
				RunPlayerTraversal( BenchmarkPhase.WaitGpuMovementInfinity );
				break;
			case BenchmarkPhase.WaitGpuMovementInfinity:
				if ( _manager.IsTerrainSettled ) CompleteGpuPlayerTraversal( "gpu_player_infinity_streaming", BenchmarkPhase.StartGpuMovementLine );
				break;
			case BenchmarkPhase.StartGpuMovementLine:
				BeginPlayerTraversal( TraversalPath.Line, "gpu_player_line_streaming", "Actual player flies straight out and back while the fixed-LOD GPU backend replaces residents" );
				_phase = BenchmarkPhase.RunGpuMovementLine;
				break;
			case BenchmarkPhase.RunGpuMovementLine:
				RunPlayerTraversal( BenchmarkPhase.WaitGpuMovementLine );
				break;
			case BenchmarkPhase.WaitGpuMovementLine:
				if ( _manager.IsTerrainSettled ) CompleteGpuPlayerTraversal( "gpu_player_line_streaming", BenchmarkPhase.StartGpuMovementDiagonal );
				break;
			case BenchmarkPhase.StartGpuMovementDiagonal:
				BeginPlayerTraversal( TraversalPath.Diagonal, "gpu_player_diagonal_streaming", "Actual player flies diagonally through simultaneous X/Y boundaries while the fixed-LOD GPU backend replaces residents" );
				_phase = BenchmarkPhase.RunGpuMovementDiagonal;
				break;
			case BenchmarkPhase.RunGpuMovementDiagonal:
				RunPlayerTraversal( BenchmarkPhase.WaitGpuMovementDiagonal );
				break;
			case BenchmarkPhase.WaitGpuMovementDiagonal:
				if ( _manager.IsTerrainSettled ) CompleteGpuPlayerTraversal( "gpu_player_diagonal_streaming", BenchmarkPhase.StartGpuMovementVertical );
				break;
			case BenchmarkPhase.StartGpuMovementVertical:
				BeginPlayerTraversal( TraversalPath.Vertical, "gpu_player_clipbox_oscillation", "Actual player moves vertically across clipbox boundaries and back while coherent regular and transition revisions settle" );
				_phase = BenchmarkPhase.RunGpuMovementVertical;
				break;
			case BenchmarkPhase.RunGpuMovementVertical:
				RunPlayerTraversal( BenchmarkPhase.WaitGpuMovementVertical );
				break;
			case BenchmarkPhase.WaitGpuMovementVertical:
				if ( _manager.IsTerrainSettled )
				{
					_phaseAfterHighSpeedCollisionTraversal = BenchmarkPhase.StartGpuLifecycleScenario;
					CompleteGpuPlayerTraversal( "gpu_player_clipbox_oscillation", BenchmarkPhase.StartHighSpeedCollisionTraversal );
				}
				break;
			case BenchmarkPhase.StartHighSpeedCollisionTraversal:
				BeginPlayerTraversal( TraversalPath.Line, "high_speed_collision_streaming", "Actual player traverses at 20,000 units/s while every occupied terrain chunk must already have collision", HighSpeedCollisionTraversalSpeed, true );
				break;
			case BenchmarkPhase.RunHighSpeedCollisionTraversal:
				RunPlayerTraversal( BenchmarkPhase.WaitHighSpeedCollisionTraversal );
				break;
			case BenchmarkPhase.WaitHighSpeedCollisionTraversal:
				if ( _manager.IsTerrainSettled ) CompleteHighSpeedCollisionTraversal();
				break;
			case BenchmarkPhase.StartGpuLifecycleScenario:
				BeginScenario( GpuLifecycleScenarioNames[_gpuLifecycleScenarioIndex], "Production range allocation, transactional replacement, exhaustion backpressure, deferred reclaim, and return-origin stability" );
				var referenceVertices = checked( (_manager.ChunkSize + 1) * (_manager.ChunkSize + 1) );
				var referenceIndices = checked( _manager.ChunkSize * _manager.ChunkSize * 6 );
				_pendingGpuPhase2BProof = VoxelGpuPhase2BProof.RunLifecycle( GpuLifecycleScenarioNames[_gpuLifecycleScenarioIndex], referenceVertices, referenceIndices );
				_phase = BenchmarkPhase.WaitGpuLifecycleScenario;
				break;
			case BenchmarkPhase.WaitGpuLifecycleScenario:
				CompleteScenario( gpuPhase2BProof: _pendingGpuPhase2BProof );
				if ( !_pendingGpuPhase2BProof.Passed ) { FailRun( $"Phase 2B lifecycle failed: {_pendingGpuPhase2BProof.Failure}" ); break; }
				_gpuLifecycleScenarioIndex++;
				_phase = _gpuLifecycleScenarioIndex < GpuLifecycleScenarioNames.Length
					? BenchmarkPhase.StartGpuLifecycleScenario
					: BenchmarkPhase.StartGpuAsyncReadbackSaturation;
				break;
			case BenchmarkPhase.StartGpuAsyncReadbackSaturation:
				_holdBenchmarkPlayersAtOrigin = true;
				HoldBenchmarkPlayersAtOrigin();
				BeginScenario( "gpu_async_readback_saturation", "Two full 128-block batches exercise bounded asynchronous compact-count publication without geometry readback" );
				_manager.ChunkRadius = _originalChunkRadius;
				_manager.GenerateWorld();
				_phase = BenchmarkPhase.WaitGpuAsyncReadbackSaturation;
				break;
			case BenchmarkPhase.WaitGpuAsyncReadbackSaturation:
				if ( _manager.IsTerrainSettled )
				{
					var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
					var proof = VoxelGpuPhase2BProof.ValidateStatic( "async_readback_saturation", diagnostics, _manager.ConfiguredChunkCount, 2 );
					CompleteScenario( gpuTerrain: diagnostics, gpuPhase2BProof: proof );
					if ( !proof.Passed ) { FailRun( $"Phase 2B async readback saturation failed: {proof.Failure}" ); break; }
					_phase = BenchmarkPhase.StartGpuResourceRecreation;
				}
				break;
			case BenchmarkPhase.StartGpuResourceRecreation:
				BeginScenario( "gpu_resource_recreation", "Dispose and recreate scratch, persistent pools, residents, and renderer, then republish the same static set" );
				_manager.GenerateWorld();
				_phase = BenchmarkPhase.WaitGpuResourceRecreation;
				break;
			case BenchmarkPhase.WaitGpuResourceRecreation:
				if ( _manager.IsTerrainSettled )
				{
					var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
					var proof = VoxelGpuPhase2BProof.ValidateStatic( "resource_recreation", diagnostics, _manager.ConfiguredChunkCount, 2 );
					CompleteScenario( gpuTerrain: diagnostics, gpuPhase2BProof: proof );
					if ( !proof.Passed ) { FailRun( $"Phase 2B recreation failed: {proof.Failure}" ); break; }
					_phase = BenchmarkPhase.StartGpuDedicatedServerStartup;
				}
				break;
			case BenchmarkPhase.StartGpuDedicatedServerStartup:
				BeginScenario( "gpu_dedicated_server_startup", "Dedicated-server capability policy rejects every GPU terrain allocation before construction" );
				_pendingGpuPhase2BProof = VoxelGpuPhase2BProof.RunDedicatedServerPolicy();
				_phase = BenchmarkPhase.WaitGpuDedicatedServerStartup;
				break;
			case BenchmarkPhase.WaitGpuDedicatedServerStartup:
				CompleteScenario( gpuPhase2BProof: _pendingGpuPhase2BProof );
				if ( !_pendingGpuPhase2BProof.Passed ) { FailRun( $"Phase 2B dedicated-server startup failed: {_pendingGpuPhase2BProof.Failure}" ); break; }
				if ( Mode == VoxelTerrainBenchmarkMode.GpuOnly )
				{
					_phase = BenchmarkPhase.StartFinalStationarySoak;
					StartWarmup();
					break;
				}
				_manager.VisualBackend = VoxelVisualBackendMode.CpuChunks;
				_manager.GpuTerrainRuleVersion = _originalGpuTerrainRuleVersion;
				_manager.ChunkRadius = _originalChunkRadius;
				_manager.GenerateWorld();
				_phaseAfterReset = BenchmarkPhase.StartLiveConfiguration;
				_phase = BenchmarkPhase.WaitReset;
				break;
			case BenchmarkPhase.StartLiveConfiguration:
				BeginLiveConfigurationScenario();
				break;
			case BenchmarkPhase.WaitLiveConfiguration:
				if ( _manager.IsTerrainSettled && _manager.DesiredChunkCount == _manager.ConfiguredChunkCount )
				{
					CompleteScenario();
					_manager.ChunkRadius = _originalChunkRadius;
					_manager.GenerateWorld();
					_phaseAfterReset = BenchmarkPhase.StartInfinityTraversal;
					_phase = BenchmarkPhase.WaitReset;
					_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
				}
				break;
			case BenchmarkPhase.StartInfinityTraversal:
				_holdBenchmarkPlayersAtOrigin = false;
				BeginPlayerTraversal( TraversalPath.Infinity, "player_infinity_streaming", "Actual player flies one or more constant-speed infinity loops while terrain streams" );
				break;
			case BenchmarkPhase.RunInfinityTraversal:
				RunPlayerTraversal( BenchmarkPhase.WaitInfinityTraversal );
				break;
			case BenchmarkPhase.WaitInfinityTraversal:
				if ( _manager.IsTerrainSettled ) CompletePlayerTraversalAndReset( BenchmarkPhase.StartLineTraversal );
				break;
			case BenchmarkPhase.StartLineTraversal:
				BeginPlayerTraversal( TraversalPath.Line, "player_line_streaming", "Actual player flies straight out and back at constant speed while terrain streams" );
				break;
			case BenchmarkPhase.RunLineTraversal:
				RunPlayerTraversal( BenchmarkPhase.WaitLineTraversal );
				break;
			case BenchmarkPhase.WaitLineTraversal:
				if ( _manager.IsTerrainSettled )
				{
					if ( Mode == VoxelTerrainBenchmarkMode.CpuOnly )
					{
						_phaseAfterHighSpeedCollisionTraversal = BenchmarkPhase.StartDiagonalTraversal;
						CompletePlayerTraversalAndReset( BenchmarkPhase.StartHighSpeedCollisionTraversal );
					}
					else CompletePlayerTraversalAndReset( BenchmarkPhase.StartDiagonalTraversal );
				}
				break;
			case BenchmarkPhase.StartDiagonalTraversal:
				BeginPlayerTraversal( TraversalPath.Diagonal, "player_diagonal_streaming", "Actual player flies diagonally through simultaneous X/Y chunk boundaries and back" );
				break;
			case BenchmarkPhase.RunDiagonalTraversal:
				RunPlayerTraversal( BenchmarkPhase.WaitDiagonalTraversal );
				break;
			case BenchmarkPhase.WaitDiagonalTraversal:
				if ( _manager.IsTerrainSettled ) CompletePlayerTraversalAndReset( BenchmarkPhase.StartSeamEdit );
				break;
			case BenchmarkPhase.StartSeamEdit:
				BeginSeamEditScenario();
				break;
			case BenchmarkPhase.WaitSeamEdit:
				if ( _manager.IsTerrainSettled ) CompleteScenarioAndReset( BenchmarkPhase.StartVariedEdits );
				break;
			case BenchmarkPhase.StartVariedEdits:
				BeginScenario( "varied_edits", "Different radii, strengths, signs, centers, and chunk seams" );
				_phase = BenchmarkPhase.RunVariedEdits;
				_editIndex = 0;
				_nextEditTimestamp = 0;
				break;
			case BenchmarkPhase.RunVariedEdits:
				RunVariedEdits();
				break;
			case BenchmarkPhase.WaitVariedEdits:
				if ( _manager.IsTerrainSettled ) CompleteScenarioAndReset( BenchmarkPhase.StartBulkEdit );
				break;
			case BenchmarkPhase.WaitReset:
				if ( _manager.IsTerrainSettled )
				{
					_phase = _phaseAfterReset;
					StartWarmup();
				}
				break;
			case BenchmarkPhase.StartBulkEdit:
				BeginScenario( "bulk_edit", "One large dig spanning many chunks" );
				ApplyLocalEdit( Vector3.Zero, _manager.VoxelSize * 10.0f, _manager.VoxelSize * 4.0f );
				_phase = BenchmarkPhase.WaitBulkEdit;
				break;
			case BenchmarkPhase.WaitBulkEdit:
				if ( _manager.IsTerrainSettled ) CompleteScenarioAndReset( BenchmarkPhase.StartSustainedDig );
				break;
			case BenchmarkPhase.StartSustainedDig:
				BeginSustainedScenario( "sustained_world_sweep_and_depth_dig_20hz", "Fast serpentine surface sweep across the configured world followed by a descending shaft", true );
				break;
			case BenchmarkPhase.RunSustainedDig:
				RunSustainedEdits( true, BenchmarkPhase.WaitSustainedDig );
				break;
			case BenchmarkPhase.WaitSustainedDig:
				if ( _manager.IsTerrainSettled ) CompleteScenarioAndReset( BenchmarkPhase.StartSustainedPlace );
				break;
			case BenchmarkPhase.StartSustainedPlace:
				BeginSustainedScenario( "sustained_world_spiral_place_20hz", "Fast placement spiral expanding from world origin to the configured terrain boundary", false );
				break;
			case BenchmarkPhase.RunSustainedPlace:
				RunSustainedEdits( false, BenchmarkPhase.WaitSustainedPlace );
				break;
			case BenchmarkPhase.WaitSustainedPlace:
				if ( _manager.IsTerrainSettled )
				{
					CompleteScenario();
					_phase = BenchmarkPhase.StartPostEditLineTraversal;
					StartWarmup();
				}
				break;
			case BenchmarkPhase.StartPostEditLineTraversal:
				BeginPlayerTraversal( TraversalPath.Line, "player_post_edit_line_streaming", "Actual player crosses streamed terrain after the complete edit workload; historical edits must remain spatially bounded" );
				if ( _phase != BenchmarkPhase.Failed ) _phase = BenchmarkPhase.RunPostEditLineTraversal;
				break;
			case BenchmarkPhase.RunPostEditLineTraversal:
				RunPlayerTraversal( BenchmarkPhase.WaitPostEditLineTraversal );
				break;
			case BenchmarkPhase.WaitPostEditLineTraversal:
				if ( _manager.IsTerrainSettled ) CompletePostEditPlayerTraversal();
				break;
			case BenchmarkPhase.StartFinalStationarySoak:
				BeginFinalStationarySoak();
				break;
			case BenchmarkPhase.WaitFinalStationarySoak:
				if ( _manager.IsTerrainSettled )
				{
					_phase4RegularSoakStartTimestamp = _phase4RegularSoakStartTimestamp == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : _phase4RegularSoakStartTimestamp;
					if ( System.Diagnostics.Stopwatch.GetElapsedTime( _phase4RegularSoakStartTimestamp ).TotalSeconds < FinalStationarySoakSeconds ) break;

					var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
					_pendingRegularClipboxProof = VoxelGpuPhase2BProof.ValidateRegularClipbox( FinalStationaryScenarioName, diagnostics, 1856, "regular_clipbox_b8_l4", 2048 );
					CompleteScenario( gpuTerrain: diagnostics, gpuPhase2BProof: _pendingRegularClipboxProof );
					RestoreFinalStationarySoakSettings();
					_holdBenchmarkPlayersAtOrigin = false;
					if ( !_pendingRegularClipboxProof.Passed )
					{
						FailRun( $"Final stationary clipbox failed: {_pendingRegularClipboxProof.Failure}" );
						break;
					}
					FinalizeCompletedRun();
				}
				break;
		}
	}

	[Button]
	public void RunBenchmark()
	{
		if ( IsRunning )
		{
			Log.Warning( "Voxel terrain benchmark is already running." );
			return;
		}
		if ( _initializationComplete )
		{
			BeginRun( true );
			return;
		}

		_runRequested = true;
		_phase = BenchmarkPhase.Idle;
		Log.Info( "Voxel terrain benchmark queued until the benchmark component initializes." );
	}

	private void BeginRun( bool regenerate, bool automaticReproduction = false )
	{
		_runRequested = false;
		if ( !automaticReproduction )
		{
			_isReproductionRun = false;
			_reproductionOfRunId = null;
			_reproductionBaselines = null;
			_reproductionTargets = null;
		}
		_manager.CaptureCallCounts = true;
		var gitIdentity = VoxelBenchmarkGitIdentity.Load();
		Revision = gitIdentity.Revision;
		WorkingTreeDirty = gitIdentity.WorkingTreeDirty;
		_manager.SetBenchmarkPlayerProtection( true );
		CaptureAndFreezeBenchmarkPlayers();
		if ( !automaticReproduction || !_worldSettingsCaptured )
		{
			_originalChunkRadius = _manager.ChunkRadius;
			_originalVisualBackend = _manager.VisualBackend;
			_originalGpuTerrainRuleVersion = _manager.GpuTerrainRuleVersion;
			_originalGpuTerrainLodPolicy = _manager.GpuTerrainLodPolicy;
			_originalGpuClipboxBlocksPerAxis = _manager.GpuClipboxBlocksPerAxis;
			_originalGpuClipboxLevelCount = _manager.GpuClipboxLevelCount;
			_originalGpuClipboxMatchChunkRadius = _manager.GpuClipboxMatchChunkRadius;
			_worldSettingsCaptured = true;
		}
		if ( Mode != VoxelTerrainBenchmarkMode.GpuOnly && _manager.VisualBackend != VoxelVisualBackendMode.CpuChunks )
		{
			_manager.VisualBackend = VoxelVisualBackendMode.CpuChunks;
			_manager.GenerateWorld();
		}
		_results.Clear();
		_comparisons.Clear();
		_phase4PlannerScenarioIndex = 0;
		_phase5EditScenarioIndex = 0;
		_phase4TransitionScenarioIndex = 0;
		_phase4IndirectScenarioIndex = 0;
		_phase4RegularScenarioIndex = 0;
		_runId = System.DateTime.UtcNow.ToString( "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture );
		if ( Mode == VoxelTerrainBenchmarkMode.GpuOnly )
		{
			_manager.VisualBackend = VoxelVisualBackendMode.GpuPersistentFixedLod;
			_manager.GpuTerrainRuleVersion = _originalGpuTerrainRuleVersion == 0 ? 1 : _originalGpuTerrainRuleVersion;
			_manager.ChunkRadius = _originalChunkRadius;
			_manager.GenerateGpuTerrainWorld();
			_phase = BenchmarkPhase.StartPhase4Planner;
			Log.Info( $"Voxel terrain benchmark {_runId} started in GPU-only mode at revision {Revision} (dirty={WorkingTreeDirty})." );
			return;
		}
		BeginScenario( "cold_generation", "Authoritative SDF generation, Transvoxel visual meshing, GPU upload, and nearby collision" );
		_phase = BenchmarkPhase.WaitInitialGeneration;
		if ( regenerate && !_manager.IsWorldGenerationPending )
		{
			_manager.GenerateWorld();
		}
		Log.Info( $"Voxel terrain benchmark {_runId} started at revision {Revision} (dirty={WorkingTreeDirty})." );
	}

	private void BeginScenario( string name, string description )
	{
		_sampler = new FrameSampler( name, description, _manager.CaptureTerrainDiagnostics(), _manager.CaptureCallCountSnapshot(), _manager.LatestChunkTimingSequence, _manager.LatestBatchTimingSequence );
		_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		Log.Info( $"Voxel terrain benchmark scenario started: {name} ({description})." );
	}

	private void CompleteScenario( VoxelGpuTransvoxelProofResult? gpuProof = null, VoxelGpuTerrainDiagnostics? gpuTerrain = null, VoxelGpuPhase2BProofResult? gpuPhase2BProof = null, VoxelGpuPhase3AProofResult? gpuPhase3AProof = null, VoxelGpuPhase3BProofResult? gpuPhase3BProof = null, VoxelClipboxPlannerProofReport? clipboxPlannerProof = null, VoxelGpuIndirectRenderProofReport? indirectRenderProof = null, VoxelTransvoxelTransitionProofResult? transitionProof = null, VoxelGpuClipboxSeamProofReport? lod5OwnershipProof = null )
	{
		if ( _sampler is null ) return;
		var streaming = _manager.CaptureChunkStreamingDiagnostics( _sampler.StartingChunkTimingSequence, _sampler.StartingBatchTimingSequence );
		var result = _sampler.Complete( _manager.CaptureTerrainDiagnostics(), _manager.CaptureCallCountSnapshot(), streaming, System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp ).TotalMilliseconds, gpuProof, gpuTerrain, gpuPhase2BProof, gpuPhase3AProof, gpuPhase3BProof, clipboxPlannerProof, indirectRenderProof, transitionProof, lod5OwnershipProof );
		_results.Add( result );
		var hottest = result.CallCounts.Enumerate().OrderByDescending( entry => entry.Count ).First();
		Log.Info( $"Voxel terrain benchmark scenario {result.Name}: result={(result.Passed ? "PASS" : "FAIL")}, elapsed={result.ElapsedMilliseconds:F2}ms, FPS(avg/1%-low/0.1%-low)={result.AverageFramesPerSecond:F1}/{result.OnePercentLowFramesPerSecond:F1}/{result.PointOnePercentLowFramesPerSecond:F1}, frameMs(p95/max)={result.FrameP95Milliseconds:F2}/{result.FrameMaximumMilliseconds:F2}, stutters={result.StutterEvents:N0}, editLatency(p95/settle)={result.EditCallP95Milliseconds:F3}/{result.PostEditSettleMilliseconds:F2}ms, GPU-p95={result.GpuP95Milliseconds:F2}ms, hottest={hottest.Name}:{hottest.Count:N0}, allocated={FormatBytes( result.AllocatedBytes )}." );
		_sampler = null;
	}

	private void BeginFinalStationarySoak()
	{
		_holdBenchmarkPlayersAtOrigin = true;
		HoldBenchmarkPlayersAtOrigin();
		_manager.SetBenchmarkPlayerProtection( true );
		_manager.GpuTerrainLodPolicy = VoxelGpuTerrainLodPolicy.RegularClipbox;
		_manager.GpuClipboxBlocksPerAxis = 8;
		_manager.GpuClipboxLevelCount = 4;
		_manager.GpuClipboxMatchChunkRadius = false;
		_manager.VisualBackend = VoxelVisualBackendMode.GpuPersistentFixedLod;
		BeginScenario( FinalStationaryScenarioName, "Final 60-second stationary GPU clipbox soak at B8 L4" );
		_manager.GenerateGpuTerrainWorld();
		_phase4RegularSoakStartTimestamp = 0;
		_phase = BenchmarkPhase.WaitFinalStationarySoak;
	}

	private void RestoreFinalStationarySoakSettings()
	{
		_manager.GpuTerrainLodPolicy = _originalGpuTerrainLodPolicy;
		_manager.GpuClipboxBlocksPerAxis = _originalGpuClipboxBlocksPerAxis;
		_manager.GpuClipboxLevelCount = _originalGpuClipboxLevelCount;
		_manager.GpuClipboxMatchChunkRadius = _originalGpuClipboxMatchChunkRadius;
		_manager.ChunkRadius = _originalChunkRadius;
	}

	private void CompleteScenarioAndWarmup( BenchmarkPhase next )
	{
		CompleteScenario();
		_phase = next;
		StartWarmup();
	}

	private void CompleteScenarioAndReset( BenchmarkPhase next )
	{
		CompleteScenario();
		_phaseAfterReset = next;
		_manager.GenerateWorld();
		_phase = BenchmarkPhase.WaitReset;
		_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
	}

	private BenchmarkPhase _phaseAfterWarmup;

	private void StartWarmup()
	{
		_phaseAfterWarmup = _phase;
		_warmupFramesRemaining = WarmupFrames;
		if ( _warmupFramesRemaining <= 0 )
		{
			AdvanceAfterWarmup();
			return;
		}
		_phase = BenchmarkPhase.Warmup;
		_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
	}

	private void AdvanceAfterWarmup()
	{
		_phase = _phaseAfterWarmup;
		_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
	}

	private void BuildVariedEditFixture()
	{
		_variedEdits.Clear();
		var voxel = _manager.VoxelSize;
		var seam = _manager.ChunkSize * voxel;
		_variedEdits.Add( new EditCommand( new Vector3( 0, 0, 0 ), voxel * 1.5f, voxel * 0.75f ) );
		_variedEdits.Add( new EditCommand( new Vector3( seam, 0, 0 ), voxel * 2.0f, voxel ) );
		_variedEdits.Add( new EditCommand( new Vector3( 0, seam, 0 ), voxel * 2.5f, -voxel ) );
		_variedEdits.Add( new EditCommand( new Vector3( seam, seam, 0 ), voxel * 3.0f, voxel * 1.5f ) );
		_variedEdits.Add( new EditCommand( new Vector3( -seam, seam, 0 ), voxel * 4.0f, -voxel * 1.5f ) );
		_variedEdits.Add( new EditCommand( new Vector3( seam * 0.5f, -seam * 0.5f, 0 ), voxel * 2.25f, voxel ) );
		_variedEdits.Add( new EditCommand( new Vector3( -seam * 0.5f, -seam * 0.5f, 0 ), voxel * 3.5f, -voxel ) );
		_variedEdits.Add( new EditCommand( new Vector3( voxel * 4.0f, voxel * 7.0f, voxel ), voxel * 2.0f, voxel * 0.5f ) );
	}

	private void BeginSeamEditScenario()
	{
		BeginScenario( "chunk_seam_edit_coherence", "One edit centered on a four-chunk intersection; visual and collision meshes must publish together" );
		var seam = _manager.ChunkSize * _manager.VoxelSize;
		ApplyLocalEdit( new Vector3( seam, seam, 0.0f ), _manager.VoxelSize * 3.0f, _manager.VoxelSize * 1.5f );
		_phase = BenchmarkPhase.WaitSeamEdit;
	}

	private void BeginLiveConfigurationScenario()
	{
		BeginScenario( "live_chunk_radius_reconfiguration", "Inspector-style chunk radius change regenerates the authoritative world without restarting play" );
		_liveConfigurationChunkRadius = _originalChunkRadius > 1 ? _originalChunkRadius - 1 : 2;
		_manager.ChunkRadius = _liveConfigurationChunkRadius;
		_phase = BenchmarkPhase.WaitLiveConfiguration;
	}

	private void BeginPlayerTraversal( TraversalPath path, string scenarioName, string description, float speed = 0.0f, bool requireCollision = false )
	{
		BeginScenario( scenarioName, description );
		_traversalPlayer = Scene.GetAllComponents<PlayerController>().FirstOrDefault();
		if ( _traversalPlayer is null )
		{
			FailRun( $"{scenarioName} requires a PlayerController" );
			return;
		}

		_traversalPath = path;
		_traversalStartWorldPosition = _traversalPlayer.WorldPosition;
		_traversalStartLocalPosition = _manager.GameObject.WorldTransform.PointToLocal( _traversalStartWorldPosition );
		_traversalLastWorldPosition = _traversalStartWorldPosition;
		_traversalInitialCachedChunkCount = _manager.LoadedChunkCount;
		_traversalDistanceTravelled = 0.0f;
		_activeTraversalSpeed = speed > 0.0f ? speed : TraversalSpeed;
		_requireTraversalCollision = requireCollision;
		_traversalLoopLength = path == TraversalPath.Infinity ? BuildInfinityPath() : TraversalDistance * 2.0f;
		_phase = requireCollision ? BenchmarkPhase.RunHighSpeedCollisionTraversal : path switch
		{
			TraversalPath.Infinity => BenchmarkPhase.RunInfinityTraversal,
			TraversalPath.Line => BenchmarkPhase.RunLineTraversal,
			TraversalPath.Vertical => BenchmarkPhase.RunGpuMovementVertical,
			_ => BenchmarkPhase.RunDiagonalTraversal
		};
	}

	private void RunPlayerTraversal( BenchmarkPhase waitPhase )
	{
		var totalDistance = _traversalLoopLength * TraversalLoopCount;
		_traversalDistanceTravelled = System.MathF.Min( totalDistance, _traversalDistanceTravelled + _activeTraversalSpeed * Time.Delta );
		if ( _traversalDistanceTravelled >= totalDistance )
		{
			MoveTraversalPlayer( _traversalStartWorldPosition );
			_phase = waitPhase;
			return;
		}

		var loopDistance = _traversalDistanceTravelled % _traversalLoopLength;
		var offset = _traversalPath switch
		{
			TraversalPath.Infinity => SampleInfinityPath( loopDistance ),
			TraversalPath.Line => SampleOutAndBackPath( loopDistance, false ),
			TraversalPath.Vertical => SampleOutAndBackPath( loopDistance, false, true ),
			_ => SampleOutAndBackPath( loopDistance, true, false )
		};
		var worldPosition = _manager.GameObject.WorldTransform.PointToWorld( _traversalStartLocalPosition + offset );
		if ( _requireTraversalCollision && !_manager.IsCollisionReadyAtWorldPosition( worldPosition ) )
		{
			FailRun( $"high-speed traversal reached {worldPosition} before its terrain collider was ready" );
			return;
		}

		var velocity = Time.Delta > 0.0f ? (worldPosition - _traversalLastWorldPosition) / Time.Delta : Vector3.Zero;
		_traversalLastWorldPosition = worldPosition;
		MoveTraversalPlayer( worldPosition, _requireTraversalCollision ? velocity : Vector3.Zero );
	}

	private float BuildInfinityPath()
	{
		var halfDistance = TraversalDistance * 0.5f;
		_infinityPathPositions[0] = Vector3.Zero;
		_infinityPathDistances[0] = 0.0f;
		for ( var index = 1; index <= InfinityPathSampleCount; index++ )
		{
			var angle = index * (System.MathF.PI * 2.0f / InfinityPathSampleCount);
			var sine = System.MathF.Sin( angle );
			var position = new Vector3( sine * halfDistance, sine * System.MathF.Cos( angle ) * halfDistance, 0.0f );
			_infinityPathPositions[index] = position;
			var segment = position - _infinityPathPositions[index - 1];
			_infinityPathDistances[index] = _infinityPathDistances[index - 1] + System.MathF.Sqrt( segment.LengthSquared );
		}
		return _infinityPathDistances[^1];
	}

	private Vector3 SampleInfinityPath( float distance )
	{
		var lower = 0;
		var upper = InfinityPathSampleCount;
		while ( lower + 1 < upper )
		{
			var middle = (lower + upper) / 2;
			if ( _infinityPathDistances[middle] <= distance ) lower = middle;
			else upper = middle;
		}

		var segmentLength = _infinityPathDistances[upper] - _infinityPathDistances[lower];
		var fraction = segmentLength > 0.0f ? (distance - _infinityPathDistances[lower]) / segmentLength : 0.0f;
		return _infinityPathPositions[lower] + (_infinityPathPositions[upper] - _infinityPathPositions[lower]) * fraction;
	}

	private Vector3 SampleOutAndBackPath( float distance, bool diagonal, bool vertical = false )
	{
		var halfDistance = TraversalDistance * 0.5f;
		float signedDistance;
		if ( distance < halfDistance ) signedDistance = distance;
		else if ( distance < halfDistance + TraversalDistance ) signedDistance = halfDistance - (distance - halfDistance);
		else signedDistance = -halfDistance + (distance - halfDistance - TraversalDistance);

		if ( vertical ) return new Vector3( 0.0f, 0.0f, signedDistance );
		if ( !diagonal ) return new Vector3( signedDistance, 0.0f, 0.0f );
		const float inverseSquareRootOfTwo = 0.70710678118f;
		return new Vector3( signedDistance * inverseSquareRootOfTwo, signedDistance * inverseSquareRootOfTwo, 0.0f );
	}

	private void MoveTraversalPlayer( Vector3 worldPosition, Vector3 velocity = default )
	{
		_traversalPlayer.WorldPosition = worldPosition;
		_manager.RecordPlayerTraversalUpdate();
		if ( _traversalPlayer.Body is null ) return;
		_traversalPlayer.Body.Velocity = velocity;
		_traversalPlayer.Body.AngularVelocity = Vector3.Zero;
	}

	private void CompleteHighSpeedCollisionTraversal()
	{
		_requireTraversalCollision = false;
		if ( _manager.VisualBackend == VoxelVisualBackendMode.CpuChunks )
		{
			CompletePlayerTraversalAndReset( _phaseAfterHighSpeedCollisionTraversal );
			return;
		}

		CompleteScenario( gpuTerrain: _manager.CaptureGpuTerrainDiagnostics() );
		MoveTraversalPlayer( _traversalStartWorldPosition );
		_phase = _phaseAfterHighSpeedCollisionTraversal;
		StartWarmup();
	}

	private void CompletePlayerTraversalAndReset( BenchmarkPhase nextPhase )
	{
		if ( !ValidatePlayerTraversal() ) return;
		CompleteScenarioAndReset( nextPhase );
	}

	private void CompletePostEditPlayerTraversal()
	{
		if ( !ValidatePlayerTraversal() ) return;
		CompleteScenario();
		if ( Mode == VoxelTerrainBenchmarkMode.CpuOnly )
		{
			FinalizeCompletedRun();
			return;
		}

		_phase = BenchmarkPhase.StartFinalStationarySoak;
		StartWarmup();
	}

	private bool ValidatePlayerTraversal()
	{
		var scenarioName = _sampler?.Name ?? _traversalPath.ToString();
		var diagnostics = _manager.CaptureTerrainDiagnostics();
		if ( _manager.LoadedChunkCount <= _traversalInitialCachedChunkCount )
		{
			FailRun( $"{scenarioName} did not generate chunks beyond the starting radius" );
			return false;
		}
		if ( diagnostics.ActiveVisualChunks != _manager.DesiredChunkCount )
		{
			FailRun( $"{scenarioName} active mesh count {diagnostics.ActiveVisualChunks} did not match desired count {_manager.DesiredChunkCount}" );
			return false;
		}
		if ( _manager.ActiveChunkGameObjectCount != _manager.DesiredChunkCount )
		{
			FailRun( $"{scenarioName} chunk object count {_manager.ActiveChunkGameObjectCount} did not match desired count {_manager.DesiredChunkCount}" );
			return false;
		}
		return true;
	}

	private void CompleteGpuPlayerTraversal( string scenarioName, BenchmarkPhase nextPhase )
	{
		var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
		if ( diagnostics.ClipboxTransitionCapacity > 0 &&
			(diagnostics.TransitionResidentBlocks != diagnostics.ClipboxTransitionActiveSlotCount ||
			 diagnostics.TransitionPendingRequests != 0 ||
			 diagnostics.TransitionBlockedRequests != 0 ||
			 diagnostics.TransitionGeometryReadbackBytes != 0 ||
			 diagnostics.TransitionCpuSdfEvaluations != 0) )
		{
			FailRun( $"{scenarioName} did not settle coherent transition residency: residents={diagnostics.TransitionResidentBlocks}, active={diagnostics.ClipboxTransitionActiveSlotCount}, pending={diagnostics.TransitionPendingRequests}, blocked={diagnostics.TransitionBlockedRequests}." );
			return;
		}
		_pendingGpuPhase3BProof = VoxelGpuPhase3BProof.ValidateMovement( scenarioName, diagnostics, _manager.CaptureTerrainDiagnostics() );
		CompleteScenario( gpuTerrain: diagnostics, gpuPhase3BProof: _pendingGpuPhase3BProof );
		if ( !_pendingGpuPhase3BProof.Passed )
		{
			FailRun( $"Phase 3B movement failed: {_pendingGpuPhase3BProof.Failure}" );
			return;
		}

		MoveTraversalPlayer( _traversalStartWorldPosition );
		_phase = nextPhase;
		StartWarmup();
	}

	private void RunVariedEdits()
	{
		if ( _editIndex >= _variedEdits.Count )
		{
			_phase = BenchmarkPhase.WaitVariedEdits;
			return;
		}
		if ( !CanApplyNextEdit() ) return;
		var edit = _variedEdits[_editIndex++];
		ApplyLocalEdit( edit.LocalPosition, edit.Radius, edit.Displacement );
		ScheduleNextEdit();
	}

	private void BeginSustainedScenario( string name, string description, bool dig )
	{
		BeginScenario( name, description );
		_editIndex = 0;
		_nextEditTimestamp = 0;
		_phase = dig ? BenchmarkPhase.RunSustainedDig : BenchmarkPhase.RunSustainedPlace;
	}

	private void RunSustainedEdits( bool dig, BenchmarkPhase waitPhase )
	{
		if ( _editIndex >= SustainedEditCount )
		{
			_phase = waitPhase;
			return;
		}
		if ( !CanApplyNextEdit() ) return;
		var localPosition = dig ? GetWorldSweepAndDepthPosition( _editIndex ) : GetWorldWidePlacementPosition( _editIndex );
		var displacement = _manager.VoxelSize * (dig ? 0.8f : -0.8f);
		ApplyLocalEdit( localPosition, _manager.VoxelSize * 3.0f, displacement );
		_editIndex++;
		ScheduleNextEdit();
	}

	private Vector3 GetWorldSweepAndDepthPosition( int index )
	{
		var surfaceEditCount = System.Math.Max( 1, SustainedEditCount * 2 / 3 );
		if ( index >= surfaceEditCount )
		{
			var shaftIndex = index - surfaceEditCount;
			var shaftLayer = shaftIndex / 3;
			var depth = -shaftLayer * _manager.VoxelSize * 0.75f;
			return new Vector3( 0.0f, 0.0f, depth );
		}

		var gridWidth = (int)System.Math.Ceiling( System.Math.Sqrt( surfaceEditCount ) );
		var row = index / gridWidth;
		var column = index % gridWidth;
		if ( (row & 1) != 0 ) column = gridWidth - 1 - column;
		var maximumRow = System.Math.Max( 1, (surfaceEditCount - 1) / gridWidth );
		var xFraction = column / (float)System.Math.Max( 1, gridWidth - 1 );
		var yFraction = row / (float)maximumRow;
		var worldHalfExtent = System.MathF.Max( _manager.VoxelSize, (_manager.ChunkRadius * _manager.ChunkSize - 4) * _manager.VoxelSize );
		return new Vector3(
			(xFraction * 2.0f - 1.0f) * worldHalfExtent,
			(yFraction * 2.0f - 1.0f) * worldHalfExtent,
			0.0f
		);
	}

	private Vector3 GetWorldWidePlacementPosition( int index )
	{
		var progress = index / (float)System.Math.Max( 1, SustainedEditCount - 1 );
		var angle = progress * System.MathF.PI * 12.0f;
		var worldHalfExtent = System.MathF.Max( _manager.VoxelSize, (_manager.ChunkRadius * _manager.ChunkSize - 4) * _manager.VoxelSize );
		var radius = worldHalfExtent * System.MathF.Sqrt( progress );
		return new Vector3( System.MathF.Cos( angle ) * radius, System.MathF.Sin( angle ) * radius, 0.0f );
	}

	private bool CanApplyNextEdit()
	{
		return _nextEditTimestamp == 0 || System.Diagnostics.Stopwatch.GetTimestamp() >= _nextEditTimestamp;
	}

	private void ScheduleNextEdit()
	{
		_nextEditTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(SustainedEditIntervalSeconds * System.Diagnostics.Stopwatch.Frequency);
	}

	private void ApplyLocalEdit( Vector3 localPosition, float radius, float displacement )
	{
		var worldPosition = _manager.GameObject.WorldTransform.PointToWorld( localPosition );
		var editStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var changedChunks = _manager.DisplaceSdf( worldPosition, radius, displacement );
		_sampler?.RecordEdit( changedChunks, System.Diagnostics.Stopwatch.GetElapsedTime( editStart ).TotalMilliseconds );
	}

	private bool PhaseTimedOut()
	{
		return _phaseStartTimestamp != 0 && System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp ).TotalSeconds > ScenarioTimeoutSeconds;
	}

	private void FinalizeCompletedRun()
	{
		var baselines = _isReproductionRun && _reproductionBaselines is not null
			? _reproductionBaselines
			: LoadPreviousScenarioMeasurements();
		ApplyComparisons( baselines );

		var hasMajorOutlier = _comparisons.Values.Any( comparison => comparison.MajorOutlier );
		var runPassed = GetSuiteCompletenessFailure() is null && _results.All( result => result.Passed );
		if ( !_isReproductionRun && runPassed && hasMajorOutlier )
		{
			foreach ( var comparison in _comparisons.Values ) comparison.ReproductionStatus = "scheduled";
			var originalRunId = _runId;
			if ( !WriteReports() )
			{
				_phase = BenchmarkPhase.Failed;
				return;
			}

			_isReproductionRun = true;
			_reproductionOfRunId = originalRunId;
			_reproductionBaselines = baselines;
			_reproductionTargets = _comparisons.ToDictionary(
				entry => entry.Key,
				entry => entry.Value.OutlierMetrics.Split( '|', System.StringSplitOptions.RemoveEmptyEntries ).ToHashSet() );
			Log.Warning( $"Voxel terrain benchmark detected a >= {MajorOutlierThresholdPercent:F1}% change; automatically repeating the complete suite against the same baseline." );
			BeginRun( true, true );
			return;
		}

		foreach ( var comparison in _comparisons.Values )
		{
			if ( !comparison.HasBaseline )
			{
				comparison.ReproductionStatus = "no_baseline";
			}
			else if ( !_isReproductionRun )
			{
				comparison.ReproductionStatus = "not_needed";
			}
			else if ( _reproductionTargets is null || !_reproductionTargets.TryGetValue( comparison.ScenarioName, out var targets ) || targets.Count == 0 )
			{
				comparison.ReproductionStatus = "control";
			}
			else
			{
				var reproduced = targets.Any( metric => comparison.PercentChanges.TryGetValue( metric, out var change ) && System.Math.Abs( change ) >= MajorOutlierThresholdPercent );
				comparison.ReproductionStatus = reproduced ? "reproduced" : "not_reproduced";
			}
		}

		_phase = WriteReports() ? BenchmarkPhase.Complete : BenchmarkPhase.Failed;
		RestoreWorldSettings();
	}

	private Dictionary<string, PreviousScenarioMeasurement> LoadPreviousScenarioMeasurements()
	{
		var measurements = new Dictionary<string, PreviousScenarioMeasurement>();
		if ( !FileSystem.Data.FileExists( HistoryJsonLinesPath ) ) return measurements;

		var currentCpu = Sandbox.Engine.SystemInfo.ProcessorName;
		var currentGpu = Sandbox.Engine.SystemInfo.Gpu;
		var configurationId = ConfigurationId;
		var lines = FileSystem.Data.ReadAllText( HistoryJsonLinesPath )
			.Split( '\n', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries );

		for ( var index = lines.Length - 1; index >= 0 && measurements.Count < SelectedRequiredScenarios.Length; index-- )
		{
			try
			{
				using var document = System.Text.Json.JsonDocument.Parse( lines[index] );
				var root = document.RootElement;
				if ( !root.TryGetProperty( "suite_version", out var suiteVersion ) || suiteVersion.GetInt32() != SuiteVersion ) continue;
				if ( !root.TryGetProperty( "suite_complete", out var suiteComplete ) || !suiteComplete.GetBoolean() ) continue;
				if ( !root.TryGetProperty( "passed", out var passed ) || !passed.GetBoolean() ) continue;
				if ( !root.TryGetProperty( "scenario", out var scenarioElement ) ) continue;
				var scenario = scenarioElement.GetString();
				if ( string.IsNullOrWhiteSpace( scenario ) || measurements.ContainsKey( scenario ) || !SelectedRequiredScenarios.Contains( scenario ) ) continue;
				if ( !JsonStringEquals( root, "cpu", currentCpu ) || !JsonStringEquals( root, "gpu", currentGpu ) ) continue;
				if ( root.TryGetProperty( "configuration_id", out var priorConfiguration ) && priorConfiguration.GetString() != configurationId ) continue;

				var measurement = new PreviousScenarioMeasurement
				{
					RunId = root.TryGetProperty( "run_id", out var runId ) ? runId.GetString() : string.Empty
				};
				foreach ( var metric in ComparisonMetrics )
				{
					if ( root.TryGetProperty( metric.Name, out var value ) && value.TryGetDouble( out var number ) )
					{
						measurement.Values[metric.Name] = number;
					}
				}
				measurements[scenario] = measurement;
			}
			catch ( System.Text.Json.JsonException )
			{
				// Historical rows are append-only. Ignore a malformed legacy row and keep searching.
			}
		}

		return measurements;
	}

	private void ApplyComparisons( Dictionary<string, PreviousScenarioMeasurement> baselines )
	{
		_comparisons.Clear();
		foreach ( var result in _results )
		{
			var comparison = new ScenarioComparison { ScenarioName = result.Name };
			if ( baselines.TryGetValue( result.Name, out var baseline ) )
			{
				comparison.HasBaseline = true;
				comparison.BaselineRunId = baseline.RunId;
				var outliers = new List<string>();
				foreach ( var metric in ComparisonMetrics )
				{
					if ( !baseline.Values.TryGetValue( metric.Name, out var previous ) || System.Math.Abs( previous ) < 0.000001 ) continue;
					var change = (ReadComparisonMetric( result, metric.Name ) - previous) / System.Math.Abs( previous ) * 100.0;
					comparison.PercentChanges[metric.Name] = change;
					comparison.MaximumAbsolutePercent = System.Math.Max( comparison.MaximumAbsolutePercent, System.Math.Abs( change ) );
					if ( metric.TriggersRerun && System.Math.Abs( change ) >= MajorOutlierThresholdPercent ) outliers.Add( metric.Name );
				}
				comparison.MajorOutlier = outliers.Count > 0;
				comparison.OutlierMetrics = string.Join( "|", outliers );
			}
			_comparisons[result.Name] = comparison;
		}
	}

	private static bool JsonStringEquals( System.Text.Json.JsonElement root, string property, string expected ) =>
		root.TryGetProperty( property, out var value ) && string.Equals( value.GetString(), expected, System.StringComparison.Ordinal );

	private static double ReadComparisonMetric( ScenarioResult result, string metric ) => metric switch
	{
		"avg_fps" => result.AverageFramesPerSecond,
		"one_percent_low_fps" => result.OnePercentLowFramesPerSecond,
		"frame_p95_ms" => result.FrameP95Milliseconds,
		"frame_max_ms" => result.FrameMaximumMilliseconds,
		"gpu_p95_ms" => result.GpuP95Milliseconds,
		"edit_call_p95_ms" => result.EditCallP95Milliseconds,
		"post_edit_settle_ms" => result.PostEditSettleMilliseconds,
		"allocated_bytes" => result.AllocatedBytes,
		"visual_batch_ms" => result.Diagnostics.VisualBatchElapsedMilliseconds,
		"worker_mesh_ms" => result.Diagnostics.WorkerMeshMilliseconds,
		"upload_ms" => result.Diagnostics.MainThreadUploadMilliseconds,
		"stream_chunk_ready_p95_ms" => result.Streaming.RequestToRender.P95Milliseconds,
		"stream_batch_p95_ms" => result.Streaming.BatchCompletion.P95Milliseconds,
		_ => 0.0
	};

	private ScenarioComparison GetComparison( ScenarioResult result ) =>
		_comparisons.TryGetValue( result.Name, out var comparison ) ? comparison : new ScenarioComparison();

	private string ConfigurationId => $"{_manager.ChunkRadius}:{_manager.ChunkSize}:{Number( _manager.VoxelSize )}:{_manager.CpuChunkBuildConcurrency}:exact:{SustainedEditCount}:{Number( SustainedEditIntervalSeconds )}:{Number( TraversalDistance )}:{Number( TraversalSpeed )}:{TraversalLoopCount}";

	private void FailRun( string reason )
	{
		Log.Error( $"Voxel terrain benchmark {_runId} failed: {reason}." );
		if ( _sampler is not null )
		{
			if ( _sampler.Name is "gpu_persistent_static_set" or "gpu_production_render_integration" or "gpu_async_readback_saturation" or "gpu_resource_recreation" )
			{
				var diagnostics = _manager.CaptureGpuTerrainDiagnostics();
				var proof = VoxelGpuPhase2BProof.ValidateStatic( _sampler.Name, diagnostics, _manager.ConfiguredChunkCount );
				CompleteScenario( gpuTerrain: diagnostics, gpuPhase2BProof: proof );
			}
			else
			{
				CompleteScenario();
			}
		}
		WriteReports( reason );
		_phase = BenchmarkPhase.Failed;
		RestoreWorldSettings();
		RestoreCallCountSetting();
		RestorePlayerProtectionSetting();
	}

	private bool WriteReports( string failure = null )
	{
		failure ??= GetSuiteCompletenessFailure();
		try
		{
			FileSystem.Data.CreateDirectory( ReportDirectory );
			AppendHistoryCsv();
			AppendHistoryJsonLines();
			FileSystem.Data.WriteAllText( LatestJsonPath, BuildLatestJson( failure ) );
			FileSystem.Data.WriteAllText( LatestMarkdownPath, BuildMarkdownReport( failure ) );
			FileSystem.Data.WriteAllText( DashboardPath, BuildDashboardHtml() );
			LastReportPath = FileSystem.Data.GetFullPath( LatestMarkdownPath );
			Log.Info( $"Voxel terrain benchmark report: {LastReportPath}" );
			Log.Info( $"Voxel terrain benchmark dashboard: {FileSystem.Data.GetFullPath( DashboardPath )}" );
			RestoreCallCountSetting();
			RestorePlayerProtectionSetting();
			return failure is null && _results.All( result => result.Passed );
		}
		catch ( System.Exception exception )
		{
			Log.Error( $"Voxel terrain benchmark could not write reports: {exception.Message}" );
			RestoreCallCountSetting();
			RestorePlayerProtectionSetting();
			return false;
		}
	}

	private string GetSuiteCompletenessFailure()
	{
		if ( _results.Count != SelectedRequiredScenarios.Length )
		{
			return $"incomplete suite: expected {SelectedRequiredScenarios.Length} scenarios, recorded {_results.Count}";
		}

		for ( var index = 0; index < SelectedRequiredScenarios.Length; index++ )
		{
			if ( _results[index].Name != SelectedRequiredScenarios[index] )
			{
				return $"invalid suite order at {index}: expected {SelectedRequiredScenarios[index]}, recorded {_results[index].Name}";
			}
		}

		return null;
	}

	private bool IsSuiteComplete => GetSuiteCompletenessFailure() is null;

	private void RestoreCallCountSetting()
	{
		if ( !_callCountSettingCaptured || _manager is null ) return;
		_manager.CaptureCallCounts = _originalCaptureCallCounts;
	}

	private void RestorePlayerProtectionSetting()
	{
		_holdBenchmarkPlayersAtOrigin = false;
		RestoreBenchmarkPlayerGravity();
		_manager?.SetBenchmarkPlayerProtection( false );
	}

	private void RunGpuRealtimeEdits( BenchmarkPhase waitPhase = BenchmarkPhase.WaitGpuRealtimeEdits )
	{
		if ( _editIndex >= RealtimeSurfaceEditCount )
		{
			_phase = waitPhase;
			return;
		}
		if ( !CanApplyNextEdit() ) return;
		var surfaceZ = _manager.SimplexBaseHeight * _manager.VoxelSize;
		var localPosition = new Vector3( _editIndex * _manager.VoxelSize * 0.5f, 0.0f, surfaceZ );
		ApplyLocalEdit( localPosition, _manager.VoxelSize * 4.0f, _manager.VoxelSize * 2.0f );
		_editIndex++;
		_nextEditTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(0.05 * System.Diagnostics.Stopwatch.Frequency);
	}

	private void RunGpuAimedBrushEdits()
	{
		if ( _editIndex >= RealtimeSurfaceEditCount )
		{
			_phase = BenchmarkPhase.WaitGpuAimedBrushEdits;
			return;
		}
		if ( !CanApplyNextEdit() ) return;

		var localX = _editIndex * _manager.VoxelSize * 0.5f;
		var rayStartZ = (_manager.SimplexBaseHeight + _manager.SimplexAmplitude + 8.0f) * _manager.VoxelSize;
		var rayDistance = (_manager.SimplexAmplitude * 2.0f + 32.0f) * _manager.VoxelSize;
		var rayStart = _manager.GameObject.WorldTransform.PointToWorld( new Vector3( localX, 0.0f, rayStartZ ) );
		var editStart = System.Diagnostics.Stopwatch.GetTimestamp();
		if ( !_manager.TryRaycastSdf( rayStart, Vector3.Down, rayDistance, out var hitPosition ) )
		{
			FailRun( $"gpu_aimed_brush_edits_20hz missed authoritative terrain at edit {_editIndex}." );
			return;
		}

		var changedChunks = _manager.DisplaceSdf( hitPosition, _manager.VoxelSize * 4.0f, _manager.VoxelSize * 2.0f );
		_sampler?.RecordEdit( changedChunks, System.Diagnostics.Stopwatch.GetElapsedTime( editStart ).TotalMilliseconds );
		_editIndex++;
		_nextEditTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(0.05 * System.Diagnostics.Stopwatch.Frequency);
	}

	private void CaptureAndFreezeBenchmarkPlayers()
	{
		if ( _benchmarkGravityStates.Count > 0 ) return;

		foreach ( var controller in Scene.GetAllComponents<PlayerController>() )
		{
			if ( controller.Body is not { } body ) continue;
			_benchmarkGravityStates.Add( new BenchmarkGravityState( body, body.Gravity ) );
			body.Gravity = false;
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
		}
	}

	private void RestoreBenchmarkPlayerGravity()
	{
		foreach ( var state in _benchmarkGravityStates )
		{
			if ( state.Body is null ) continue;
			state.Body.Gravity = state.Gravity;
		}
		_benchmarkGravityStates.Clear();
	}

	private void HoldBenchmarkPlayersAtOrigin()
	{
		foreach ( var controller in Scene.GetAllComponents<PlayerController>() )
		{
			controller.WorldPosition = Vector3.Zero;
			if ( controller.Body is not { } body ) continue;
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
		}
	}

	private void RestoreWorldSettings()
	{
		if ( _cameraSweepCamera is not null )
		{
			_cameraSweepCamera.WorldRotation = _cameraSweepOriginalRotation;
			_cameraSweepCamera = null;
		}
		if ( !_worldSettingsCaptured || _manager is null ) return;
		_worldSettingsCaptured = false;
		var changed = _manager.ChunkRadius != _originalChunkRadius ||
			_manager.VisualBackend != _originalVisualBackend ||
			_manager.GpuTerrainRuleVersion != _originalGpuTerrainRuleVersion ||
			_manager.GpuTerrainLodPolicy != _originalGpuTerrainLodPolicy ||
			_manager.GpuClipboxBlocksPerAxis != _originalGpuClipboxBlocksPerAxis ||
			_manager.GpuClipboxLevelCount != _originalGpuClipboxLevelCount ||
			_manager.GpuClipboxMatchChunkRadius != _originalGpuClipboxMatchChunkRadius;
		_manager.ChunkRadius = _originalChunkRadius;
		_manager.VisualBackend = _originalVisualBackend;
		_manager.GpuTerrainRuleVersion = _originalGpuTerrainRuleVersion;
		_manager.GpuTerrainLodPolicy = _originalGpuTerrainLodPolicy;
		_manager.GpuClipboxBlocksPerAxis = _originalGpuClipboxBlocksPerAxis;
		_manager.GpuClipboxLevelCount = _originalGpuClipboxLevelCount;
		_manager.GpuClipboxMatchChunkRadius = _originalGpuClipboxMatchChunkRadius;
		if ( changed ) _manager.GenerateWorld();
	}

	private void AppendHistoryCsv()
	{
		var comparisonHeader = string.Join( ",", ComparisonMetrics.Select( metric => $"change_{metric.Name}_pct" ) );
		var header = "run_id,suite_version,suite_complete,timestamp_utc,revision,working_tree_dirty,engine_version,cpu,gpu,scenario,description,passed,edits,changed_chunk_events,visual_coherence_violation_frames,elapsed_ms,frames,avg_fps,one_percent_low_fps,point_one_percent_low_fps,min_fps,frame_avg_ms,frame_stddev_ms,frame_p50_ms,frame_p95_ms,frame_p99_ms,frame_p999_ms,frame_max_ms,unaccounted_frame_ms,frames_over_16ms,frames_over_33ms,frames_over_50ms,frames_over_100ms,stutter_events,longest_stutter_frames,gpu_avg_ms,gpu_p95_ms,gpu_max_ms,edit_call_avg_ms,edit_call_p95_ms,edit_call_max_ms,post_edit_settle_ms,edit_throughput_per_second,update_avg_ms,update_max_ms,render_avg_ms,render_max_ms,physics_avg_ms,physics_max_ms,network_avg_ms,network_max_ms,network_out_bytes_per_second,network_in_bytes_per_second,network_ping_ms,maximum_connections,messages_sent,messages_received,allocated_bytes,gc_pause_ms,gen0_gc,gen1_gc,gen2_gc,exceptions,peak_memory_bytes,texture_pool_peak_bytes,texture_pool_non_evictable_peak_bytes,pending_streaming_requests_max,draw_calls_avg,triangles_rendered_avg,objects_rendered_avg,material_changes_avg,loaded_chunks,authoritative_sdf_bytes,uniform_sdf_chunks,visual_chunks,failed_visual_chunks,visual_batch_built_chunks,visual_vertices,visual_triangles,colliders,collision_triangles,player_safety_active,generation_ms,visual_batch_ms,snapshot_wait_ms,snapshot_copy_ms,worker_mesh_ms,upload_ms,gpu_transvoxel_available,gpu_transvoxel_passed,gpu_transvoxel_failure,gpu_transvoxel_vertices,gpu_transvoxel_indices,gpu_transvoxel_active_cells,gpu_transvoxel_overflow_attempts,gpu_transvoxel_buffer_bytes,gpu_transvoxel_submission_ms,gpu_transvoxel_completion_ms,gpu_transvoxel_readback_ms,gpu_transvoxel_batch_size,gpu_transvoxel_surface_blocks,gpu_transvoxel_dispatches,gpu_transvoxel_gpu_publication_passed,gpu_transvoxel_gpu_publication_ms,gpu_transvoxel_cpu_publication_ms," + Lod5OwnershipCsvHeader + ",clipbox_planner_available,clipbox_planner_passed,clipbox_planner_failure,clipbox_planner_cases,clipbox_planner_configurations,clipbox_planner_active_regular_count,clipbox_planner_stable_regular_slots,clipbox_planner_allocated_after_warmup,clipbox_planner_transition_capacity,clipbox_planner_active_transition_count,clipbox_planner_changed_transition_slots,clipbox_planner_transition_ownership_validated,transition_reference_available,transition_reference_passed,transition_reference_failure,transition_reference_cases,transition_reference_orientations,transition_reference_fixture_cases,transition_reference_triangles,transition_reference_boundary_edges,transition_reference_gradient_normals,transition_reference_position_tolerance,transition_reference_tables_validated,transition_gpu_case_available,transition_gpu_case_passed,transition_gpu_case_failure,transition_gpu_case_variants,transition_gpu_case_buffer_bytes,transition_gpu_case_submission_ms,transition_gpu_case_completion_ms,transition_gpu_case_readback_ms,indirect_render_available,indirect_render_passed,indirect_render_failure,indirect_render_tested_command_counts,indirect_render_maximum_command_count,indirect_render_group_size,indirect_render_boundary_command_count,indirect_render_boundary_active_lists,indirect_render_boundary_visible_commands,indirect_render_maximum_active_lists," + GpuPhase2BCsvHeader + ",stream_chunks_completed,stream_chunks_fresh,stream_chunks_cached,stream_batches_completed,stream_sdf_generation_avg_ms,stream_sdf_generation_p95_ms,stream_sdf_generation_max_ms,stream_mesh_queue_avg_ms,stream_mesh_queue_p95_ms,stream_mesh_queue_max_ms,stream_snapshot_avg_ms,stream_snapshot_p95_ms,stream_snapshot_max_ms,stream_worker_mesh_avg_ms,stream_worker_mesh_p95_ms,stream_worker_mesh_max_ms,stream_publication_wait_avg_ms,stream_publication_wait_p95_ms,stream_publication_wait_max_ms,stream_upload_avg_ms,stream_upload_p95_ms,stream_upload_max_ms,stream_chunk_ready_avg_ms,stream_chunk_ready_p95_ms,stream_chunk_ready_max_ms,stream_batch_avg_ms,stream_batch_p95_ms,stream_batch_max_ms,configuration_id,comparison_baseline_run_id,comparison_has_baseline,change_max_abs_pct,outlier_detected,outlier_metrics,reproduction_of_run_id,reproduction_status," + comparisonHeader + "," +
			string.Join( ",", default(VoxelCallCountSnapshot).Enumerate().Select( entry => CallCountKey( entry.Name ) ) );
		header = header.Replace( "upload_ms,gpu_transvoxel_available", "upload_ms,collision_snapshot_wait_ms,collision_snapshot_copy_ms,collision_worker_mesh_ms,collision_model_build_ms,collision_publication_ms,gpu_transvoxel_available" );
		header = header.Replace( GpuPhase2BCsvHeader + ",stream_chunks_completed", GpuPhase2BCsvHeader + "," + GpuClipboxCsvHeader + ",stream_chunks_completed", System.StringComparison.Ordinal );
		header = header.Replace( "clipbox_planner_allocated_after_warmup,indirect_render_available", "clipbox_planner_allocated_after_warmup,clipbox_planner_transition_capacity,clipbox_planner_active_transition_count,clipbox_planner_changed_transition_slots,clipbox_planner_transition_ownership_validated,indirect_render_available", System.StringComparison.Ordinal );
		var builder = new System.Text.StringBuilder();
		if ( FileSystem.Data.FileExists( HistoryCsvPath ) )
		{
			var existing = FileSystem.Data.ReadAllText( HistoryCsvPath ).TrimEnd();
			if ( existing.StartsWith( header, System.StringComparison.Ordinal ) )
			{
				builder.Append( existing );
				builder.AppendLine();
			}
			else
			{
				FileSystem.Data.WriteAllText( $"{ReportDirectory}/history-legacy-{_runId}.csv", existing );
				builder.AppendLine( header );
			}
		}
		else
		{
			builder.AppendLine( header );
		}
		foreach ( var result in _results ) builder.AppendLine( ToCsvRow( result ) );
		FileSystem.Data.WriteAllText( HistoryCsvPath, builder.ToString() );
	}

	private void AppendHistoryJsonLines()
	{
		var builder = new System.Text.StringBuilder();
		if ( FileSystem.Data.FileExists( HistoryJsonLinesPath ) )
		{
			var existingLines = FileSystem.Data.ReadAllText( HistoryJsonLinesPath )
				.Split( '\n', System.StringSplitOptions.RemoveEmptyEntries )
				.Select( NormalizeHistoryJsonLine );
			builder.Append( string.Join( "\n", existingLines ).TrimEnd() );
			builder.AppendLine();
		}
		foreach ( var result in _results ) builder.AppendLine( SerializeScenarioJson( result ) );
		FileSystem.Data.WriteAllText( HistoryJsonLinesPath, builder.ToString() );
	}

	private static string NormalizeHistoryJsonLine( string line )
	{
		return line.Replace( "\"gpu_terrain_structured_debug_report\":,", "\"gpu_terrain_structured_debug_report\":{},", System.StringComparison.Ordinal )
			.Replace( "\"gpu_terrain_structured_debug_report\":}", "\"gpu_terrain_structured_debug_report\":{}}", System.StringComparison.Ordinal );
	}

	private string BuildLatestJson( string failure )
	{
		var scenarios = string.Join( ",\n", _results.Select( result => "    " + SerializeScenarioJson( result ) ) );
		var renderSettings = Application.RenderSettings;
		var vsync = renderSettings is null ? "null" : renderSettings.VSync.ToString().ToLowerInvariant();
		var frameCap = renderSettings is null ? "null" : renderSettings.MaxFrameRate.ToString( System.Globalization.CultureInfo.InvariantCulture );
		return "{\n" +
			$"  \"run_id\":\"{Json( _runId )}\",\n" +
			$"  \"suite_version\":{SuiteVersion},\n" +
			$"  \"suite_complete\":{IsSuiteComplete.ToString().ToLowerInvariant()},\n" +
			$"  \"configuration_id\":\"{Json( ConfigurationId )}\",\n" +
			$"  \"traversal_distance\":{Number( TraversalDistance )},\n" +
			$"  \"traversal_speed\":{Number( TraversalSpeed )},\n" +
			$"  \"traversal_loops\":{TraversalLoopCount},\n" +
			$"  \"major_outlier_threshold_percent\":{Number( MajorOutlierThresholdPercent )},\n" +
			$"  \"automatic_reproduction\":{_isReproductionRun.ToString().ToLowerInvariant()},\n" +
			$"  \"reproduction_of_run_id\":{(_reproductionOfRunId is null ? "null" : "\"" + Json( _reproductionOfRunId ) + "\"")},\n" +
			$"  \"benchmark_mode\":\"{Mode}\",\n" +
			$"  \"required_scenarios\":[{string.Join( ",", SelectedRequiredScenarios.Select( name => "\"" + Json( name ) + "\"" ) )}],\n" +
			$"  \"executed_scenarios\":[{string.Join( ",", _results.Select( result => "\"" + Json( result.Name ) + "\"" ) )}],\n" +
			$"  \"timestamp_utc\":\"{System.DateTime.UtcNow:O}\",\n" +
			$"  \"revision\":\"{Json( Revision )}\",\n" +
			$"  \"working_tree_dirty\":{WorkingTreeDirty.ToString().ToLowerInvariant()},\n" +
			$"  \"engine_version\":\"{Json( Application.Version )}\",\n" +
			"  \"gpu_phase2b_visual_backend\":\"gpu_persistent_fixed_lod\",\n" +
			"  \"gpu_phase2b_procedural_rule_version\":1,\n" +
			"  \"gpu_phase2b_lod_policy\":\"fixed_lod_0\",\n" +
			$"  \"gpu_phase2b_chunk_size\":{_manager.ChunkSize},\n" +
			$"  \"gpu_phase2b_voxel_size\":{Number( _manager.VoxelSize )},\n" +
			$"  \"gpu_phase2b_batch_capacity\":{VoxelGpuScratchArena.MaximumBatchSize},\n" +
			$"  \"gpu_phase2b_scratch_ring_count\":{VoxelGpuScratchArena.RingSize},\n" +
			$"  \"gpu_phase2b_multi_draw_command_limit\":{VoxelGpuCapabilities.Detect().IndirectCommandGroupSize},\n" +
			$"  \"gpu_phase2b_vertex_pool_capacity\":{_manager.EffectiveGpuVertexPoolCapacity},\n" +
			$"  \"gpu_phase2b_index_pool_capacity\":{_manager.EffectiveGpuIndexPoolCapacity},\n" +
			"  \"gpu_phase2b_vertex_addressing\":\"world_space_vertex_fallback\",\n" +
			$"  \"gpu_phase2b_retirement_mechanism\":\"frame_epoch_{VoxelGpuCapabilities.RetirementEpochs}\",\n" +
			$"  \"resolution\":\"{Screen.Width:F0}x{Screen.Height:F0}\",\n" +
			$"  \"vsync\":{vsync},\n" +
			$"  \"frame_cap\":{frameCap},\n" +
			"  \"coverage\":{\"activation\":\"optional\",\"active_run_instrumentation\":\"mandatory\",\"terrain_streaming\":\"measured\",\"terrain_persistence\":\"not_implemented\",\"terrain_replication\":\"not_implemented\",\"engine_network_observation\":\"measured\",\"memory_and_render_cache\":\"measured\",\"call_frequency\":\"measured\"},\n" +
			$"  \"failure\":{(failure is null ? "null" : "\"" + Json( failure ) + "\"")},\n" +
			$"  \"scenarios\":[\n{scenarios}\n  ]\n" +
			"}\n";
	}

	private string BuildMarkdownReport( string failure )
	{
		var passed = failure is null && _results.All( result => result.Passed );
		var worstP95 = _results.Count > 0 ? _results.Max( result => result.FrameP95Milliseconds ) : 0.0;
		var worstMax = _results.Count > 0 ? _results.Max( result => result.FrameMaximumMilliseconds ) : 0.0;
		var renderSettings = Application.RenderSettings;
		var vsync = renderSettings is null ? "unknown" : renderSettings.VSync.ToString();
		var frameCap = renderSettings is null ? "unknown" : renderSettings.MaxFrameRate.ToString( System.Globalization.CultureInfo.InvariantCulture );
		var builder = new System.Text.StringBuilder();
		builder.AppendLine( "# Voxel Terrain Benchmark Report" );
		builder.AppendLine();
		builder.AppendLine( $"- Outcome: **{(passed ? "PASS" : "FAIL")}**" );
		builder.AppendLine( $"- Run: `{_runId}`" );
		builder.AppendLine( $"- Mode: `{Mode}`" );
		builder.AppendLine( $"- Suite: `v{SuiteVersion}`, completeness: **{(IsSuiteComplete ? "COMPLETE" : "INCOMPLETE")}** (`{_results.Count}/{SelectedRequiredScenarios.Length}` scenarios)" );
		builder.AppendLine( $"- Revision: `{Revision}`" );
		builder.AppendLine( $"- Working tree dirty: `{WorkingTreeDirty}`" );
		builder.AppendLine( $"- Engine: `{Application.Version}` ({Application.VersionDate:O})" );
		builder.AppendLine( $"- CPU: `{Sandbox.Engine.SystemInfo.ProcessorName}` ({Sandbox.Engine.SystemInfo.ProcessorCount:F0} logical processors)" );
		builder.AppendLine( $"- GPU: `{Sandbox.Engine.SystemInfo.Gpu}` ({FormatBytes( (long)Sandbox.Engine.SystemInfo.GpuMemory )})" );
		builder.AppendLine( $"- Display: `{Screen.Width:F0}x{Screen.Height:F0}`, VSync `{vsync}`, frame cap `{frameCap}`" );
		builder.AppendLine( $"- Configuration: `{_manager.ChunkRadius}` radius, `{_manager.ConfiguredChunkCount}` chunks, `{_manager.ChunkSize}^3` cells, `{_manager.CpuChunkBuildConcurrency}` visual workers, collision `exact visual mesh`" );
		builder.AppendLine( $"- Player traversal: `{TraversalDistance:F0}` units peak-to-peak, `{TraversalSpeed:F0}` units/s, `{TraversalLoopCount}` loop(s) per route" );
		builder.AppendLine( $"- Outlier rule: absolute change `>= {MajorOutlierThresholdPercent:F1}%` on stable comparison metrics triggers one complete-suite reproduction run" );
		if ( _isReproductionRun ) builder.AppendLine( $"- Reproduction of run: `{_reproductionOfRunId}`" );
		builder.AppendLine( $"- Worst frame p95/max: `{worstP95:F2} / {worstMax:F2} ms`" );
		builder.AppendLine( $"- Worst unaccounted frame time: `{(_results.Count > 0 ? _results.Max( result => result.UnaccountedFrameMaximumMilliseconds ) : 0.0):F2} ms`" );
		if ( failure is not null ) builder.AppendLine( $"- Failure: `{failure}`" );
		builder.AppendLine();
		builder.AppendLine( "## Scenario overview" );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Result | Average FPS | 1% low | 0.1% low | Frame p95 / max | Unaccounted max | Stutters | Edits | Rebuilt chunks | Coherence violations |" );
		builder.AppendLine( "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|" );
		foreach ( var result in _results )
		{
			builder.AppendLine( $"| {result.Name} | {(result.Passed ? "PASS" : "FAIL")} | {result.AverageFramesPerSecond:F1} | {result.OnePercentLowFramesPerSecond:F1} | {result.PointOnePercentLowFramesPerSecond:F1} | {result.FrameP95Milliseconds:F2} / {result.FrameMaximumMilliseconds:F2} ms | {result.UnaccountedFrameMaximumMilliseconds:F2} ms | {result.StutterEvents:N0} | {result.EditCount:N0} | {result.Diagnostics.VisualBatchBuiltChunks:N0} | {result.VisualCoherenceViolationFrames:N0} |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Persistent GPU terrain Phase 2B" );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Result | Residents / requested | Async count readbacks | Request-to-visible avg / p95 / max | Batch completion avg / p95 / max | Count / emit submit per block | Visible draws | Pool used / peak / capacity | Scratch | Geometry readback | Backpressure / allocation failures | Capacity state | Detail |" );
		builder.AppendLine( "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|" );
		foreach ( var result in _results.Where( result => result.GpuTerrain.HasValue || result.GpuPhase2BProof.HasValue ) )
		{
			var terrain = result.GpuTerrain ?? default;
			var proof = result.GpuPhase2BProof ?? default;
			builder.AppendLine( $"| {result.Name} | {(result.Passed ? "PASS" : "FAIL")} | {terrain.ResidentBlocks:N0} / {terrain.RequestedBlocks:N0} | {terrain.CountReadbackCount:N0} at {terrain.CountReadbackAverageMilliseconds:F3} ms avg | {Timing( terrain.RequestToVisible )} | {Timing( terrain.BatchCompletion )} | {terrain.CountSubmissionPerBlockMilliseconds:F4} / {terrain.EmitSubmissionPerBlockMilliseconds:F4} ms | {terrain.VisibleDrawCommands:N0} | {FormatBytes( terrain.PoolUsedBytes )} / {FormatBytes( terrain.PeakPoolUsedBytes )} / {FormatBytes( terrain.PoolCapacityBytes )} | {FormatBytes( terrain.ScratchBytes )} | {terrain.GeometryReadbackBytes:N0} B | {terrain.BackpressureEvents:N0} / {terrain.AllocationFailures:N0} | {(terrain.CapacityLimited ? $"limited, blocked={terrain.BlockedRequests:N0}, deferrals={terrain.CapacityDeferrals:N0}" : "unlimited")} | {(proof.Failure ?? terrain.Failure ?? string.Empty).Replace( "|", "\\|" )} |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Production GPU render integration Phase 3A" );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Result | Shader | Standard lighting | Depth prepass | Depth lists | Opaque lists | Failure |" );
		builder.AppendLine( "|---|---|---|---|---|---:|---:|---|" );
		foreach ( var result in _results.Where( result => result.GpuPhase3AProof.HasValue ) )
		{
			var terrain = result.GpuTerrain ?? default;
			var proof = result.GpuPhase3AProof.Value;
			builder.AppendLine( $"| {result.Name} | {(result.Passed ? "PASS" : "FAIL")} | `{terrain.RenderShader}` | {terrain.ProductionLighting} | {terrain.DepthPrepass} | {terrain.DepthPrepassCommandLists} | {terrain.OpaqueCommandLists} | {(proof.Failure ?? terrain.Failure ?? string.Empty).Replace( "|", "\\|" )} |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Observer-driven GPU streaming Phase 3B" );
		builder.AppendLine();
		builder.AppendLine( "GPU movement scenarios use the actual player as the streaming observer. Desired coverage remains logical and may exceed the resident pool; capacity-limited admission protects higher-priority residents, pending requests and publications remain bounded, and all GPU terrain remains in the persistent pool without production geometry readback." );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Result | Desired / resident | Pending requests / publications | Blocked | Queue caps | Visible draws | Request-to-visible p95 | Batch completion p95 | Backpressure | Allocation failures | Stale publications | Failure |" );
		builder.AppendLine( "|---|---|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|" );
		foreach ( var result in _results.Where( result => result.GpuPhase3BProof.HasValue ) )
		{
			var terrain = result.GpuTerrain ?? default;
			var proof = result.GpuPhase3BProof.Value;
			builder.AppendLine( $"| {result.Name} | {(result.Passed ? "PASS" : "FAIL")} | {terrain.DesiredBlocks:N0} / {terrain.ResidentBlocks:N0} | {terrain.PendingRequestCount:N0} / {terrain.PendingPublicationCount:N0} | {terrain.BlockedRequests:N0} | {terrain.PendingRequestCapacity:N0} / {(terrain.QueuesBounded ? "bounded" : "OVERFLOW")} | {terrain.VisibleDrawCommands:N0} | {terrain.RequestToVisible.P95Milliseconds:F3} ms | {terrain.BatchCompletion.P95Milliseconds:F3} ms | {terrain.BackpressureEvents:N0} | {terrain.AllocationFailures:N0} | {terrain.StalePublicationsRejected:N0} | {(proof.Failure ?? terrain.Failure ?? string.Empty).Replace( "|", "\\|" )} |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Frame pacing and edit latency" );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Frame standard deviation | Frames >16 / 33 / 50 / 100 ms | Longest stutter | Brush call avg / p95 / max | Post-edit settle | Edit throughput |" );
		builder.AppendLine( "|---|---:|---:|---:|---:|---:|---:|" );
		foreach ( var result in _results )
		{
			builder.AppendLine( $"| {result.Name} | {result.FrameStandardDeviationMilliseconds:F2} ms | {result.FramesOver16Milliseconds:N0} / {result.FramesOver33Milliseconds:N0} / {result.FramesOver50Milliseconds:N0} / {result.FramesOver100Milliseconds:N0} | {result.LongestStutterFrames:N0} frames | {result.EditCallAverageMilliseconds:F3} / {result.EditCallP95Milliseconds:F3} / {result.EditCallMaximumMilliseconds:F3} ms | {result.PostEditSettleMilliseconds:F2} ms | {result.EditThroughputPerSecond:F2}/s |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Pipeline detail" );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Generation | Visual batch | Snapshot wait | Snapshot copy | Worker mesh | Main upload | Collision snapshot | Collision mesh | Collision model / publication | Collision triangles | Safety released |" );
		builder.AppendLine( "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|" );
		foreach ( var result in _results )
		{
			var diagnostics = result.Diagnostics;
			builder.AppendLine( $"| {result.Name} | {diagnostics.GenerationElapsedMilliseconds:F2} ms | {diagnostics.VisualBatchElapsedMilliseconds:F2} ms | {diagnostics.SnapshotWaitMilliseconds:F2} ms | {diagnostics.SnapshotCopyMilliseconds:F2} ms | {diagnostics.WorkerMeshMilliseconds:F2} ms | {diagnostics.MainThreadUploadMilliseconds:F2} ms | {diagnostics.TotalCollisionSnapshotCopyMilliseconds:F2} ms | {diagnostics.TotalCollisionWorkerMeshMilliseconds:F2} ms | {diagnostics.TotalCollisionModelBuildMilliseconds:F2} / {diagnostics.TotalCollisionPublicationMilliseconds:F2} ms | {diagnostics.CollisionTriangles:N0} | {(!diagnostics.PlayerSafetyActive ? "yes" : "no")} |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Chunk streaming latency" );
		builder.AppendLine();
		builder.AppendLine( "Each value is average / p95 / maximum. Chunk-ready time runs from streaming interest to visual publication; batch time runs until all queued terrain is published." );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Chunks fresh / cached | SDF generation | Mesh queue | SDF snapshot | Worker mesh | Publication wait | Main upload | Request to render | Batch completion |" );
		builder.AppendLine( "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|" );
		foreach ( var result in _results )
		{
			var s = result.Streaming;
			builder.AppendLine( $"| {result.Name} | {s.FreshGeneratedChunks:N0} / {s.CachedChunks:N0} | {Timing( s.SdfGeneration )} | {Timing( s.MeshQueue )} | {Timing( s.SdfSnapshot )} | {Timing( s.WorkerMesh )} | {Timing( s.PublicationWait )} | {Timing( s.MainThreadUpload )} | {Timing( s.RequestToRender )} | {Timing( s.BatchCompletion )} |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Percentage change and outliers" );
		builder.AppendLine();
		builder.AppendLine( "Changes are signed relative to the latest compatible prior run on the same CPU, GPU, suite, and configuration. Positive means the raw metric increased; use metric semantics to decide whether that is better or worse." );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Baseline run | Largest absolute change | Major outlier metrics | Reproduction |" );
		builder.AppendLine( "|---|---|---:|---|---|" );
		foreach ( var result in _results )
		{
			var comparison = GetComparison( result );
			builder.AppendLine( $"| {result.Name} | {(comparison.HasBaseline ? $"`{comparison.BaselineRunId}`" : "none")} | {(comparison.HasBaseline ? comparison.MaximumAbsolutePercent.ToString( "F2", System.Globalization.CultureInfo.InvariantCulture ) + "%" : "n/a")} | {(string.IsNullOrWhiteSpace( comparison.OutlierMetrics ) ? "none" : $"`{comparison.OutlierMetrics}`")} | `{comparison.ReproductionStatus}` |" );
		}
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Metric | Change from previous |" );
		builder.AppendLine( "|---|---|---:|" );
		foreach ( var result in _results )
		{
			var comparison = GetComparison( result );
			foreach ( var metric in ComparisonMetrics )
			{
				var change = comparison.PercentChanges.TryGetValue( metric.Name, out var value ) ? $"{value:+0.00;-0.00;0.00}%" : "n/a";
				builder.AppendLine( $"| {result.Name} | `{metric.Name}` | {change} |" );
			}
		}
		builder.AppendLine();
		builder.AppendLine( "## Call frequency" );
		builder.AppendLine();
		builder.AppendLine( "Counters are scenario deltas. Function counters measure invocations; `samples_*` and `chunks_*` counters measure aggregate work units and are updated once per operation to keep instrumentation overhead low." );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Counter | Count | Per second |" );
		builder.AppendLine( "|---|---|---:|---:|" );
		foreach ( var result in _results )
		{
			foreach ( var entry in result.CallCounts.Enumerate().OrderByDescending( entry => entry.Count ) )
			{
				var perSecond = result.ElapsedMilliseconds > 0.0 ? entry.Count * 1000.0 / result.ElapsedMilliseconds : 0.0;
				builder.AppendLine( $"| {result.Name} | `{entry.Name}` | {entry.Count:N0} | {perSecond:N2} |" );
			}
		}
		builder.AppendLine();
		builder.AppendLine( "## Phase 4 mathematical clipbox planner" );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Result | Cases | Configurations | Active regular blocks | Stable regular slots | Allocated after warm-up | Detail |" );
		builder.AppendLine( "|---|---|---:|---:|---:|---:|---:|---|" );
		foreach ( var result in _results.Where( result => result.ClipboxPlannerProof.HasValue ) )
		{
			var proof = result.ClipboxPlannerProof.Value;
			builder.AppendLine( $"| {result.Name} | {(proof.Passed ? "PASS" : "FAIL")} | {proof.Cases:N0} | {proof.Configurations:N0} | {proof.ActiveRegularCount:N0} | {proof.StableRegularSlots:N0} | {proof.AllocatedBytesAfterWarmup:N0} B | {proof.Failure.Replace( "|", "\\|" )} |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Phase 4 regular clipbox residency" );
		builder.AppendLine();
		builder.AppendLine( "The GPU clipbox report records the committed planner revision, changed toroidal slots, active/stable slot counts, pending revision high-water mark, dropped work, and stationary observer updates. Editor gizmos can use the same resident identities to distinguish LOD, slot, generation, and persistent mesh ranges." );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Policy | Result | Revision | Changed slots | Active / stable | Pending revisions max | Dropped work | Stationary updates | Resident / requested | Detail |" );
		builder.AppendLine( "|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|" );
		foreach ( var result in _results.Where( result => result.GpuTerrain.HasValue && result.GpuTerrain.Value.LodPolicy.StartsWith( "regular_clipbox", System.StringComparison.Ordinal ) ) )
		{
			var terrain = result.GpuTerrain.Value;
			var proof = result.GpuPhase2BProof ?? default;
			builder.AppendLine( $"| {result.Name} | `{terrain.LodPolicy}` | {(result.Passed ? "PASS" : "FAIL")} | {terrain.ClipboxRevision} | {terrain.ClipboxChangedSlots:N0} | {terrain.ClipboxActiveSlotCount:N0} / {terrain.ClipboxStableSlotCount:N0} | {terrain.ClipboxMaximumPendingRevisionCount:N0} | {terrain.ClipboxDroppedWork:N0} | {terrain.ClipboxStationaryUpdates:N0} | {terrain.ResidentBlocks:N0} / {terrain.RequestedBlocks:N0} | {(proof.Failure ?? terrain.Failure ?? string.Empty).Replace( "|", "\\|" )} |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Realtime GPU terrain editing" );
		builder.AppendLine();
		builder.AppendLine( "The edit queue timings measure synchronous work on the brush frame. They do not include deferred mesh publication, which remains visible in the frame, GPU, and post-edit settle metrics above." );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Edits | Queue avg / p95 / max | Upload avg | Planner avg | Regular delta avg | Transition metadata avg | Transition desired avg |" );
		builder.AppendLine( "|---|---:|---:|---:|---:|---:|---:|---:|" );
		foreach ( var result in _results.Where( result => result.GpuTerrain?.EditQueue.Total.Count > 0 ) )
		{
			var timings = result.GpuTerrain.Value.EditQueue;
			builder.AppendLine( $"| {result.Name} | {timings.Total.Count:N0} | {timings.Total.AverageMilliseconds:F3} / {timings.Total.P95Milliseconds:F3} / {timings.Total.MaximumMilliseconds:F3} ms | {timings.Upload.AverageMilliseconds:F3} ms | {timings.Planner.AverageMilliseconds:F3} ms | {timings.RegularDelta.AverageMilliseconds:F3} ms | {timings.TransitionMetadata.AverageMilliseconds:F3} ms | {timings.TransitionDesired.AverageMilliseconds:F3} ms |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Structured GPU clipbox diagnostics" );
		builder.AppendLine();
		builder.AppendLine( "Each report is the authoritative backend snapshot for the scenario’s latest planned or committed revision. It includes per-LOD residency, regular/transition renderer separation, transition dependencies, pool state, and the bounded hitch trace. Null fields are capabilities that the engine does not expose as a measured value." );
		foreach ( var result in _results.Where( result => result.GpuTerrain.HasValue && !string.IsNullOrWhiteSpace( result.GpuTerrain.Value.StructuredDebugReportJson ) && result.GpuTerrain.Value.StructuredDebugReportJson != "{}" ) )
		{
			builder.AppendLine();
			builder.AppendLine( $"### `{result.Name}`" );
			builder.AppendLine();
			builder.AppendLine( "```json" );
			builder.AppendLine( result.GpuTerrain.Value.StructuredDebugReportJson );
			builder.AppendLine( "```" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Phase 4 indirect renderer capacity" );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Result | Counts tested | Maximum commands | Group size capability | 49-command active lists / visible | Maximum active lists | Detail |" );
		builder.AppendLine( "|---|---|---:|---:|---:|---:|---:|---|" );
		foreach ( var result in _results.Where( result => result.IndirectRenderProof.HasValue ) )
		{
			var proof = result.IndirectRenderProof.Value;
			builder.AppendLine( $"| {result.Name} | {(proof.Passed ? "PASS" : "FAIL")} | {proof.TestedCommandCounts:N0} | {proof.MaximumCommandCount:N0} | {proof.CommandGroupSize:N0} | {proof.BoundaryActiveCommandLists:N0} / {proof.BoundaryVisibleCommands:N0} | {proof.MaximumActiveCommandLists:N0} | {proof.Failure.Replace( "|", "\\|" )} |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## GPU Transvoxel proof" );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Result | Batch / surface | Vertices / indices | Dispatches | GPU publication | GPU / CPU publication | GPU buffers | Submit / complete | Diagnostic readback | Detail |" );
		builder.AppendLine( "|---|---|---:|---:|---:|---|---:|---:|---:|---:|---|" );
		foreach ( var result in _results.Where( result => result.GpuTransvoxelProof.HasValue ) )
		{
			var proof = result.GpuTransvoxelProof.Value;
			builder.AppendLine( $"| {result.Name} | {(proof.Passed ? "PASS" : "FAIL")} | {proof.BatchSize:N0} / {proof.SurfaceBlockCount:N0} | {proof.VertexCount:N0} / {proof.IndexCount:N0} | {proof.DispatchCount:N0} | {(proof.GpuCountPublicationPassed ? "PASS" : "FAIL")} | {proof.GpuCountPublicationMilliseconds:F3} / {proof.CpuCountPublicationMilliseconds:F3} ms | {FormatBytes( proof.GpuBufferBytes )} | {proof.SubmissionMilliseconds:F3} / {proof.CompletionMilliseconds:F3} ms | {proof.GeometryReadbackMilliseconds:F3} ms | {proof.Failure.Replace( "|", "\\|" )} |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Runtime, memory, cache, and networking" );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Update avg / max | Render avg / max | Physics avg / max | Network avg / max | Network out / in | Ping | Managed allocations | Peak process memory | SDF storage | Texture pool peak |" );
		builder.AppendLine( "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|" );
		foreach ( var result in _results )
		{
			builder.AppendLine( $"| {result.Name} | {result.UpdateAverageMilliseconds:F2} / {result.UpdateMaximumMilliseconds:F2} ms | {result.RenderAverageMilliseconds:F2} / {result.RenderMaximumMilliseconds:F2} ms | {result.PhysicsAverageMilliseconds:F2} / {result.PhysicsMaximumMilliseconds:F2} ms | {result.NetworkAverageMilliseconds:F3} / {result.NetworkMaximumMilliseconds:F3} ms | {FormatBytes( (long)result.NetworkOutBytesPerSecondAverage )}/s / {FormatBytes( (long)result.NetworkInBytesPerSecondAverage )}/s | {result.NetworkPingMillisecondsAverage:F2} ms | {FormatBytes( result.AllocatedBytes )} | {FormatBytes( (long)result.PeakMemoryBytes )} | {FormatBytes( result.Diagnostics.AuthoritativeSdfStorageBytes )} | {FormatBytes( (long)result.PeakTexturePoolUsedBytes )} |" );
		}
		builder.AppendLine();
		builder.AppendLine( "## Coverage" );
		builder.AppendLine();
		builder.AppendLine( "| Aspect | Status |" );
		builder.AppendLine( "|---|---|" );
		builder.AppendLine( "| Terrain generation, visuals, collision, editing, frame pacing, latency, memory, render cache | Measured |" );
		builder.AppendLine( "| Engine networking CPU, traffic, ping, messages, connections | Measured when a network session is active |" );
		builder.AppendLine( "| Terrain streaming/player traversal | Measured with an outward radius crossing and backtrack |" );
		builder.AppendLine( "| Terrain persistence and disk/cache I/O | Not implemented; no synthetic substitute reported |" );
		builder.AppendLine( "| Terrain replication bandwidth and convergence | Not implemented; requires the multiplayer terrain slice |" );
		builder.AppendLine();
		builder.AppendLine( "## History and visualization" );
		builder.AppendLine();
		builder.AppendLine( $"Open `{FileSystem.Data.GetFullPath( DashboardPath )}` for interactive historical charts. Raw history is retained in `history.csv` and `history.jsonl`; every row includes its revision and run ID." );
		builder.AppendLine();
		builder.AppendLine( "Terrain persistence/cache I/O and terrain replication remain unavailable until their authoritative systems exist. Streaming traversal is measured without changing older JSONL rows." );
		return builder.ToString();
	}

	private string BuildDashboardHtml()
	{
		var history = FileSystem.Data.FileExists( HistoryJsonLinesPath ) ? FileSystem.Data.ReadAllText( HistoryJsonLinesPath ) : string.Empty;
		return VoxelBenchmarkDashboard.Build( history );
	}

	private string SerializeScenarioJson( ScenarioResult result )
	{
		var d = result.Diagnostics;
		var comparison = GetComparison( result );
		return "{" +
			$"\"run_id\":\"{Json( _runId )}\",\"suite_version\":{SuiteVersion},\"suite_complete\":{IsSuiteComplete.ToString().ToLowerInvariant()},\"timestamp_utc\":\"{System.DateTime.UtcNow:O}\",\"revision\":\"{Json( Revision )}\",\"working_tree_dirty\":{WorkingTreeDirty.ToString().ToLowerInvariant()},\"engine_version\":\"{Json( Application.Version )}\"," +
			$"\"cpu\":\"{Json( Sandbox.Engine.SystemInfo.ProcessorName )}\",\"gpu\":\"{Json( Sandbox.Engine.SystemInfo.Gpu )}\",\"scenario\":\"{Json( result.Name )}\",\"description\":\"{Json( result.Description )}\",\"passed\":{result.Passed.ToString().ToLowerInvariant()}," +
			$"\"edits\":{result.EditCount},\"changed_chunk_events\":{result.ChangedChunkEvents},\"visual_coherence_violation_frames\":{result.VisualCoherenceViolationFrames},\"elapsed_ms\":{Number( result.ElapsedMilliseconds )},\"frames\":{result.FrameCount},\"avg_fps\":{Number( result.AverageFramesPerSecond )},\"one_percent_low_fps\":{Number( result.OnePercentLowFramesPerSecond )},\"point_one_percent_low_fps\":{Number( result.PointOnePercentLowFramesPerSecond )},\"min_fps\":{Number( result.MinimumFramesPerSecond )},\"frame_avg_ms\":{Number( result.FrameAverageMilliseconds )},\"frame_stddev_ms\":{Number( result.FrameStandardDeviationMilliseconds )},\"frame_p50_ms\":{Number( result.FrameP50Milliseconds )},\"frame_p95_ms\":{Number( result.FrameP95Milliseconds )},\"frame_p99_ms\":{Number( result.FrameP99Milliseconds )},\"frame_p999_ms\":{Number( result.FrameP999Milliseconds )},\"frame_max_ms\":{Number( result.FrameMaximumMilliseconds )},\"unaccounted_frame_ms\":{Number( result.UnaccountedFrameMaximumMilliseconds )},\"frames_over_16ms\":{result.FramesOver16Milliseconds},\"frames_over_33ms\":{result.FramesOver33Milliseconds},\"frames_over_50ms\":{result.FramesOver50Milliseconds},\"frames_over_100ms\":{result.FramesOver100Milliseconds},\"stutter_events\":{result.StutterEvents},\"longest_stutter_frames\":{result.LongestStutterFrames}," +
			$"\"gpu_avg_ms\":{Number( result.GpuAverageMilliseconds )},\"gpu_p95_ms\":{Number( result.GpuP95Milliseconds )},\"gpu_max_ms\":{Number( result.GpuMaximumMilliseconds )},\"allocated_bytes\":{result.AllocatedBytes},\"gc_pause_ms\":{Number( result.GcPauseMilliseconds )},\"gen0_gc\":{result.Gen0Collections},\"gen1_gc\":{result.Gen1Collections},\"gen2_gc\":{result.Gen2Collections},\"exceptions\":{result.Exceptions},\"peak_memory_bytes\":{result.PeakMemoryBytes}," +
			$"\"edit_call_avg_ms\":{Number( result.EditCallAverageMilliseconds )},\"edit_call_p95_ms\":{Number( result.EditCallP95Milliseconds )},\"edit_call_max_ms\":{Number( result.EditCallMaximumMilliseconds )},\"post_edit_settle_ms\":{Number( result.PostEditSettleMilliseconds )},\"edit_throughput_per_second\":{Number( result.EditThroughputPerSecond )}," +
			$"\"update_avg_ms\":{Number( result.UpdateAverageMilliseconds )},\"update_max_ms\":{Number( result.UpdateMaximumMilliseconds )},\"render_avg_ms\":{Number( result.RenderAverageMilliseconds )},\"render_max_ms\":{Number( result.RenderMaximumMilliseconds )},\"physics_avg_ms\":{Number( result.PhysicsAverageMilliseconds )},\"physics_max_ms\":{Number( result.PhysicsMaximumMilliseconds )},\"network_avg_ms\":{Number( result.NetworkAverageMilliseconds )},\"network_max_ms\":{Number( result.NetworkMaximumMilliseconds )},\"network_out_bytes_per_second\":{Number( result.NetworkOutBytesPerSecondAverage )},\"network_in_bytes_per_second\":{Number( result.NetworkInBytesPerSecondAverage )},\"network_ping_ms\":{Number( result.NetworkPingMillisecondsAverage )},\"maximum_connections\":{result.MaximumConnections},\"messages_sent\":{result.MessagesSent},\"messages_received\":{result.MessagesReceived}," +
			$"\"texture_pool_peak_bytes\":{result.PeakTexturePoolUsedBytes},\"texture_pool_non_evictable_peak_bytes\":{result.PeakTexturePoolNonEvictableBytes},\"pending_streaming_requests_max\":{result.MaximumPendingStreamingRequests},\"draw_calls_avg\":{Number( result.DrawCallsAverage )},\"triangles_rendered_avg\":{Number( result.TrianglesRenderedAverage )},\"objects_rendered_avg\":{Number( result.ObjectsRenderedAverage )},\"material_changes_avg\":{Number( result.MaterialChangesAverage )}," +
			$"\"loaded_chunks\":{d.LoadedChunks},\"authoritative_sdf_bytes\":{d.AuthoritativeSdfStorageBytes},\"uniform_sdf_chunks\":{d.UniformSdfChunks},\"visual_chunks\":{d.ActiveVisualChunks},\"failed_visual_chunks\":{d.FailedVisualChunks},\"visual_batch_built_chunks\":{d.VisualBatchBuiltChunks},\"visual_vertices\":{d.VisualVertices},\"visual_triangles\":{d.VisualTriangles},\"colliders\":{d.ActiveColliders},\"collision_triangles\":{d.CollisionTriangles},\"player_safety_active\":{d.PlayerSafetyActive.ToString().ToLowerInvariant()}," +
			$"\"generation_ms\":{Number( d.GenerationElapsedMilliseconds )},\"visual_batch_ms\":{Number( d.VisualBatchElapsedMilliseconds )},\"snapshot_wait_ms\":{Number( d.SnapshotWaitMilliseconds )},\"snapshot_copy_ms\":{Number( d.SnapshotCopyMilliseconds )},\"worker_mesh_ms\":{Number( d.WorkerMeshMilliseconds )},\"upload_ms\":{Number( d.MainThreadUploadMilliseconds )}," +
			$"\"collision_snapshot_wait_ms\":{Number( d.TotalCollisionSnapshotWaitMilliseconds )},\"collision_snapshot_copy_ms\":{Number( d.TotalCollisionSnapshotCopyMilliseconds )},\"collision_worker_mesh_ms\":{Number( d.TotalCollisionWorkerMeshMilliseconds )},\"collision_model_build_ms\":{Number( d.TotalCollisionModelBuildMilliseconds )},\"collision_publication_ms\":{Number( d.TotalCollisionPublicationMilliseconds )}," +
			SerializeGpuTransvoxelProofJson( result.GpuTransvoxelProof ) + "," +
			SerializeLod5OwnershipProofJson( result.Lod5OwnershipProof ) + "," +
			SerializeGpuPhase2BJson( result.GpuTerrain, result.GpuPhase2BProof ) + "," +
			SerializeGpuPhase3AJson( result.GpuPhase3AProof ) + "," +
			SerializeGpuPhase3BJson( result.GpuPhase3BProof ) + "," +
			SerializeClipboxPlannerProofJson( result.ClipboxPlannerProof ) + "," +
			SerializeTransitionProofJson( result.TransitionProof ) + "," +
			SerializeIndirectRenderProofJson( result.IndirectRenderProof ) + "," +
			SerializeGpuQueueJson( result.GpuTerrain ) + "," +
			SerializeStreamingJson( result.Streaming ) + "," +
			$"\"configuration_id\":\"{Json( ConfigurationId )}\",\"comparison_baseline_run_id\":{(comparison.HasBaseline ? "\"" + Json( comparison.BaselineRunId ) + "\"" : "null")},\"comparison_has_baseline\":{comparison.HasBaseline.ToString().ToLowerInvariant()},\"change_max_abs_pct\":{(comparison.HasBaseline ? Number( comparison.MaximumAbsolutePercent ) : "null")},\"outlier_detected\":{comparison.MajorOutlier.ToString().ToLowerInvariant()},\"outlier_metrics\":\"{Json( comparison.OutlierMetrics )}\",\"reproduction_of_run_id\":{(_reproductionOfRunId is null ? "null" : "\"" + Json( _reproductionOfRunId ) + "\"")},\"reproduction_status\":\"{Json( comparison.ReproductionStatus )}\"," +
			SerializePercentChangesJson( comparison ) + "," +
			SerializeCallCountsJson( result.CallCounts ) +
			"}";
	}

	private string ToCsvRow( ScenarioResult result )
	{
		var d = result.Diagnostics;
		var comparison = GetComparison( result );
		var baseRow = string.Join( ",",
			Csv( _runId ), SuiteVersion, IsSuiteComplete, Csv( System.DateTime.UtcNow.ToString( "O" ) ), Csv( Revision ), WorkingTreeDirty, Csv( Application.Version ), Csv( Sandbox.Engine.SystemInfo.ProcessorName ), Csv( Sandbox.Engine.SystemInfo.Gpu ), Csv( result.Name ), Csv( result.Description ), result.Passed, result.EditCount, result.ChangedChunkEvents, result.VisualCoherenceViolationFrames,
			Number( result.ElapsedMilliseconds ), result.FrameCount, Number( result.AverageFramesPerSecond ), Number( result.OnePercentLowFramesPerSecond ), Number( result.PointOnePercentLowFramesPerSecond ), Number( result.MinimumFramesPerSecond ), Number( result.FrameAverageMilliseconds ), Number( result.FrameStandardDeviationMilliseconds ), Number( result.FrameP50Milliseconds ), Number( result.FrameP95Milliseconds ), Number( result.FrameP99Milliseconds ), Number( result.FrameP999Milliseconds ), Number( result.FrameMaximumMilliseconds ), Number( result.UnaccountedFrameMaximumMilliseconds ), result.FramesOver16Milliseconds, result.FramesOver33Milliseconds, result.FramesOver50Milliseconds, result.FramesOver100Milliseconds, result.StutterEvents, result.LongestStutterFrames,
			Number( result.GpuAverageMilliseconds ), Number( result.GpuP95Milliseconds ), Number( result.GpuMaximumMilliseconds ), Number( result.EditCallAverageMilliseconds ), Number( result.EditCallP95Milliseconds ), Number( result.EditCallMaximumMilliseconds ), Number( result.PostEditSettleMilliseconds ), Number( result.EditThroughputPerSecond ), Number( result.UpdateAverageMilliseconds ), Number( result.UpdateMaximumMilliseconds ), Number( result.RenderAverageMilliseconds ), Number( result.RenderMaximumMilliseconds ), Number( result.PhysicsAverageMilliseconds ), Number( result.PhysicsMaximumMilliseconds ), Number( result.NetworkAverageMilliseconds ), Number( result.NetworkMaximumMilliseconds ), Number( result.NetworkOutBytesPerSecondAverage ), Number( result.NetworkInBytesPerSecondAverage ), Number( result.NetworkPingMillisecondsAverage ), result.MaximumConnections, result.MessagesSent, result.MessagesReceived,
			result.AllocatedBytes, Number( result.GcPauseMilliseconds ), result.Gen0Collections, result.Gen1Collections, result.Gen2Collections, result.Exceptions, result.PeakMemoryBytes, result.PeakTexturePoolUsedBytes, result.PeakTexturePoolNonEvictableBytes, result.MaximumPendingStreamingRequests, Number( result.DrawCallsAverage ), Number( result.TrianglesRenderedAverage ), Number( result.ObjectsRenderedAverage ), Number( result.MaterialChangesAverage ),
			d.LoadedChunks, d.AuthoritativeSdfStorageBytes, d.UniformSdfChunks, d.ActiveVisualChunks, d.FailedVisualChunks, d.VisualBatchBuiltChunks, d.VisualVertices, d.VisualTriangles, d.ActiveColliders, d.CollisionTriangles, d.PlayerSafetyActive, Number( d.GenerationElapsedMilliseconds ), Number( d.VisualBatchElapsedMilliseconds ), Number( d.SnapshotWaitMilliseconds ), Number( d.SnapshotCopyMilliseconds ), Number( d.WorkerMeshMilliseconds ), Number( d.MainThreadUploadMilliseconds ), Number( d.TotalCollisionSnapshotWaitMilliseconds ), Number( d.TotalCollisionSnapshotCopyMilliseconds ), Number( d.TotalCollisionWorkerMeshMilliseconds ), Number( d.TotalCollisionModelBuildMilliseconds ), Number( d.TotalCollisionPublicationMilliseconds ),
			GpuTransvoxelProofCsv( result.GpuTransvoxelProof ),
			Lod5OwnershipProofCsv( result.Lod5OwnershipProof ),
			ClipboxPlannerProofCsv( result.ClipboxPlannerProof ),
			TransitionProofCsv( result.TransitionProof ),
			IndirectRenderProofCsv( result.IndirectRenderProof ),
			GpuPhase2BCsv( result.GpuTerrain, result.GpuPhase2BProof, result.GpuPhase3AProof, result.GpuPhase3BProof ),
			GpuClipboxCsv( result.GpuTerrain ),
			StreamingCsv( result.Streaming ),
			Csv( ConfigurationId ), Csv( comparison.BaselineRunId ), comparison.HasBaseline, comparison.HasBaseline ? Number( comparison.MaximumAbsolutePercent ) : string.Empty, comparison.MajorOutlier, Csv( comparison.OutlierMetrics ), Csv( _reproductionOfRunId ), Csv( comparison.ReproductionStatus )
		);
		var changes = string.Join( ",", ComparisonMetrics.Select( metric => comparison.PercentChanges.TryGetValue( metric.Name, out var change ) ? Number( change ) : string.Empty ) );
		var calls = string.Join( ",", result.CallCounts.Enumerate().Select( entry => entry.Count.ToString( System.Globalization.CultureInfo.InvariantCulture ) ) );
		return baseRow + "," + changes + "," + calls;
	}

	private static string SerializePercentChangesJson( ScenarioComparison comparison ) =>
		string.Join( ",", ComparisonMetrics.Select( metric => $"\"change_{metric.Name}_pct\":{(comparison.PercentChanges.TryGetValue( metric.Name, out var change ) ? Number( change ) : "null")}" ) );

	private static string SerializeCallCountsJson( VoxelCallCountSnapshot counts ) =>
		string.Join( ",", counts.Enumerate().Select( entry => $"\"{CallCountKey( entry.Name )}\":{entry.Count}" ) );

	private static string SerializeGpuTransvoxelProofJson( VoxelGpuTransvoxelProofResult? proof ) => proof.HasValue
		? $"\"gpu_transvoxel_available\":true,\"gpu_transvoxel_passed\":{proof.Value.Passed.ToString().ToLowerInvariant()},\"gpu_transvoxel_failure\":\"{Json( proof.Value.Failure )}\",\"gpu_transvoxel_vertices\":{proof.Value.VertexCount},\"gpu_transvoxel_indices\":{proof.Value.IndexCount},\"gpu_transvoxel_active_cells\":{proof.Value.ActiveCells},\"gpu_transvoxel_overflow_attempts\":{proof.Value.OverflowAttempts},\"gpu_transvoxel_buffer_bytes\":{proof.Value.GpuBufferBytes},\"gpu_transvoxel_submission_ms\":{Number( proof.Value.SubmissionMilliseconds )},\"gpu_transvoxel_completion_ms\":{Number( proof.Value.CompletionMilliseconds )},\"gpu_transvoxel_readback_ms\":{Number( proof.Value.GeometryReadbackMilliseconds )},\"gpu_transvoxel_batch_size\":{proof.Value.BatchSize},\"gpu_transvoxel_surface_blocks\":{proof.Value.SurfaceBlockCount},\"gpu_transvoxel_dispatches\":{proof.Value.DispatchCount},\"gpu_transvoxel_gpu_publication_passed\":{proof.Value.GpuCountPublicationPassed.ToString().ToLowerInvariant()},\"gpu_transvoxel_gpu_publication_ms\":{Number( proof.Value.GpuCountPublicationMilliseconds )},\"gpu_transvoxel_cpu_publication_ms\":{Number( proof.Value.CpuCountPublicationMilliseconds )}"
		: "\"gpu_transvoxel_available\":false,\"gpu_transvoxel_passed\":false,\"gpu_transvoxel_failure\":\"\",\"gpu_transvoxel_vertices\":0,\"gpu_transvoxel_indices\":0,\"gpu_transvoxel_active_cells\":0,\"gpu_transvoxel_overflow_attempts\":0,\"gpu_transvoxel_buffer_bytes\":0,\"gpu_transvoxel_submission_ms\":0,\"gpu_transvoxel_completion_ms\":0,\"gpu_transvoxel_readback_ms\":0,\"gpu_transvoxel_batch_size\":0,\"gpu_transvoxel_surface_blocks\":0,\"gpu_transvoxel_dispatches\":0,\"gpu_transvoxel_gpu_publication_passed\":false,\"gpu_transvoxel_gpu_publication_ms\":0,\"gpu_transvoxel_cpu_publication_ms\":0";

	private static string GpuTransvoxelProofCsv( VoxelGpuTransvoxelProofResult? proof ) => proof.HasValue
		? string.Join( ",", true, proof.Value.Passed, Csv( proof.Value.Failure ), proof.Value.VertexCount, proof.Value.IndexCount, proof.Value.ActiveCells, proof.Value.OverflowAttempts, proof.Value.GpuBufferBytes, Number( proof.Value.SubmissionMilliseconds ), Number( proof.Value.CompletionMilliseconds ), Number( proof.Value.GeometryReadbackMilliseconds ), proof.Value.BatchSize, proof.Value.SurfaceBlockCount, proof.Value.DispatchCount, proof.Value.GpuCountPublicationPassed, Number( proof.Value.GpuCountPublicationMilliseconds ), Number( proof.Value.CpuCountPublicationMilliseconds ) )
		: "False,False,\"\",0,0,0,0,0,0,0,0,0,0,0,False,0,0";

	private static string SerializeLod5OwnershipProofJson( VoxelGpuClipboxSeamProofReport? proof )
	{
		var p = proof ?? default;
		return $"\"gpu_lod_ownership_available\":{proof.HasValue.ToString().ToLowerInvariant()},\"gpu_lod_ownership_passed\":{p.LodOwnershipPassed.ToString().ToLowerInvariant()},\"gpu_lod_ownership_failure\":\"{Json( p.LodOwnershipFailure )}\",\"gpu_lod_ownership_active_transitions\":{p.ActiveTransitions},\"gpu_lod_ownership_published_transitions\":{p.PublishedTransitions},\"gpu_lod_ownership_duplicate_regular_triangles\":{p.DuplicateRegularTriangles},\"gpu_lod_ownership_duplicate_transition_triangles\":{p.DuplicateTransitionTriangles},\"gpu_lod_ownership_cross_owner_triangles\":{p.CrossOwnerDuplicateTriangles},\"gpu_lod_ownership_lod5_duplicate_triangles\":{p.Lod5DuplicateTriangles},\"gpu_lod_ownership_collapsed_transitions\":{p.CollapsedTransitionMeshes},\"gpu_lod_ownership_undeformed_coarse_vertices\":{p.UndeformedCoarseBoundaryVertices},\"gpu_lod_ownership_boundary_vertices\":{p.BoundaryVertices},\"gpu_lod_ownership_unmatched_boundary_vertices\":{p.UnmatchedBoundaryVertices},\"gpu_lod_ownership_max_seam_error\":{Number( p.MaximumSeamPositionError )},\"gpu_lod_ownership_readback_bytes\":{p.GeometryReadbackBytes},\"gpu_lod_ownership_readback_ms\":{Number( p.GeometryReadbackMilliseconds )}";
	}

	private static string Lod5OwnershipProofCsv( VoxelGpuClipboxSeamProofReport? proof )
	{
		var p = proof ?? default;
		return string.Join( ",", proof.HasValue, p.LodOwnershipPassed, Csv( p.LodOwnershipFailure ), p.ActiveTransitions, p.PublishedTransitions,
			p.DuplicateRegularTriangles, p.DuplicateTransitionTriangles, p.CrossOwnerDuplicateTriangles, p.Lod5DuplicateTriangles,
			p.CollapsedTransitionMeshes, p.UndeformedCoarseBoundaryVertices, p.BoundaryVertices, p.UnmatchedBoundaryVertices,
			Number( p.MaximumSeamPositionError ), p.GeometryReadbackBytes, Number( p.GeometryReadbackMilliseconds ) );
	}

	private const string Lod5OwnershipCsvHeader = "gpu_lod_ownership_available,gpu_lod_ownership_passed,gpu_lod_ownership_failure,gpu_lod_ownership_active_transitions,gpu_lod_ownership_published_transitions,gpu_lod_ownership_duplicate_regular_triangles,gpu_lod_ownership_duplicate_transition_triangles,gpu_lod_ownership_cross_owner_triangles,gpu_lod_ownership_lod5_duplicate_triangles,gpu_lod_ownership_collapsed_transitions,gpu_lod_ownership_undeformed_coarse_vertices,gpu_lod_ownership_boundary_vertices,gpu_lod_ownership_unmatched_boundary_vertices,gpu_lod_ownership_max_seam_error,gpu_lod_ownership_readback_bytes,gpu_lod_ownership_readback_ms";

	private const string GpuPhase2BCsvHeader = "gpu_terrain_available,gpu_terrain_backend,gpu_terrain_lod_policy,gpu_terrain_indirect_command_group_size,gpu_terrain_requested_blocks,gpu_terrain_resident_blocks,gpu_terrain_pending_count_batches,gpu_terrain_pending_emit_batches,gpu_terrain_backpressure_events,gpu_terrain_allocation_failures,gpu_terrain_stale_publications_rejected,gpu_terrain_visible_draw_commands,gpu_terrain_scratch_bytes,gpu_terrain_pool_capacity_bytes,gpu_terrain_pool_used_bytes,gpu_terrain_pool_peak_bytes,gpu_terrain_geometry_readback_bytes,gpu_terrain_count_submission_ms,gpu_terrain_count_readback_avg_ms,gpu_terrain_count_readback_count,gpu_terrain_emit_submission_ms,gpu_terrain_count_submission_per_block_ms,gpu_terrain_emit_submission_per_block_ms,gpu_terrain_request_to_visible_avg_ms,gpu_terrain_request_to_visible_p95_ms,gpu_terrain_request_to_visible_max_ms,gpu_terrain_batch_completion_avg_ms,gpu_terrain_batch_completion_p95_ms,gpu_terrain_batch_completion_max_ms,gpu_terrain_failure,gpu_terrain_capacity_limited,gpu_terrain_blocked_requests,gpu_terrain_capacity_evictions,gpu_terrain_capacity_deferrals,gpu_terrain_vertex_free,gpu_terrain_index_free,gpu_terrain_vertex_largest_free,gpu_terrain_index_largest_free,gpu_terrain_vertex_free_ranges,gpu_terrain_index_free_ranges,gpu_phase2b_available,gpu_phase2b_passed,gpu_phase2b_test,gpu_phase2b_failure,gpu_lifecycle_budget_bytes,gpu_lifecycle_peak_used_bytes,gpu_lifecycle_churn_operations,gpu_lifecycle_allocation_failures,gpu_lifecycle_backpressure_events,gpu_lifecycle_stale_publications_rejected,gpu_lifecycle_retained_delta_percent,gpu_terrain_render_shader,gpu_terrain_production_lighting,gpu_terrain_depth_prepass,gpu_terrain_depth_command_lists,gpu_terrain_opaque_command_lists,gpu_phase3a_available,gpu_phase3a_passed,gpu_phase3a_test,gpu_phase3a_failure,gpu_terrain_desired_blocks,gpu_terrain_resident_capacity,gpu_terrain_pending_request_capacity,gpu_terrain_pending_request_count,gpu_terrain_pending_publication_count,gpu_terrain_queues_bounded,gpu_phase3b_available,gpu_phase3b_passed,gpu_phase3b_test,gpu_phase3b_failure";

	private const string GpuClipboxCsvHeader = "gpu_terrain_clipbox_revision,gpu_terrain_clipbox_changed_slots,gpu_terrain_clipbox_pending_revision_count,gpu_terrain_clipbox_max_pending_revision_count,gpu_terrain_clipbox_stable_slots,gpu_terrain_clipbox_active_slots,gpu_terrain_clipbox_dropped_work,gpu_terrain_clipbox_stationary_updates,gpu_terrain_clipbox_transition_capacity,gpu_terrain_clipbox_transition_active_slots,gpu_terrain_clipbox_transition_changed_slots,gpu_terrain_clipbox_transition_pending_slots,gpu_terrain_clipbox_transition_dependency_mismatches,gpu_terrain_clipbox_transition_stationary_updates,gpu_terrain_transition_resident_blocks,gpu_terrain_transition_renderable_residents,gpu_terrain_transition_visible_draw_commands,gpu_terrain_transition_pending_requests,gpu_terrain_transition_blocked_requests,gpu_terrain_transition_allocated_vertex_count,gpu_terrain_transition_allocated_index_count,gpu_terrain_transition_allocated_bytes,gpu_terrain_transition_geometry_readback_bytes,gpu_terrain_transition_cpu_sdf_evaluations,gpu_terrain_transition_stale_scheduler_rejections,gpu_terrain_transition_stale_dependency_rejections,gpu_edit_queue_count,gpu_edit_queue_avg_ms,gpu_edit_queue_p95_ms,gpu_edit_queue_max_ms,gpu_edit_upload_avg_ms,gpu_edit_upload_p95_ms,gpu_edit_upload_max_ms,gpu_edit_planner_avg_ms,gpu_edit_planner_p95_ms,gpu_edit_planner_max_ms,gpu_edit_regular_delta_avg_ms,gpu_edit_regular_delta_p95_ms,gpu_edit_regular_delta_max_ms,gpu_edit_transition_metadata_avg_ms,gpu_edit_transition_metadata_p95_ms,gpu_edit_transition_metadata_max_ms,gpu_edit_transition_desired_avg_ms,gpu_edit_transition_desired_p95_ms,gpu_edit_transition_desired_max_ms,gpu_terrain_structured_debug_report";

	private static string SerializeGpuPhase2BJson( VoxelGpuTerrainDiagnostics? terrain, VoxelGpuPhase2BProofResult? proof )
	{
		var t = terrain ?? default;
		var p = proof ?? default;
		var lifecycle = p.Lifecycle ?? default;
		return $"\"gpu_terrain_available\":{terrain.HasValue.ToString().ToLowerInvariant()},\"gpu_terrain_backend\":\"{Json( t.Backend )}\",\"gpu_terrain_render_shader\":\"{Json( t.RenderShader )}\",\"gpu_terrain_production_lighting\":{t.ProductionLighting.ToString().ToLowerInvariant()},\"gpu_terrain_depth_prepass\":{t.DepthPrepass.ToString().ToLowerInvariant()},\"gpu_terrain_depth_command_lists\":{t.DepthPrepassCommandLists},\"gpu_terrain_opaque_command_lists\":{t.OpaqueCommandLists},\"gpu_terrain_requested_blocks\":{t.RequestedBlocks},\"gpu_terrain_resident_blocks\":{t.ResidentBlocks},\"gpu_terrain_pending_count_batches\":{t.PendingCountBatches},\"gpu_terrain_pending_emit_batches\":{t.PendingEmitBatches},\"gpu_terrain_backpressure_events\":{t.BackpressureEvents},\"gpu_terrain_allocation_failures\":{t.AllocationFailures},\"gpu_terrain_stale_publications_rejected\":{t.StalePublicationsRejected},\"gpu_terrain_visible_draw_commands\":{t.VisibleDrawCommands},\"gpu_terrain_scratch_bytes\":{t.ScratchBytes},\"gpu_terrain_pool_capacity_bytes\":{t.PoolCapacityBytes},\"gpu_terrain_pool_used_bytes\":{t.PoolUsedBytes},\"gpu_terrain_pool_peak_bytes\":{t.PeakPoolUsedBytes},\"gpu_terrain_geometry_readback_bytes\":{t.GeometryReadbackBytes},\"gpu_terrain_count_submission_ms\":{Number( t.CountSubmissionMilliseconds )},\"gpu_terrain_count_readback_avg_ms\":{Number( t.CountReadbackAverageMilliseconds )},\"gpu_terrain_count_readback_count\":{t.CountReadbackCount},\"gpu_terrain_emit_submission_ms\":{Number( t.EmitSubmissionMilliseconds )},\"gpu_terrain_count_submission_per_block_ms\":{Number( t.CountSubmissionPerBlockMilliseconds )},\"gpu_terrain_emit_submission_per_block_ms\":{Number( t.EmitSubmissionPerBlockMilliseconds )},\"gpu_terrain_request_to_visible_avg_ms\":{Number( t.RequestToVisible.AverageMilliseconds )},\"gpu_terrain_request_to_visible_p95_ms\":{Number( t.RequestToVisible.P95Milliseconds )},\"gpu_terrain_request_to_visible_max_ms\":{Number( t.RequestToVisible.MaximumMilliseconds )},\"gpu_terrain_batch_completion_avg_ms\":{Number( t.BatchCompletion.AverageMilliseconds )},\"gpu_terrain_batch_completion_p95_ms\":{Number( t.BatchCompletion.P95Milliseconds )},\"gpu_terrain_batch_completion_max_ms\":{Number( t.BatchCompletion.MaximumMilliseconds )},\"gpu_terrain_failure\":\"{Json( t.Failure )}\",\"gpu_terrain_capacity_limited\":{t.CapacityLimited.ToString().ToLowerInvariant()},\"gpu_terrain_blocked_requests\":{t.BlockedRequests},\"gpu_terrain_capacity_evictions\":{t.CapacityEvictions},\"gpu_terrain_capacity_deferrals\":{t.CapacityDeferrals},\"gpu_terrain_vertex_free\":{t.VertexFree},\"gpu_terrain_index_free\":{t.IndexFree},\"gpu_terrain_vertex_largest_free\":{t.VertexLargestFree},\"gpu_terrain_index_largest_free\":{t.IndexLargestFree},\"gpu_terrain_vertex_free_ranges\":{t.VertexFreeRangeCount},\"gpu_terrain_index_free_ranges\":{t.IndexFreeRangeCount},\"gpu_phase2b_available\":{proof.HasValue.ToString().ToLowerInvariant()},\"gpu_phase2b_passed\":{p.Passed.ToString().ToLowerInvariant()},\"gpu_phase2b_test\":\"{Json( p.Test )}\",\"gpu_phase2b_failure\":\"{Json( p.Failure )}\",\"gpu_lifecycle_budget_bytes\":{lifecycle.BudgetBytes},\"gpu_lifecycle_peak_used_bytes\":{lifecycle.PeakUsedBytes},\"gpu_lifecycle_churn_operations\":{lifecycle.ChurnOperations},\"gpu_lifecycle_allocation_failures\":{lifecycle.AllocationFailures},\"gpu_lifecycle_backpressure_events\":{lifecycle.BackpressureEvents},\"gpu_lifecycle_stale_publications_rejected\":{lifecycle.StalePublicationsRejected},\"gpu_lifecycle_retained_delta_percent\":{Number( lifecycle.RetainedMemoryDeltaPercent )}";
	}

	private static string SerializeGpuPhase3AJson( VoxelGpuPhase3AProofResult? proof )
	{
		var p = proof ?? default;
		return $"\"gpu_phase3a_available\":{proof.HasValue.ToString().ToLowerInvariant()},\"gpu_phase3a_passed\":{p.Passed.ToString().ToLowerInvariant()},\"gpu_phase3a_test\":\"{Json( p.Test )}\",\"gpu_phase3a_failure\":\"{Json( p.Failure )}\"";
	}

	private static string SerializeGpuPhase3BJson( VoxelGpuPhase3BProofResult? proof )
	{
		var p = proof ?? default;
		return $"\"gpu_phase3b_available\":{proof.HasValue.ToString().ToLowerInvariant()},\"gpu_phase3b_passed\":{p.Passed.ToString().ToLowerInvariant()},\"gpu_phase3b_test\":\"{Json( p.Test )}\",\"gpu_phase3b_failure\":\"{Json( p.Failure )}\"";
	}

	private bool TryInitialize()
	{
		_manager = GameObject is null ? null : GameObject.Components.Get<VoxelManager>();
		_manager ??= Scene.GetAllComponents<VoxelManager>().FirstOrDefault();
		if ( _manager is null ) return false;
		_initializationComplete = true;
		_initializationAttempts = 0;
		EnsurePlayerSafetyFixture();
		_originalCaptureCallCounts = _manager.CaptureCallCounts;
		_callCountSettingCaptured = true;
		BuildVariedEditFixture();
		if ( RunOnStart && _phase == BenchmarkPhase.Idle ) BeginRun( false );
		return true;
	}

	private static string SerializeClipboxPlannerProofJson( VoxelClipboxPlannerProofReport? proof )
	{
		var p = proof ?? default;
		return $"\"clipbox_planner_available\":{proof.HasValue.ToString().ToLowerInvariant()},\"clipbox_planner_passed\":{p.Passed.ToString().ToLowerInvariant()},\"clipbox_planner_failure\":\"{Json( p.Failure )}\",\"clipbox_planner_cases\":{p.Cases},\"clipbox_planner_configurations\":{p.Configurations},\"clipbox_planner_active_regular_count\":{p.ActiveRegularCount},\"clipbox_planner_stable_regular_slots\":{p.StableRegularSlots},\"clipbox_planner_allocated_after_warmup\":{p.AllocatedBytesAfterWarmup},\"clipbox_planner_transition_capacity\":{p.TransitionCapacity},\"clipbox_planner_active_transition_count\":{p.ActiveTransitionCount},\"clipbox_planner_changed_transition_slots\":{p.ChangedTransitionSlots},\"clipbox_planner_transition_ownership_validated\":{p.TransitionOwnershipValidated.ToString().ToLowerInvariant()}";
	}

	private static string ClipboxPlannerProofCsv( VoxelClipboxPlannerProofReport? proof )
	{
		var p = proof ?? default;
		return string.Join( ",", proof.HasValue, p.Passed, Csv( p.Failure ), p.Cases, p.Configurations, p.ActiveRegularCount, p.StableRegularSlots, p.AllocatedBytesAfterWarmup, p.TransitionCapacity, p.ActiveTransitionCount, p.ChangedTransitionSlots, p.TransitionOwnershipValidated );
	}

	private static string SerializeTransitionProofJson( VoxelTransvoxelTransitionProofResult? proof )
	{
		var p = proof ?? default;
		return $"\"transition_reference_available\":{proof.HasValue.ToString().ToLowerInvariant()},\"transition_reference_passed\":{p.Passed.ToString().ToLowerInvariant()},\"transition_reference_failure\":\"{Json( p.Failure )}\",\"transition_reference_cases\":{p.Cases},\"transition_reference_orientations\":{p.Orientations},\"transition_reference_fixture_cases\":{p.FixtureCases},\"transition_reference_triangles\":{p.ValidatedTriangles},\"transition_reference_boundary_edges\":{p.BoundaryEdges},\"transition_reference_gradient_normals\":{p.GradientNormals},\"transition_reference_position_tolerance\":{Number( p.PositionTolerance )},\"transition_reference_tables_validated\":{p.TablesValidated.ToString().ToLowerInvariant()},\"transition_gpu_case_available\":{p.GpuCaseProofAvailable.ToString().ToLowerInvariant()},\"transition_gpu_case_passed\":{p.GpuCaseProofPassed.ToString().ToLowerInvariant()},\"transition_gpu_case_failure\":\"{Json( p.GpuCaseProofFailure )}\",\"transition_gpu_case_variants\":{p.GpuCaseVariants},\"transition_gpu_case_buffer_bytes\":{p.GpuCaseBufferBytes},\"transition_gpu_case_submission_ms\":{Number( p.GpuCaseSubmissionMilliseconds )},\"transition_gpu_case_completion_ms\":{Number( p.GpuCaseCompletionMilliseconds )},\"transition_gpu_case_readback_ms\":{Number( p.GpuCaseReadbackMilliseconds )}";
	}

	private static string TransitionProofCsv( VoxelTransvoxelTransitionProofResult? proof )
	{
		var p = proof ?? default;
		return string.Join( ",", proof.HasValue, p.Passed, Csv( p.Failure ), p.Cases, p.Orientations, p.FixtureCases, p.ValidatedTriangles, p.BoundaryEdges, p.GradientNormals, Number( p.PositionTolerance ), p.TablesValidated, p.GpuCaseProofAvailable, p.GpuCaseProofPassed, Csv( p.GpuCaseProofFailure ), p.GpuCaseVariants, p.GpuCaseBufferBytes, Number( p.GpuCaseSubmissionMilliseconds ), Number( p.GpuCaseCompletionMilliseconds ), Number( p.GpuCaseReadbackMilliseconds ) );
	}

	private static string SerializeIndirectRenderProofJson( VoxelGpuIndirectRenderProofReport? proof )
	{
		var p = proof ?? default;
		return $"\"indirect_render_available\":{proof.HasValue.ToString().ToLowerInvariant()},\"indirect_render_passed\":{p.Passed.ToString().ToLowerInvariant()},\"indirect_render_failure\":\"{Json( p.Failure )}\",\"indirect_render_tested_command_counts\":{p.TestedCommandCounts},\"indirect_render_maximum_command_count\":{p.MaximumCommandCount},\"indirect_render_group_size\":{p.CommandGroupSize},\"indirect_render_boundary_command_count\":{p.BoundaryCommandCount},\"indirect_render_boundary_active_lists\":{p.BoundaryActiveCommandLists},\"indirect_render_boundary_visible_commands\":{p.BoundaryVisibleCommands},\"indirect_render_maximum_active_lists\":{p.MaximumActiveCommandLists}";
	}

	private static string IndirectRenderProofCsv( VoxelGpuIndirectRenderProofReport? proof )
	{
		var p = proof ?? default;
		return string.Join( ",", proof.HasValue, p.Passed, Csv( p.Failure ), p.TestedCommandCounts, p.MaximumCommandCount, p.CommandGroupSize, p.BoundaryCommandCount, p.BoundaryActiveCommandLists, p.BoundaryVisibleCommands, p.MaximumActiveCommandLists );
	}

	private static string SerializeGpuQueueJson( VoxelGpuTerrainDiagnostics? terrain )
	{
		var t = terrain ?? default;
		var structuredDebugReportJson = string.IsNullOrWhiteSpace( t.StructuredDebugReportJson ) ? "{}" : t.StructuredDebugReportJson;
		return $"\"gpu_terrain_desired_blocks\":{t.DesiredBlocks},\"gpu_terrain_resident_capacity\":{t.ResidentCapacity},\"gpu_terrain_pending_request_capacity\":{t.PendingRequestCapacity},\"gpu_terrain_pending_request_count\":{t.PendingRequestCount},\"gpu_terrain_pending_publication_count\":{t.PendingPublicationCount},\"gpu_terrain_blocked_requests\":{t.BlockedRequests},\"gpu_terrain_capacity_limited\":{t.CapacityLimited.ToString().ToLowerInvariant()},\"gpu_terrain_queues_bounded\":{t.QueuesBounded.ToString().ToLowerInvariant()},\"gpu_terrain_indirect_command_group_size\":{t.IndirectCommandGroupSize},\"gpu_terrain_lod_policy\":\"{Json( t.LodPolicy )}\",\"gpu_terrain_clipbox_revision\":{t.ClipboxRevision},\"gpu_terrain_clipbox_changed_slots\":{t.ClipboxChangedSlots},\"gpu_terrain_clipbox_pending_revision_count\":{t.ClipboxPendingRevisionCount},\"gpu_terrain_clipbox_max_pending_revision_count\":{t.ClipboxMaximumPendingRevisionCount},\"gpu_terrain_clipbox_stable_slots\":{t.ClipboxStableSlotCount},\"gpu_terrain_clipbox_active_slots\":{t.ClipboxActiveSlotCount},\"gpu_terrain_clipbox_dropped_work\":{t.ClipboxDroppedWork},\"gpu_terrain_clipbox_stationary_updates\":{t.ClipboxStationaryUpdates},\"gpu_terrain_clipbox_transition_capacity\":{t.ClipboxTransitionCapacity},\"gpu_terrain_clipbox_transition_active_slots\":{t.ClipboxTransitionActiveSlotCount},\"gpu_terrain_clipbox_transition_changed_slots\":{t.ClipboxTransitionChangedSlots},\"gpu_terrain_clipbox_transition_pending_slots\":{t.ClipboxTransitionPendingSlots},\"gpu_terrain_clipbox_transition_dependency_mismatches\":{t.ClipboxTransitionDependencyMismatches},\"gpu_terrain_clipbox_transition_stationary_updates\":{t.ClipboxTransitionStationaryUpdates},\"gpu_terrain_transition_resident_blocks\":{t.TransitionResidentBlocks},\"gpu_terrain_transition_renderable_residents\":{t.TransitionRenderableResidents},\"gpu_terrain_transition_visible_draw_commands\":{t.TransitionVisibleDrawCommands},\"gpu_terrain_transition_pending_requests\":{t.TransitionPendingRequests},\"gpu_terrain_transition_blocked_requests\":{t.TransitionBlockedRequests},\"gpu_terrain_transition_allocated_vertex_count\":{t.TransitionAllocatedVertexCount},\"gpu_terrain_transition_allocated_index_count\":{t.TransitionAllocatedIndexCount},\"gpu_terrain_transition_allocated_bytes\":{t.TransitionAllocatedBytes},\"gpu_terrain_transition_geometry_readback_bytes\":{t.TransitionGeometryReadbackBytes},\"gpu_terrain_transition_cpu_sdf_evaluations\":{t.TransitionCpuSdfEvaluations},\"gpu_terrain_transition_stale_scheduler_rejections\":{t.TransitionStaleSchedulerRejections},\"gpu_terrain_transition_stale_dependency_rejections\":{t.TransitionStaleDependencyRejections},\"gpu_edit_queue_count\":{t.EditQueue.Total.Count}," + SerializeTimingJson( "gpu_edit_queue", t.EditQueue.Total ) + "," + SerializeTimingJson( "gpu_edit_upload", t.EditQueue.Upload ) + "," + SerializeTimingJson( "gpu_edit_planner", t.EditQueue.Planner ) + "," + SerializeTimingJson( "gpu_edit_regular_delta", t.EditQueue.RegularDelta ) + "," + SerializeTimingJson( "gpu_edit_transition_metadata", t.EditQueue.TransitionMetadata ) + "," + SerializeTimingJson( "gpu_edit_transition_desired", t.EditQueue.TransitionDesired ) + $",\"gpu_terrain_structured_debug_report\":{structuredDebugReportJson}";
	}

	private static string GpuPhase2BCsv( VoxelGpuTerrainDiagnostics? terrain, VoxelGpuPhase2BProofResult? proof, VoxelGpuPhase3AProofResult? phase3Proof, VoxelGpuPhase3BProofResult? phase3BProof )
	{
		var t = terrain ?? default;
		var p = proof ?? default;
		var p3 = phase3Proof ?? default;
		var p3b = phase3BProof ?? default;
		var lifecycle = p.Lifecycle ?? default;
		return string.Join( ",", terrain.HasValue, Csv( t.Backend ), Csv( t.LodPolicy ), t.IndirectCommandGroupSize, t.RequestedBlocks, t.ResidentBlocks, t.PendingCountBatches, t.PendingEmitBatches, t.BackpressureEvents, t.AllocationFailures, t.StalePublicationsRejected, t.VisibleDrawCommands, t.ScratchBytes, t.PoolCapacityBytes, t.PoolUsedBytes, t.PeakPoolUsedBytes, t.GeometryReadbackBytes, Number( t.CountSubmissionMilliseconds ), Number( t.CountReadbackAverageMilliseconds ), t.CountReadbackCount, Number( t.EmitSubmissionMilliseconds ), Number( t.CountSubmissionPerBlockMilliseconds ), Number( t.EmitSubmissionPerBlockMilliseconds ), Number( t.RequestToVisible.AverageMilliseconds ), Number( t.RequestToVisible.P95Milliseconds ), Number( t.RequestToVisible.MaximumMilliseconds ), Number( t.BatchCompletion.AverageMilliseconds ), Number( t.BatchCompletion.P95Milliseconds ), Number( t.BatchCompletion.MaximumMilliseconds ), Csv( t.Failure ), t.CapacityLimited, t.BlockedRequests, t.CapacityEvictions, t.CapacityDeferrals, t.VertexFree, t.IndexFree, t.VertexLargestFree, t.IndexLargestFree, t.VertexFreeRangeCount, t.IndexFreeRangeCount, proof.HasValue, p.Passed, Csv( p.Test ), Csv( p.Failure ), lifecycle.BudgetBytes, lifecycle.PeakUsedBytes, lifecycle.ChurnOperations, lifecycle.AllocationFailures, lifecycle.BackpressureEvents, lifecycle.StalePublicationsRejected, Number( lifecycle.RetainedMemoryDeltaPercent ), Csv( t.RenderShader ), t.ProductionLighting, t.DepthPrepass, t.DepthPrepassCommandLists, t.OpaqueCommandLists, phase3Proof.HasValue, p3.Passed, Csv( p3.Test ), Csv( p3.Failure ), t.DesiredBlocks, t.ResidentCapacity, t.PendingRequestCapacity, t.PendingRequestCount, t.PendingPublicationCount, t.QueuesBounded, phase3BProof.HasValue, p3b.Passed, Csv( p3b.Test ), Csv( p3b.Failure ) );
	}

	private static string GpuClipboxCsv( VoxelGpuTerrainDiagnostics? terrain )
	{
		var t = terrain ?? default;
		return string.Join( ",", t.ClipboxRevision, t.ClipboxChangedSlots, t.ClipboxPendingRevisionCount, t.ClipboxMaximumPendingRevisionCount, t.ClipboxStableSlotCount, t.ClipboxActiveSlotCount, t.ClipboxDroppedWork, t.ClipboxStationaryUpdates, t.ClipboxTransitionCapacity, t.ClipboxTransitionActiveSlotCount, t.ClipboxTransitionChangedSlots, t.ClipboxTransitionPendingSlots, t.ClipboxTransitionDependencyMismatches, t.ClipboxTransitionStationaryUpdates, t.TransitionResidentBlocks, t.TransitionRenderableResidents, t.TransitionVisibleDrawCommands, t.TransitionPendingRequests, t.TransitionBlockedRequests, t.TransitionAllocatedVertexCount, t.TransitionAllocatedIndexCount, t.TransitionAllocatedBytes, t.TransitionGeometryReadbackBytes, t.TransitionCpuSdfEvaluations, t.TransitionStaleSchedulerRejections, t.TransitionStaleDependencyRejections, t.EditQueue.Total.Count, TimingCsv( t.EditQueue.Total ), TimingCsv( t.EditQueue.Upload ), TimingCsv( t.EditQueue.Planner ), TimingCsv( t.EditQueue.RegularDelta ), TimingCsv( t.EditQueue.TransitionMetadata ), TimingCsv( t.EditQueue.TransitionDesired ), Csv( string.IsNullOrWhiteSpace( t.StructuredDebugReportJson ) ? "{}" : t.StructuredDebugReportJson ) );
	}

	private static string CallCountKey( string name ) => "calls_" + name.Replace( '.', '_' );

	private static string SerializeStreamingJson( VoxelChunkStreamingDiagnostics streaming ) =>
		$"\"stream_chunks_completed\":{streaming.CompletedChunks},\"stream_chunks_fresh\":{streaming.FreshGeneratedChunks},\"stream_chunks_cached\":{streaming.CachedChunks},\"stream_batches_completed\":{streaming.CompletedBatches}," +
		SerializeTimingJson( "stream_sdf_generation", streaming.SdfGeneration ) + "," + SerializeTimingJson( "stream_mesh_queue", streaming.MeshQueue ) + "," +
		SerializeTimingJson( "stream_snapshot", streaming.SdfSnapshot ) + "," + SerializeTimingJson( "stream_worker_mesh", streaming.WorkerMesh ) + "," +
		SerializeTimingJson( "stream_publication_wait", streaming.PublicationWait ) + "," + SerializeTimingJson( "stream_upload", streaming.MainThreadUpload ) + "," +
		SerializeTimingJson( "stream_chunk_ready", streaming.RequestToRender ) + "," + SerializeTimingJson( "stream_batch", streaming.BatchCompletion );

	private static string SerializeTimingJson( string name, VoxelTimingDistribution timing ) =>
		$"\"{name}_avg_ms\":{Number( timing.AverageMilliseconds )},\"{name}_p95_ms\":{Number( timing.P95Milliseconds )},\"{name}_max_ms\":{Number( timing.MaximumMilliseconds )}";

	private static string StreamingCsv( VoxelChunkStreamingDiagnostics streaming ) => string.Join( ",",
		streaming.CompletedChunks, streaming.FreshGeneratedChunks, streaming.CachedChunks, streaming.CompletedBatches,
		TimingCsv( streaming.SdfGeneration ), TimingCsv( streaming.MeshQueue ), TimingCsv( streaming.SdfSnapshot ), TimingCsv( streaming.WorkerMesh ),
		TimingCsv( streaming.PublicationWait ), TimingCsv( streaming.MainThreadUpload ), TimingCsv( streaming.RequestToRender ), TimingCsv( streaming.BatchCompletion ) );

	private static string TimingCsv( VoxelTimingDistribution timing ) =>
		$"{Number( timing.AverageMilliseconds )},{Number( timing.P95Milliseconds )},{Number( timing.MaximumMilliseconds )}";

	private static string Timing( VoxelTimingDistribution timing ) => timing.Count == 0
		? "n/a"
		: $"{timing.AverageMilliseconds:F2} / {timing.P95Milliseconds:F2} / {timing.MaximumMilliseconds:F2} ms";

	private static string Number( double value ) => value.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture );
	private static string Csv( string value ) => "\"" + (value ?? string.Empty).Replace( "\"", "\"\"" ) + "\"";
	private static string Json( string value ) => (value ?? string.Empty).Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" ).Replace( "\r", "\\r" ).Replace( "\n", "\\n" );

	private static string FormatBytes( long bytes )
	{
		if ( bytes < 1024 ) return $"{bytes:N0} B";
		if ( bytes < 1024 * 1024 ) return $"{bytes / 1024.0:F2} KiB";
		return $"{bytes / (1024.0 * 1024.0):F2} MiB";
	}

	private readonly record struct EditCommand( Vector3 LocalPosition, float Radius, float Displacement );
	private readonly record struct BenchmarkGravityState( Rigidbody Body, bool Gravity );
	private readonly record struct ComparisonMetric( string Name, bool TriggersRerun );

	private sealed class PreviousScenarioMeasurement
	{
		public string RunId { get; init; }
		public Dictionary<string, double> Values { get; } = new();
	}

	private sealed class ScenarioComparison
	{
		public string ScenarioName { get; init; }
		public string BaselineRunId { get; set; } = string.Empty;
		public bool HasBaseline { get; set; }
		public Dictionary<string, double> PercentChanges { get; } = new();
		public double MaximumAbsolutePercent { get; set; }
		public bool MajorOutlier { get; set; }
		public string OutlierMetrics { get; set; } = string.Empty;
		public string ReproductionStatus { get; set; } = "not_needed";
	}

	private enum BenchmarkPhase
	{
		Idle,
		WaitInitialGeneration,
		Warmup,
		StartCollisionBacklog,
		WaitCollisionBacklog,
		StartCollisionProximityEdit,
		WaitCollisionProximityEdit,
		StartPhase4Planner,
		WaitPhase4Planner,
		StartPhase5EditProof,
		WaitPhase5EditProof,
		StartPhase4Transition,
		WaitPhase4TransitionGpu,
		WaitPhase4Transition,
		StartPhase4Indirect,
		WaitPhase4Indirect,
		StartPhase4Regular,
		WaitPhase4Regular,
		StartGpuLod5Ownership,
		WaitGpuLod5Ownership,
		StartGpuRealtimeEdits,
		RunGpuRealtimeEdits,
		WaitGpuRealtimeEdits,
		StartGpuAccumulatedEdits,
		RunGpuAccumulatedEdits,
		WaitGpuAccumulatedEdits,
		StartGpuAimedBrushEdits,
		RunGpuAimedBrushEdits,
		WaitGpuAimedBrushEdits,
		StartGpuCpuCollisionRebuild,
		WaitGpuCpuCollisionRebuild,
		StartGpuTransvoxelProof,
		WaitGpuTransvoxelProof,
		StartGpuPersistentStatic,
		WaitGpuPersistentStatic,
		StartGpuCameraSweepGeneration,
		RunGpuCameraSweepGeneration,
		StartGpuProductionRender,
		WaitGpuProductionRender,
		StartGpuMovementInfinity,
		RunGpuMovementInfinity,
		WaitGpuMovementInfinity,
		StartGpuMovementLine,
		RunGpuMovementLine,
		WaitGpuMovementLine,
		StartGpuMovementDiagonal,
		RunGpuMovementDiagonal,
		WaitGpuMovementDiagonal,
		StartGpuMovementVertical,
		RunGpuMovementVertical,
		WaitGpuMovementVertical,
		StartHighSpeedCollisionTraversal,
		RunHighSpeedCollisionTraversal,
		WaitHighSpeedCollisionTraversal,
		StartGpuLifecycleScenario,
		WaitGpuLifecycleScenario,
		StartGpuAsyncReadbackSaturation,
		WaitGpuAsyncReadbackSaturation,
		StartGpuResourceRecreation,
		WaitGpuResourceRecreation,
		StartGpuDedicatedServerStartup,
		WaitGpuDedicatedServerStartup,
		StartFinalStationarySoak,
		WaitFinalStationarySoak,
		StartLiveConfiguration,
		WaitLiveConfiguration,
		StartInfinityTraversal,
		RunInfinityTraversal,
		WaitInfinityTraversal,
		StartLineTraversal,
		RunLineTraversal,
		WaitLineTraversal,
		StartDiagonalTraversal,
		RunDiagonalTraversal,
		WaitDiagonalTraversal,
		StartSeamEdit,
		WaitSeamEdit,
		StartVariedEdits,
		RunVariedEdits,
		WaitVariedEdits,
		WaitReset,
		StartBulkEdit,
		WaitBulkEdit,
		StartSustainedDig,
		RunSustainedDig,
		WaitSustainedDig,
		StartSustainedPlace,
		RunSustainedPlace,
		WaitSustainedPlace,
		StartPostEditLineTraversal,
		RunPostEditLineTraversal,
		WaitPostEditLineTraversal,
		Complete,
		Failed
	}

	private enum TraversalPath
	{
		Infinity,
		Line,
		Diagonal,
		Vertical
	}

	private sealed class FrameSampler
	{
		private readonly List<double> _frameTimes = new( 4096 );
		private readonly List<double> _gpuTimes = new( 4096 );
		private readonly List<double> _editCallTimes = new( 512 );
		private double _drawCalls;
		private double _trianglesRendered;
		private double _objectsRendered;
		private double _materialChanges;
		private long _allocatedBytes;
		private long _gcPauseTicks;
		private int _gen0Collections;
		private int _gen1Collections;
		private int _gen2Collections;
		private int _exceptions;
		private bool _failed;
		private ulong _peakMemoryBytes;
		private uint _lastGpuFrameNumber;
		private bool _hasGpuFrameNumber;
		private int _gpuFramesToSkip = 2;
		private readonly VoxelTerrainDiagnostics _startingDiagnostics;
		private readonly VoxelCallCountSnapshot _startingCallCounts;
		private readonly int _startingMessagesSent;
		private readonly int _startingMessagesReceived;
		private long _lastEditTimestamp;
		private int _framesOver16Milliseconds;
		private int _framesOver33Milliseconds;
		private int _framesOver50Milliseconds;
		private int _framesOver100Milliseconds;
		private int _stutterEvents;
		private int _currentStutterFrames;
		private int _longestStutterFrames;
		private int _visualCoherenceViolationFrames;
		private double _networkOutBytesPerSecond;
		private double _networkInBytesPerSecond;
		private double _networkPingMilliseconds;
		private int _networkSampleCount;
		private int _maximumConnections;
		private ulong _peakTexturePoolUsedBytes;
		private ulong _peakTexturePoolNonEvictableBytes;
		private int _maximumPendingStreamingRequests;
		private int _stutterDiagnosticCount;
		private readonly List<double> _unaccountedFrameMilliseconds = new();

		public string Name { get; }
		public string Description { get; }
		public long StartingChunkTimingSequence { get; }
		public long StartingBatchTimingSequence { get; }
		public int EditCount { get; private set; }
		public int ChangedChunkEvents { get; private set; }

		public FrameSampler( string name, string description, VoxelTerrainDiagnostics startingDiagnostics, VoxelCallCountSnapshot startingCallCounts, long startingChunkTimingSequence, long startingBatchTimingSequence )
		{
			Name = name;
			Description = description;
			_startingDiagnostics = startingDiagnostics;
			_startingCallCounts = startingCallCounts;
			_startingMessagesSent = SumMessagesSent();
			_startingMessagesReceived = SumMessagesReceived();
			StartingChunkTimingSequence = startingChunkTimingSequence;
			StartingBatchTimingSequence = startingBatchTimingSequence;
		}

		public void RecordEdit( int changedChunks, double callMilliseconds )
		{
			EditCount++;
			ChangedChunkEvents += changedChunks;
			_editCallTimes.Add( callMilliseconds );
			_lastEditTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		}

		public void RecordVisualCoherenceViolation()
		{
			_visualCoherenceViolationFrames++;
		}

		public void RecordFailure()
		{
			_failed = true;
		}

		public bool Sample()
		{
			var frameMilliseconds = Sandbox.Diagnostics.PerformanceStats.FrameTime * 1000.0;
			_frameTimes.Add( frameMilliseconds );
			_framesOver16Milliseconds += frameMilliseconds >= 16.6667 ? 1 : 0;
			_framesOver33Milliseconds += frameMilliseconds >= 33.3333 ? 1 : 0;
			_framesOver50Milliseconds += frameMilliseconds >= 50.0 ? 1 : 0;
			_framesOver100Milliseconds += frameMilliseconds >= 100.0 ? 1 : 0;
			if ( frameMilliseconds >= 33.3333 )
			{
				var updateTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Update.GetMetric( 1 );
				var renderTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Render.GetMetric( 1 );
				var physicsTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Physics.GetMetric( 1 );
				var idleTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Idle.GetMetric( 1 );
				var asyncTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Async.GetMetric( 1 );
				var gcTiming = Sandbox.Diagnostics.PerformanceStats.Timings.GcPause.GetMetric( 1 );
				_unaccountedFrameMilliseconds.Add( VoxelManager.ComputeUnaccountedFrameMilliseconds( frameMilliseconds, updateTiming.Max, renderTiming.Max, physicsTiming.Max, idleTiming.Max, asyncTiming.Max, gcTiming.Max ) );
				if ( _currentStutterFrames == 0 ) _stutterEvents++;
				_currentStutterFrames++;
				_longestStutterFrames = System.Math.Max( _longestStutterFrames, _currentStutterFrames );
			}
			else
			{
				_currentStutterFrames = 0;
			}
			var gpuFrameNumber = Sandbox.Diagnostics.PerformanceStats.GpuFrameNumber;
			if ( !_hasGpuFrameNumber )
			{
				_lastGpuFrameNumber = gpuFrameNumber;
				_hasGpuFrameNumber = true;
			}
			else if ( gpuFrameNumber != _lastGpuFrameNumber )
			{
				_lastGpuFrameNumber = gpuFrameNumber;
				if ( _gpuFramesToSkip > 0 )
				{
					_gpuFramesToSkip--;
				}
				else if ( Sandbox.Diagnostics.PerformanceStats.GpuFrametime >= 0.0f )
				{
					_gpuTimes.Add( Sandbox.Diagnostics.PerformanceStats.GpuFrametime );
				}
			}
			_allocatedBytes += Sandbox.Diagnostics.PerformanceStats.BytesAllocated;
			_gcPauseTicks += Sandbox.Diagnostics.PerformanceStats.GcPause;
			_gen0Collections += Sandbox.Diagnostics.PerformanceStats.Gen0Collections;
			_gen1Collections += Sandbox.Diagnostics.PerformanceStats.Gen1Collections;
			_gen2Collections += Sandbox.Diagnostics.PerformanceStats.Gen2Collections;
			_exceptions += Sandbox.Diagnostics.PerformanceStats.Exceptions;
			_peakMemoryBytes = System.Math.Max( _peakMemoryBytes, Sandbox.Diagnostics.PerformanceStats.ApproximateProcessMemoryUsage );
			var render = Sandbox.Diagnostics.FrameStats.Current;
			_drawCalls += render.DrawCalls;
			_trianglesRendered += render.TrianglesRendered;
			_objectsRendered += render.ObjectsRendered;
			_materialChanges += render.MaterialChanges;
			_peakTexturePoolUsedBytes = System.Math.Max( _peakTexturePoolUsedBytes, render.TexturePoolUsedBytes );
			_peakTexturePoolNonEvictableBytes = System.Math.Max( _peakTexturePoolNonEvictableBytes, render.TexturePoolNonEvictableBytes );
			_maximumPendingStreamingRequests = System.Math.Max( _maximumPendingStreamingRequests, render.PendingStreamingRequests );
			if ( Networking.IsActive )
			{
				_networkOutBytesPerSecond += Networking.HostStats.OutBytesPerSecond;
				_networkInBytesPerSecond += Networking.HostStats.InBytesPerSecond;
				var connections = Connection.All;
				_maximumConnections = System.Math.Max( _maximumConnections, connections.Count );
				double pingTotal = 0.0;
				foreach ( var connection in connections ) pingTotal += connection.Stats.Ping;
				_networkPingMilliseconds += connections.Count > 0 ? pingTotal / connections.Count : 0.0;
				_networkSampleCount++;
			}
			return frameMilliseconds >= 33.3333;
		}

		public bool TryConsumeStutterDiagnostic() => _stutterDiagnosticCount++ < 2;

		public ScenarioResult Complete( VoxelTerrainDiagnostics diagnostics, VoxelCallCountSnapshot callCounts, VoxelChunkStreamingDiagnostics streaming, double elapsedMilliseconds, VoxelGpuTransvoxelProofResult? gpuProof, VoxelGpuTerrainDiagnostics? gpuTerrain, VoxelGpuPhase2BProofResult? gpuPhase2BProof, VoxelGpuPhase3AProofResult? gpuPhase3AProof, VoxelGpuPhase3BProofResult? gpuPhase3BProof, VoxelClipboxPlannerProofReport? clipboxPlannerProof, VoxelGpuIndirectRenderProofReport? indirectRenderProof, VoxelTransvoxelTransitionProofResult? transitionProof, VoxelGpuClipboxSeamProofReport? lod5OwnershipProof )
		{
			_frameTimes.Sort();
			_gpuTimes.Sort();
			_editCallTimes.Sort();
			_unaccountedFrameMilliseconds.Sort();
			var count = _frameTimes.Count;
			var gpuCount = _gpuTimes.Count;
			var timingFrames = System.Math.Clamp( count, 1, 4096 );
			var updateTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Update.GetMetric( timingFrames );
			var renderTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Render.GetMetric( timingFrames );
			var physicsTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Physics.GetMetric( timingFrames );
			var networkTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Network.GetMetric( timingFrames );
			var frameAverage = Average( _frameTimes );
			var frameP99 = Percentile( _frameTimes, 0.99 );
			var frameP999 = Percentile( _frameTimes, 0.999 );
			var frameMaximum = count > 0 ? _frameTimes[^1] : 0.0;
			var unaccountedFrameMaximum = _unaccountedFrameMilliseconds.Count > 0 ? _unaccountedFrameMilliseconds[^1] : 0.0;
			var scenarioDiagnostics = diagnostics with
			{
				VisualBatchBuiltChunks = (int)System.Math.Clamp( diagnostics.TotalVisualBuildsCompleted - _startingDiagnostics.TotalVisualBuildsCompleted, 0, int.MaxValue ),
				VisualBatchElapsedMilliseconds = System.Math.Max( 0.0, diagnostics.TotalVisualBatchElapsedMilliseconds - _startingDiagnostics.TotalVisualBatchElapsedMilliseconds ),
				SnapshotWaitMilliseconds = System.Math.Max( 0.0, diagnostics.TotalSnapshotWaitMilliseconds - _startingDiagnostics.TotalSnapshotWaitMilliseconds ),
				SnapshotCopyMilliseconds = System.Math.Max( 0.0, diagnostics.TotalSnapshotCopyMilliseconds - _startingDiagnostics.TotalSnapshotCopyMilliseconds ),
				WorkerMeshMilliseconds = System.Math.Max( 0.0, diagnostics.TotalWorkerMeshMilliseconds - _startingDiagnostics.TotalWorkerMeshMilliseconds ),
				MainThreadUploadMilliseconds = System.Math.Max( 0.0, diagnostics.TotalMainThreadUploadMilliseconds - _startingDiagnostics.TotalMainThreadUploadMilliseconds ),
				TotalCollisionSnapshotWaitMilliseconds = System.Math.Max( 0.0, diagnostics.TotalCollisionSnapshotWaitMilliseconds - _startingDiagnostics.TotalCollisionSnapshotWaitMilliseconds ),
				TotalCollisionSnapshotCopyMilliseconds = System.Math.Max( 0.0, diagnostics.TotalCollisionSnapshotCopyMilliseconds - _startingDiagnostics.TotalCollisionSnapshotCopyMilliseconds ),
				TotalCollisionWorkerMeshMilliseconds = System.Math.Max( 0.0, diagnostics.TotalCollisionWorkerMeshMilliseconds - _startingDiagnostics.TotalCollisionWorkerMeshMilliseconds ),
				TotalCollisionModelBuildMilliseconds = System.Math.Max( 0.0, diagnostics.TotalCollisionModelBuildMilliseconds - _startingDiagnostics.TotalCollisionModelBuildMilliseconds ),
				TotalCollisionPublicationMilliseconds = System.Math.Max( 0.0, diagnostics.TotalCollisionPublicationMilliseconds - _startingDiagnostics.TotalCollisionPublicationMilliseconds )
			};
			return new ScenarioResult
			{
				Name = Name,
				Description = Description,
				Passed = !_failed && diagnostics.FailedVisualChunks == 0 && diagnostics.PendingVisualBuilds == 0 && diagnostics.PendingCollisionBuilds == 0 && !diagnostics.PlayerSafetyActive && _exceptions == 0 && _visualCoherenceViolationFrames == 0 && (!gpuProof.HasValue || gpuProof.Value.Passed) && (!gpuPhase2BProof.HasValue || gpuPhase2BProof.Value.Passed) && (!gpuPhase3AProof.HasValue || gpuPhase3AProof.Value.Passed) && (!gpuPhase3BProof.HasValue || gpuPhase3BProof.Value.Passed) && (!clipboxPlannerProof.HasValue || clipboxPlannerProof.Value.Passed) && (!indirectRenderProof.HasValue || indirectRenderProof.Value.Passed) && (!transitionProof.HasValue || transitionProof.Value.Passed) && (!transitionProof.HasValue || !transitionProof.Value.GpuCaseProofAvailable || transitionProof.Value.GpuCaseProofPassed),
				EditCount = EditCount,
				ChangedChunkEvents = ChangedChunkEvents,
				VisualCoherenceViolationFrames = _visualCoherenceViolationFrames,
				ElapsedMilliseconds = elapsedMilliseconds,
				FrameCount = count,
				FrameAverageMilliseconds = frameAverage,
				FrameP50Milliseconds = Percentile( _frameTimes, 0.50 ),
				FrameP95Milliseconds = Percentile( _frameTimes, 0.95 ),
				FrameP99Milliseconds = frameP99,
				FrameP999Milliseconds = frameP999,
				FrameMaximumMilliseconds = frameMaximum,
				UnaccountedFrameMaximumMilliseconds = unaccountedFrameMaximum,
				FrameStandardDeviationMilliseconds = StandardDeviation( _frameTimes, frameAverage ),
				AverageFramesPerSecond = ToFramesPerSecond( frameAverage ),
				OnePercentLowFramesPerSecond = ToFramesPerSecond( frameP99 ),
				PointOnePercentLowFramesPerSecond = ToFramesPerSecond( frameP999 ),
				MinimumFramesPerSecond = ToFramesPerSecond( frameMaximum ),
				FramesOver16Milliseconds = _framesOver16Milliseconds,
				FramesOver33Milliseconds = _framesOver33Milliseconds,
				FramesOver50Milliseconds = _framesOver50Milliseconds,
				FramesOver100Milliseconds = _framesOver100Milliseconds,
				StutterEvents = _stutterEvents,
				LongestStutterFrames = _longestStutterFrames,
				GpuAverageMilliseconds = Average( _gpuTimes ),
				GpuP95Milliseconds = Percentile( _gpuTimes, 0.95 ),
				GpuMaximumMilliseconds = gpuCount > 0 ? _gpuTimes[^1] : 0.0,
				AllocatedBytes = _allocatedBytes,
				GcPauseMilliseconds = System.TimeSpan.FromTicks( _gcPauseTicks ).TotalMilliseconds,
				Gen0Collections = _gen0Collections,
				Gen1Collections = _gen1Collections,
				Gen2Collections = _gen2Collections,
				Exceptions = _exceptions,
				PeakMemoryBytes = _peakMemoryBytes,
				DrawCallsAverage = count > 0 ? _drawCalls / count : 0.0,
				TrianglesRenderedAverage = count > 0 ? _trianglesRendered / count : 0.0,
				ObjectsRenderedAverage = count > 0 ? _objectsRendered / count : 0.0,
				MaterialChangesAverage = count > 0 ? _materialChanges / count : 0.0,
				EditCallAverageMilliseconds = Average( _editCallTimes ),
				EditCallP95Milliseconds = Percentile( _editCallTimes, 0.95 ),
				EditCallMaximumMilliseconds = _editCallTimes.Count > 0 ? _editCallTimes[^1] : 0.0,
				PostEditSettleMilliseconds = _lastEditTimestamp == 0 ? 0.0 : System.Diagnostics.Stopwatch.GetElapsedTime( _lastEditTimestamp ).TotalMilliseconds,
				EditThroughputPerSecond = elapsedMilliseconds > 0.0 ? EditCount * 1000.0 / elapsedMilliseconds : 0.0,
				UpdateAverageMilliseconds = updateTiming.Avg,
				UpdateMaximumMilliseconds = updateTiming.Max,
				RenderAverageMilliseconds = renderTiming.Avg,
				RenderMaximumMilliseconds = renderTiming.Max,
				PhysicsAverageMilliseconds = physicsTiming.Avg,
				PhysicsMaximumMilliseconds = physicsTiming.Max,
				NetworkAverageMilliseconds = networkTiming.Avg,
				NetworkMaximumMilliseconds = networkTiming.Max,
				NetworkOutBytesPerSecondAverage = _networkSampleCount > 0 ? _networkOutBytesPerSecond / _networkSampleCount : 0.0,
				NetworkInBytesPerSecondAverage = _networkSampleCount > 0 ? _networkInBytesPerSecond / _networkSampleCount : 0.0,
				NetworkPingMillisecondsAverage = _networkSampleCount > 0 ? _networkPingMilliseconds / _networkSampleCount : 0.0,
				MaximumConnections = _maximumConnections,
				MessagesSent = System.Math.Max( 0, SumMessagesSent() - _startingMessagesSent ),
				MessagesReceived = System.Math.Max( 0, SumMessagesReceived() - _startingMessagesReceived ),
				PeakTexturePoolUsedBytes = _peakTexturePoolUsedBytes,
				PeakTexturePoolNonEvictableBytes = _peakTexturePoolNonEvictableBytes,
				MaximumPendingStreamingRequests = _maximumPendingStreamingRequests,
				Diagnostics = scenarioDiagnostics,
				Streaming = streaming,
				CallCounts = callCounts.Subtract( _startingCallCounts ),
				GpuTransvoxelProof = gpuProof,
				GpuTerrain = gpuTerrain,
				GpuPhase2BProof = gpuPhase2BProof,
				GpuPhase3AProof = gpuPhase3AProof,
				GpuPhase3BProof = gpuPhase3BProof,
				ClipboxPlannerProof = clipboxPlannerProof,
				IndirectRenderProof = indirectRenderProof,
				TransitionProof = transitionProof,
				Lod5OwnershipProof = lod5OwnershipProof
			};
		}

		private static double Average( List<double> values )
		{
			if ( values.Count == 0 ) return 0.0;
			double sum = 0.0;
			foreach ( var value in values ) sum += value;
			return sum / values.Count;
		}

		private static double Percentile( List<double> sorted, double percentile )
		{
			if ( sorted.Count == 0 ) return 0.0;
			var index = (int)System.Math.Clamp( System.Math.Ceiling( sorted.Count * percentile ) - 1, 0, sorted.Count - 1 );
			return sorted[index];
		}

		private static double StandardDeviation( List<double> values, double average )
		{
			if ( values.Count == 0 ) return 0.0;
			double squaredDifference = 0.0;
			foreach ( var value in values ) squaredDifference += (value - average) * (value - average);
			return System.Math.Sqrt( squaredDifference / values.Count );
		}

		private static double ToFramesPerSecond( double milliseconds ) => milliseconds > 0.0 ? 1000.0 / milliseconds : 0.0;

		private static int SumMessagesSent()
		{
			var total = 0;
			foreach ( var connection in Connection.All ) total += connection.MessagesSent;
			return total;
		}

		private static int SumMessagesReceived()
		{
			var total = 0;
			foreach ( var connection in Connection.All ) total += connection.MessagesRecieved;
			return total;
		}
	}

	private sealed class ScenarioResult
	{
		public string Name { get; init; }
		public string Description { get; init; }
		public bool Passed { get; init; }
		public int EditCount { get; init; }
		public int ChangedChunkEvents { get; init; }
		public int VisualCoherenceViolationFrames { get; init; }
		public double ElapsedMilliseconds { get; init; }
		public int FrameCount { get; init; }
		public double FrameAverageMilliseconds { get; init; }
		public double FrameP50Milliseconds { get; init; }
		public double FrameP95Milliseconds { get; init; }
		public double FrameP99Milliseconds { get; init; }
		public double FrameP999Milliseconds { get; init; }
		public double FrameMaximumMilliseconds { get; init; }
		public double UnaccountedFrameMaximumMilliseconds { get; init; }
		public double FrameStandardDeviationMilliseconds { get; init; }
		public double AverageFramesPerSecond { get; init; }
		public double OnePercentLowFramesPerSecond { get; init; }
		public double PointOnePercentLowFramesPerSecond { get; init; }
		public double MinimumFramesPerSecond { get; init; }
		public int FramesOver16Milliseconds { get; init; }
		public int FramesOver33Milliseconds { get; init; }
		public int FramesOver50Milliseconds { get; init; }
		public int FramesOver100Milliseconds { get; init; }
		public int StutterEvents { get; init; }
		public int LongestStutterFrames { get; init; }
		public double GpuAverageMilliseconds { get; init; }
		public double GpuP95Milliseconds { get; init; }
		public double GpuMaximumMilliseconds { get; init; }
		public long AllocatedBytes { get; init; }
		public double GcPauseMilliseconds { get; init; }
		public int Gen0Collections { get; init; }
		public int Gen1Collections { get; init; }
		public int Gen2Collections { get; init; }
		public int Exceptions { get; init; }
		public ulong PeakMemoryBytes { get; init; }
		public double DrawCallsAverage { get; init; }
		public double TrianglesRenderedAverage { get; init; }
		public double ObjectsRenderedAverage { get; init; }
		public double MaterialChangesAverage { get; init; }
		public double EditCallAverageMilliseconds { get; init; }
		public double EditCallP95Milliseconds { get; init; }
		public double EditCallMaximumMilliseconds { get; init; }
		public double PostEditSettleMilliseconds { get; init; }
		public double EditThroughputPerSecond { get; init; }
		public double UpdateAverageMilliseconds { get; init; }
		public double UpdateMaximumMilliseconds { get; init; }
		public double RenderAverageMilliseconds { get; init; }
		public double RenderMaximumMilliseconds { get; init; }
		public double PhysicsAverageMilliseconds { get; init; }
		public double PhysicsMaximumMilliseconds { get; init; }
		public double NetworkAverageMilliseconds { get; init; }
		public double NetworkMaximumMilliseconds { get; init; }
		public double NetworkOutBytesPerSecondAverage { get; init; }
		public double NetworkInBytesPerSecondAverage { get; init; }
		public double NetworkPingMillisecondsAverage { get; init; }
		public int MaximumConnections { get; init; }
		public int MessagesSent { get; init; }
		public int MessagesReceived { get; init; }
		public ulong PeakTexturePoolUsedBytes { get; init; }
		public ulong PeakTexturePoolNonEvictableBytes { get; init; }
		public int MaximumPendingStreamingRequests { get; init; }
		public VoxelTerrainDiagnostics Diagnostics { get; init; }
		public VoxelChunkStreamingDiagnostics Streaming { get; init; }
		public VoxelCallCountSnapshot CallCounts { get; init; }
		public VoxelGpuTransvoxelProofResult? GpuTransvoxelProof { get; init; }
		public VoxelGpuTerrainDiagnostics? GpuTerrain { get; init; }
		public VoxelGpuPhase2BProofResult? GpuPhase2BProof { get; init; }
		public VoxelGpuPhase3AProofResult? GpuPhase3AProof { get; init; }
		public VoxelGpuPhase3BProofResult? GpuPhase3BProof { get; init; }
		public VoxelClipboxPlannerProofReport? ClipboxPlannerProof { get; init; }
		public VoxelGpuIndirectRenderProofReport? IndirectRenderProof { get; init; }
		public VoxelTransvoxelTransitionProofResult? TransitionProof { get; init; }
		public VoxelGpuClipboxSeamProofReport? Lod5OwnershipProof { get; init; }
	}
}
