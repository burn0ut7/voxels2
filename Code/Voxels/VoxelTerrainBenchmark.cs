using System.Globalization;

public sealed class VoxelTerrainBenchmark : Component
{
	private const string ReportDirectory = "voxel-terrain-benchmarks";
	private const string HistoryJsonLinesPath = ReportDirectory + "/history.jsonl";
	private const string HistoryCsvPath = ReportDirectory + "/history.csv";
	private const string LatestJsonPath = ReportDirectory + "/latest-report.json";
	private const string LatestMarkdownPath = ReportDirectory + "/latest-report.md";
	private const string DashboardPath = ReportDirectory + "/dashboard.html";
	private const int SuiteVersion = 44;
	private const string BenchmarkWorldScene = "basic_example";
	private const int InfinityPathSampleCount = 512;

	private static readonly ScenarioDefinition[] RequiredScenarios =
	{
		new( "cold_generation", "Cold GPU terrain generation reaches a settled playable world.", ScenarioKind.Generation ),
		new( "player_natural_traversal", "A real PlayerController follows an outward-and-back terrain journey to drive streaming.", ScenarioKind.Traversal ),
		new( "varied_edits", "Small digs and placements exercise varied radii, signs, seams, and materials.", ScenarioKind.VariedEdits ),
		new( "bulk_edit", "A large terrain deformation crosses several visual cells in one operation.", ScenarioKind.BulkEdit ),
		new( "sustained_world_sweep_and_depth_dig_20hz", "Repeated world-wide digging and a descending shaft exercise sustained deformation.", ScenarioKind.SustainedDig ),
		new( "sustained_world_spiral_place_20hz", "Repeated world-wide placement exercises sustained additive deformation.", ScenarioKind.SustainedPlace ),
		new( "player_post_edit_revisit", "The player revisits edited terrain after deformation and streaming have settled.", ScenarioKind.PostEditTraversal )
	};

	private readonly List<ScenarioResult> _results = new();
	private readonly Vector3[] _infinityPathPositions = new Vector3[InfinityPathSampleCount + 1];
	private readonly float[] _infinityPathDistances = new float[InfinityPathSampleCount + 1];
	private readonly List<EditCommand> _variedEdits = new();
	private VoxelManager _manager;
	private PlayerController _player;
	private GameObject _playerFixture;
	private FrameSampler _sampler;
	private BenchmarkPhase _phase;
	private int _scenarioIndex = -1;
	private int _editIndex;
	private long _nextEditTimestamp;
	private long _phaseStartTimestamp;
	private long _traversalStartTimestamp;
	private float _traversalDistance;
	private float _traversalLoopLength;
	private float _activeTraversalSpeed;
	private Vector3 _traversalStartWorldPosition;
	private Vector3 _traversalStartLocalPosition;
	private Vector3 _traversalLastWorldPosition;
	private int _forcedFailure;
	private string _failureReason = string.Empty;
	private string _runId = string.Empty;
	private bool _initializationComplete;
	private int _initializationAttempts;
	private bool _runRequested;
	private bool _worldSettingsCaptured;
	private bool _originalCaptureCallCounts;

	[Property, Group( "Run" )]
	public bool RunOnStart { get; set; } = true;

	[Property, ReadOnly, Group( "Run" )]
	public string Revision { get; private set; } = "unknown";

	[Property, ReadOnly, Group( "Run" )]
	public bool WorkingTreeDirty { get; private set; } = true;

	[Property, Group( "Run" ), Range( 0, 240 )]
	public int WarmupFrames { get; set; } = 30;

	[Property, Group( "Run" ), Range( 10.0f, 180.0f )]
	public float ScenarioTimeoutSeconds { get; set; } = 120.0f;

	[Property, Group( "Editing" ), Range( 10, 240 )]
	public int SustainedEditCount { get; set; } = 80;

	[Property, Group( "Editing" ), Range( 0.01f, 0.25f )]
	public float SustainedEditIntervalSeconds { get; set; } = 0.05f;

	[Property, Group( "Player Journey" ), Range( 128.0f, 10000.0f )]
	public float TraversalDistance { get; set; } = 2048.0f;

	[Property, Group( "Player Journey" ), Range( 1.0f, 5000.0f )]
	public float TraversalSpeed { get; set; } = 700.0f;

	[Property, ReadOnly, Group( "Run" )]
	public string LastReportPath { get; private set; } = string.Empty;

	[Property, ReadOnly, Group( "Run" )]
	public string CurrentScenario => _sampler?.Name ?? ( _scenarioIndex >= 0 && _scenarioIndex < RequiredScenarios.Length ? RequiredScenarios[_scenarioIndex].Name : _phase.ToString() );

	[Property, ReadOnly, Group( "Run" )]
	public string CurrentScenarioDescription => _sampler?.Description ?? string.Empty;

	[Property, ReadOnly, Group( "Run" )]
	public bool IsSuiteComplete => _phase == BenchmarkPhase.Complete && _results.Count == RequiredScenarios.Length;

	[Property, ReadOnly, Group( "Run" )]
	public bool IsRunning => _phase is not BenchmarkPhase.Idle and not BenchmarkPhase.Complete and not BenchmarkPhase.Failed;

	[Property, ReadOnly, Group( "Run" )]
	public string Status => $"mode=GpuOnly; phase={_phase}; scenario={CurrentScenario}; completed={_results.Count}/{RequiredScenarios.Length}; elapsed={ElapsedSeconds():F1}s";

	protected override void OnValidate()
	{
		WarmupFrames = System.Math.Clamp( WarmupFrames, 0, 240 );
		ScenarioTimeoutSeconds = System.Math.Clamp( ScenarioTimeoutSeconds, 10.0f, 180.0f );
		SustainedEditCount = System.Math.Clamp( SustainedEditCount, 10, 240 );
		SustainedEditIntervalSeconds = System.Math.Clamp( SustainedEditIntervalSeconds, 0.01f, 0.25f );
		TraversalDistance = System.Math.Clamp( TraversalDistance, 128.0f, 10000.0f );
		TraversalSpeed = System.Math.Clamp( TraversalSpeed, 1.0f, 5000.0f );
	}

	protected override void OnStart() => TryInitialize();

	protected override void OnDisabled()
	{
		RestoreSettings();
		DestroyPlayerFixture();
		_manager = null;
		_initializationComplete = false;
	}

	protected override void OnDestroy()
	{
		RestoreSettings();
		DestroyPlayerFixture();
	}

	[Button]
	public void RunBenchmark()
	{
		if ( IsRunning )
		{
			Log.Warning( "Voxel terrain benchmark is already running." );
			return;
		}
		if ( !_initializationComplete )
		{
			_runRequested = true;
			return;
		}
		BeginRun();
	}

	protected override void OnUpdate()
	{
		if ( !_initializationComplete && !TryInitialize() )
		{
			if ( ++_initializationAttempts == 120 ) Log.Error( "Voxel terrain benchmark requires a VoxelManager." );
			return;
		}
		if ( _runRequested && !IsRunning ) BeginRun();
		if ( _sampler is not null ) _sampler.Sample();
		if ( _sampler is not null && _manager.HasPartialVisualEditPublication ) _sampler.RecordVisualCoherenceViolation();
		if ( !IsRunning ) return;
		if ( ElapsedSeconds() > ScenarioTimeoutSeconds )
		{
			FailRun( $"scenario {CurrentScenario} exceeded {ScenarioTimeoutSeconds:F0}s; settle={_manager.TerrainSettleDiagnostics}; gpu={_manager.GpuTerrainLiveDiagnostics}" );
			return;
		}

		switch ( _phase )
		{
			case BenchmarkPhase.WaitGeneration:
				if ( IsSettled() ) FinishScenario();
				break;
			case BenchmarkPhase.Warmup:
				if ( --_editIndex <= 0 ) StartScenarioWork();
				break;
			case BenchmarkPhase.Traverse:
				RunTraversal();
				break;
			case BenchmarkPhase.WaitAfterTraversal:
				if ( IsSettled() ) FinishScenario();
				break;
			case BenchmarkPhase.Edit:
				RunEdits();
				break;
			case BenchmarkPhase.WaitAfterEdit:
				if ( IsSettled() ) FinishScenario();
				break;
		}
	}

	private bool TryInitialize()
	{
		_manager = GameObject?.Components.Get<VoxelManager>() ?? Scene.GetAllComponents<VoxelManager>().FirstOrDefault();
		if ( _manager is null ) return false;
		_initializationComplete = true;
		_initializationAttempts = 0;
		EnsurePlayerFixture();
		BuildVariedEdits();
		if ( RunOnStart && _phase == BenchmarkPhase.Idle ) BeginRun();
		return true;
	}

	private void BeginRun()
	{
		_runRequested = false;
		_results.Clear();
		_failureReason = string.Empty;
		_forcedFailure = 0;
		_scenarioIndex = 0;
		_runId = System.DateTime.UtcNow.ToString( "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture );
		var identity = VoxelBenchmarkGitIdentity.Load();
		Revision = identity.Revision;
		WorkingTreeDirty = identity.WorkingTreeDirty;
		CaptureSettings();
		_manager.CaptureCallCounts = true;
		_manager.SetBenchmarkPlayerProtection( true );
		BeginScenario( RequiredScenarios[_scenarioIndex] );
		try
		{
			_manager.GenerateGpuTerrainWorld();
			_phase = BenchmarkPhase.WaitGeneration;
		}
		catch ( System.Exception exception )
		{
			FailRun( $"GPU terrain startup failed: {exception.Message}" );
		}
		Log.Info( $"Voxel terrain benchmark {_runId} started in GPU-only mode on {BenchmarkWorldScene} at revision {Revision} (dirty={WorkingTreeDirty})." );
	}

	private void BeginScenario( ScenarioDefinition definition )
	{
		_sampler = new FrameSampler( definition.Name, definition.Description, _manager.CaptureTerrainDiagnostics(), _manager.CaptureCallCountSnapshot(), _manager.LatestChunkTimingSequence, _manager.LatestBatchTimingSequence );
		_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		Log.Info( $"Voxel terrain benchmark scenario started: {definition.Name}." );
	}

	private void StartScenarioWork()
	{
		var definition = RequiredScenarios[_scenarioIndex];
		if ( definition.Kind is ScenarioKind.Traversal or ScenarioKind.PostEditTraversal )
		{
			_manager.SetBenchmarkPlayerProtection( false );
			if ( !BeginTraversal() ) return;
			_phase = BenchmarkPhase.Traverse;
			return;
		}

		_manager.SetBenchmarkPlayerProtection( true );
		_editIndex = 0;
		_nextEditTimestamp = 0;
		_phase = BenchmarkPhase.Edit;
	}

	private void FinishScenario()
	{
		if ( _sampler is null ) return;
		var definition = RequiredScenarios[_scenarioIndex];
		if ( definition.Kind is ScenarioKind.Traversal or ScenarioKind.PostEditTraversal )
		{
			var calls = _manager.CaptureCallCountSnapshot().Subtract( _sampler.StartingCallCounts );
			if ( calls.Enumerate().FirstOrDefault( entry => entry.Name == "player.traversal_updates" ).Count <= 0 )
			{
				FailRun( $"{definition.Name} did not move a PlayerController." );
				return;
			}
		}
		CompleteScenario( _forcedFailure == 0 );
		if ( _phase == BenchmarkPhase.Failed ) return;
		_scenarioIndex++;
		if ( _scenarioIndex >= RequiredScenarios.Length )
		{
			_phase = BenchmarkPhase.Complete;
			WriteReports();
			_manager.SetBenchmarkPlayerProtection( false );
			Log.Info( $"Voxel terrain benchmark {_runId} complete: {_results.Count}/{RequiredScenarios.Length} scenarios." );
			return;
		}
		BeginScenario( RequiredScenarios[_scenarioIndex] );
		if ( WarmupFrames > 0 )
		{
			_editIndex = WarmupFrames + 1;
			_phase = BenchmarkPhase.Warmup;
		}
		else StartScenarioWork();
	}

	private void CompleteScenario( bool passed )
	{
		var streaming = _manager.CaptureChunkStreamingDiagnostics( _sampler.StartingChunkTimingSequence, _sampler.StartingBatchTimingSequence );
		var result = _sampler.Complete( _manager.CaptureTerrainDiagnostics(), _manager.CaptureGpuTerrainDiagnostics(), _manager.CaptureCallCountSnapshot(), streaming, System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp ).TotalMilliseconds, passed && string.IsNullOrEmpty( _failureReason ) );
		_results.Add( result );
		Log.Info( $"Voxel terrain benchmark scenario {result.Name}: result={(result.Passed ? "PASS" : "FAIL")}, avgFps={result.AverageFramesPerSecond:F1}, p95={result.FrameP95Milliseconds:F2}ms, edits={result.EditCount}, traversalCalls={result.TraversalUpdates}." );
		_sampler = null;
	}

	private void FailRun( string reason )
	{
		_failureReason = reason;
		_forcedFailure = 1;
		_sampler?.RecordFailure();
		Log.Error( $"Voxel terrain benchmark failed: {reason}" );
		if ( _sampler is not null ) CompleteScenario( false );
		_phase = BenchmarkPhase.Failed;
		WriteReports();
	}

	private bool IsSettled() => _manager.IsTerrainSettled && !_manager.IsPlayerSafetyActive;

	private void RunEdits()
	{
		var definition = RequiredScenarios[_scenarioIndex];
		var count = definition.Kind switch
		{
			ScenarioKind.VariedEdits => _variedEdits.Count,
			ScenarioKind.BulkEdit => 1,
			ScenarioKind.SustainedDig or ScenarioKind.SustainedPlace => SustainedEditCount,
			_ => 0
		};
		if ( _editIndex >= count )
		{
			_phase = BenchmarkPhase.WaitAfterEdit;
			return;
		}
		var now = System.Diagnostics.Stopwatch.GetTimestamp();
		if ( _nextEditTimestamp != 0 && now < _nextEditTimestamp ) return;
		var localPosition = Vector3.Zero;
		var radius = _manager.VoxelSize * 3.0f;
		var displacement = _manager.VoxelSize * 0.8f;
		var interval = SustainedEditIntervalSeconds;
		if ( definition.Kind == ScenarioKind.VariedEdits )
		{
			var edit = _variedEdits[_editIndex];
			localPosition = edit.LocalPosition;
			radius = edit.Radius;
			displacement = edit.Displacement;
			interval = 0.08f;
		}
		else if ( definition.Kind == ScenarioKind.SustainedDig )
		{
			localPosition = GetWorldSweepAndDepthPosition( _editIndex );
		}
		else if ( definition.Kind == ScenarioKind.SustainedPlace )
		{
			localPosition = GetWorldWidePlacementPosition( _editIndex );
			displacement = -displacement;
		}
		else if ( definition.Kind == ScenarioKind.BulkEdit )
		{
			radius = _manager.VoxelSize * 12.0f;
			displacement = _manager.VoxelSize * 4.0f;
		}

		var start = System.Diagnostics.Stopwatch.GetTimestamp();
		var changedChunks = _manager.DisplaceSdf( _manager.GameObject.WorldTransform.PointToWorld( localPosition ), radius, displacement );
		var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime( start ).TotalMilliseconds;
		_sampler.RecordEdit( changedChunks, elapsed );
		_editIndex++;
		_nextEditTimestamp = now + (long)( interval * System.Diagnostics.Stopwatch.Frequency );
	}

	private bool BeginTraversal()
	{
		_player = Scene.GetAllComponents<PlayerController>().FirstOrDefault();
		if ( _player is null )
		{
			FailRun( "A player journey requires a PlayerController." );
			return false;
		}
		_traversalStartWorldPosition = _player.WorldPosition;
		_traversalStartLocalPosition = _manager.GameObject.WorldTransform.PointToLocal( _traversalStartWorldPosition );
		_traversalLastWorldPosition = _traversalStartWorldPosition;
		_traversalDistance = 0.0f;
		_activeTraversalSpeed = TraversalSpeed;
		_traversalLoopLength = BuildInfinityPath();
		_traversalStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		return true;
	}

	private void RunTraversal()
	{
		_traversalDistance = System.MathF.Min( _traversalLoopLength, _traversalDistance + _activeTraversalSpeed * Time.Delta );
		var offset = SampleInfinityPath( _traversalDistance );
		var worldPosition = _manager.GameObject.WorldTransform.PointToWorld( _traversalStartLocalPosition + offset );
		var velocity = Time.Delta > 0.0f ? (worldPosition - _traversalLastWorldPosition) / Time.Delta : Vector3.Zero;
		_traversalLastWorldPosition = worldPosition;
		MovePlayer( worldPosition, velocity );
		if ( _traversalDistance >= _traversalLoopLength )
		{
			MovePlayer( _traversalStartWorldPosition );
			_phase = BenchmarkPhase.WaitAfterTraversal;
		}
	}

	private void MovePlayer( Vector3 position, Vector3 velocity = default )
	{
		if ( _player is null ) return;
		_player.WorldPosition = position;
		_manager.RecordPlayerTraversalUpdate();
		if ( _player.Body is null ) return;
		_player.Body.Velocity = velocity;
		_player.Body.AngularVelocity = Vector3.Zero;
	}

	private float BuildInfinityPath()
	{
		var halfDistance = TraversalDistance * 0.5f;
		_infinityPathPositions[0] = Vector3.Zero;
		_infinityPathDistances[0] = 0.0f;
		for ( var index = 1; index <= InfinityPathSampleCount; index++ )
		{
			var angle = index * (System.MathF.PI * 2.0f / InfinityPathSampleCount);
			var position = new Vector3( System.MathF.Sin( angle ) * halfDistance, System.MathF.Sin( angle ) * System.MathF.Cos( angle ) * halfDistance, 0.0f );
			_infinityPathPositions[index] = position;
			_infinityPathDistances[index] = _infinityPathDistances[index - 1] + (position - _infinityPathPositions[index - 1]).Length;
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
		var length = _infinityPathDistances[upper] - _infinityPathDistances[lower];
		var fraction = length > 0.0f ? (distance - _infinityPathDistances[lower]) / length : 0.0f;
		return _infinityPathPositions[lower] + (_infinityPathPositions[upper] - _infinityPathPositions[lower]) * fraction;
	}

	private Vector3 GetWorldSweepAndDepthPosition( int index )
	{
		var surfaceCount = System.Math.Max( 1, SustainedEditCount * 2 / 3 );
		if ( index >= surfaceCount ) return new Vector3( 0.0f, 0.0f, -(index - surfaceCount) / 3.0f * _manager.VoxelSize * 0.75f );
		var width = (int)System.Math.Ceiling( System.Math.Sqrt( surfaceCount ) );
		var row = index / width;
		var column = index % width;
		if ( (row & 1) != 0 ) column = width - 1 - column;
		var maximumRow = System.Math.Max( 1, (surfaceCount - 1) / width );
		var extent = System.MathF.Max( _manager.VoxelSize, (_manager.ChunkRadius * _manager.ChunkSize - 4) * _manager.VoxelSize * 0.35f );
		return new Vector3( (column / (float)System.Math.Max( 1, width - 1 ) * 2.0f - 1.0f) * extent, (row / (float)maximumRow * 2.0f - 1.0f) * extent, 0.0f );
	}

	private Vector3 GetWorldWidePlacementPosition( int index )
	{
		var progress = index / (float)System.Math.Max( 1, SustainedEditCount - 1 );
		var angle = progress * System.MathF.PI * 12.0f;
		var extent = System.MathF.Max( _manager.VoxelSize, (_manager.ChunkRadius * _manager.ChunkSize - 4) * _manager.VoxelSize * 0.35f );
		var radius = extent * System.MathF.Sqrt( progress );
		return new Vector3( System.MathF.Cos( angle ) * radius, System.MathF.Sin( angle ) * radius, 0.0f );
	}

	private void BuildVariedEdits()
	{
		_variedEdits.Clear();
		var voxel = _manager?.VoxelSize ?? 32.0f;
		var seam = (_manager?.ChunkSize ?? 32) * voxel;
		_variedEdits.Add( new( Vector3.Zero, voxel * 1.5f, voxel * 0.8f ) );
		_variedEdits.Add( new( new Vector3( seam, 0, 0 ), voxel * 2.0f, voxel ) );
		_variedEdits.Add( new( new Vector3( 0, seam, 0 ), voxel * 2.5f, -voxel ) );
		_variedEdits.Add( new( new Vector3( seam, seam, 0 ), voxel * 3.0f, voxel * 1.5f ) );
		_variedEdits.Add( new( new Vector3( -seam, seam, 0 ), voxel * 4.0f, -voxel * 1.5f ) );
		_variedEdits.Add( new( new Vector3( seam * 0.5f, -seam * 0.5f, 0 ), voxel * 2.25f, voxel ) );
		_variedEdits.Add( new( new Vector3( -seam * 0.5f, -seam * 0.5f, 0 ), voxel * 3.5f, -voxel ) );
		_variedEdits.Add( new( new Vector3( voxel * 4.0f, voxel * 7.0f, voxel ), voxel * 2.0f, voxel * 0.5f ) );
	}

	private void EnsurePlayerFixture()
	{
		if ( Scene.GetAllComponents<PlayerController>().Any() ) return;
		_playerFixture = new GameObject( true, "Voxel Benchmark Player" );
		_playerFixture.WorldPosition = Vector3.Zero;
		var body = _playerFixture.AddComponent<Rigidbody>();
		body.Gravity = false;
		body.MotionEnabled = true;
		_playerFixture.AddComponent<PlayerController>();
	}

	private void DestroyPlayerFixture()
	{
		if ( _playerFixture is null ) return;
		_playerFixture.Destroy();
		_playerFixture = null;
	}

	private void CaptureSettings()
	{
		if ( _worldSettingsCaptured ) return;
		_worldSettingsCaptured = true;
		_originalCaptureCallCounts = _manager.CaptureCallCounts;
	}

	private void RestoreSettings()
	{
		if ( !_worldSettingsCaptured || _manager is null ) return;
		_manager.SetBenchmarkPlayerProtection( false );
		_manager.CaptureCallCounts = _originalCaptureCallCounts;
		_worldSettingsCaptured = false;
	}

	private double ElapsedSeconds() => _phaseStartTimestamp == 0 ? 0.0 : System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp ).TotalSeconds;

	private void WriteReports()
	{
		var complete = IsSuiteComplete;
		var required = string.Join( ",", RequiredScenarios.Select( scenario => $"\"{Json( scenario.Name )}\"" ) );
		var executed = string.Join( ",", _results.Select( result => $"\"{Json( result.Name )}\"" ) );
		var scenarios = string.Join( ",\n", _results.Select( result => "    " + SerializeScenarioJson( result ) ) );
		var latest = $"{{\n  \"run_id\":\"{Json( _runId )}\",\n  \"suite_version\":{SuiteVersion},\n  \"benchmark_mode\":\"GpuOnly\",\n  \"world_scene\":\"{BenchmarkWorldScene}\",\n  \"suite_complete\":{complete.ToString().ToLowerInvariant()},\n  \"failure\":\"{Json( _failureReason )}\",\n  \"revision\":\"{Json( Revision )}\",\n  \"working_tree_dirty\":{WorkingTreeDirty.ToString().ToLowerInvariant()},\n  \"required_scenarios\":[{required}],\n  \"executed_scenarios\":[{executed}],\n  \"scenarios\":[\n{scenarios}\n  ]\n}}";
		FileSystem.Data.WriteAllText( LatestJsonPath, latest );
		FileSystem.Data.WriteAllText( LatestMarkdownPath, BuildMarkdown( complete ) );
		AppendHistoryJsonLines();
		AppendHistoryCsv();
		FileSystem.Data.WriteAllText( DashboardPath, BuildDashboardHtml() );
		LastReportPath = LatestJsonPath;
	}

	private void AppendHistoryJsonLines()
	{
		var existing = FileSystem.Data.FileExists( HistoryJsonLinesPath ) ? FileSystem.Data.ReadAllText( HistoryJsonLinesPath ).TrimEnd() : string.Empty;
		var current = string.Join( "\n", _results.Select( SerializeScenarioJson ) );
		FileSystem.Data.WriteAllText( HistoryJsonLinesPath, string.IsNullOrWhiteSpace( existing ) ? current + "\n" : existing + "\n" + current + "\n" );
	}

	private void AppendHistoryCsv()
	{
		const string header = "run_id,suite_version,suite_complete,benchmark_mode,timestamp_utc,revision,scenario,passed,elapsed_ms,frames,avg_fps,frame_p95_ms,frame_max_ms,gpu_p95_ms,edits,changed_chunk_events,stream_chunks_completed,stream_batches_completed,calls_player_traversal_updates,failed_visual_chunks,player_safety_active";
		var existing = FileSystem.Data.FileExists( HistoryCsvPath ) ? FileSystem.Data.ReadAllText( HistoryCsvPath ) : string.Empty;
		var builder = new System.Text.StringBuilder();
		if ( existing.StartsWith( header, System.StringComparison.Ordinal ) ) builder.Append( existing.TrimEnd() ).AppendLine();
		else builder.AppendLine( header );
		foreach ( var result in _results ) builder.AppendLine( ToCsvRow( result ) );
		FileSystem.Data.WriteAllText( HistoryCsvPath, builder.ToString() );
	}

	private string BuildMarkdown( bool complete )
	{
		var builder = new System.Text.StringBuilder();
		builder.AppendLine( "# Voxel Terrain Benchmark" ).AppendLine();
		builder.AppendLine( $"- Run: `{_runId}`" );
		builder.AppendLine( $"- Mode: GPU-only on `{BenchmarkWorldScene}`" );
		builder.AppendLine( $"- Revision: `{Revision}` (dirty={WorkingTreeDirty})" );
		builder.AppendLine( $"- completeness: **{(complete ? "COMPLETE" : "FAILED/INCOMPLETE")}** ({_results.Count}/{RequiredScenarios.Length})" ).AppendLine();
		builder.AppendLine( "| Scenario | Result | Avg FPS | Frame p95 | Edits | Traversal updates |" ).AppendLine( "|---|---:|---:|---:|---:|---:|" );
		foreach ( var result in _results ) builder.AppendLine( $"| `{result.Name}` | {(result.Passed ? "PASS" : "FAIL")} | {result.AverageFramesPerSecond:F1} | {result.FrameP95Milliseconds:F2} ms | {result.EditCount} | {result.TraversalUpdates} |" );
		builder.AppendLine().AppendLine( "The suite intentionally measures the GPU terrain path only. Player movement still uses normal collision so the traversal scenarios represent gameplay." );
		builder.AppendLine( "CPU-only visual backend tests and one-off subsystem proofs were removed from the authoritative suite." );
		return builder.ToString();
	}

	private string BuildDashboardHtml()
	{
		var history = FileSystem.Data.FileExists( HistoryJsonLinesPath ) ? FileSystem.Data.ReadAllText( HistoryJsonLinesPath ) : string.Empty;
		return VoxelBenchmarkDashboard.Build( history );
	}

	private string SerializeScenarioJson( ScenarioResult result )
	{
		var calls = result.CallCounts.Enumerate().Select( entry => $"\"calls_{Json( entry.Name.Replace( '.', '_' ) )}\":{entry.Count}" );
		return "{" +
			$"\"run_id\":\"{Json( _runId )}\",\"suite_version\":{SuiteVersion},\"suite_complete\":{IsSuiteComplete.ToString().ToLowerInvariant()},\"benchmark_mode\":\"GpuOnly\",\"timestamp_utc\":\"{System.DateTime.UtcNow:O}\",\"revision\":\"{Json( Revision )}\",\"scenario\":\"{Json( result.Name )}\",\"description\":\"{Json( result.Description )}\",\"passed\":{result.Passed.ToString().ToLowerInvariant()}," +
			$"\"elapsed_ms\":{Number( result.ElapsedMilliseconds )},\"frames\":{result.FrameCount},\"avg_fps\":{Number( result.AverageFramesPerSecond )},\"one_percent_low_fps\":{Number( result.OnePercentLowFramesPerSecond )},\"frame_p95_ms\":{Number( result.FrameP95Milliseconds )},\"frame_max_ms\":{Number( result.FrameMaximumMilliseconds )},\"gpu_p95_ms\":{Number( result.GpuP95Milliseconds )},\"edits\":{result.EditCount},\"changed_chunk_events\":{result.ChangedChunkEvents},\"visual_coherence_violation_frames\":{result.VisualCoherenceViolationFrames},\"exceptions\":{result.Exceptions}," +
			$"\"loaded_chunks\":{result.Diagnostics.LoadedChunks},\"failed_visual_chunks\":{result.Diagnostics.FailedVisualChunks},\"pending_visual_builds\":{result.Diagnostics.PendingVisualBuilds},\"pending_collision_builds\":{result.Diagnostics.PendingCollisionBuilds},\"player_safety_active\":{result.Diagnostics.PlayerSafetyActive.ToString().ToLowerInvariant()},\"stream_chunks_completed\":{result.Streaming.CompletedChunks},\"stream_batches_completed\":{result.Streaming.CompletedBatches}," +
			$"\"gpu_terrain_available\":true,\"gpu_terrain_requested_blocks\":{result.GpuTerrain.RequestedBlocks},\"gpu_terrain_resident_blocks\":{result.GpuTerrain.ResidentBlocks},\"gpu_terrain_pending_count_batches\":{result.GpuTerrain.PendingCountBatches},\"gpu_terrain_pending_emit_batches\":{result.GpuTerrain.PendingEmitBatches},\"gpu_terrain_failure\":\"{Json( result.GpuTerrain.Failure )}\"," +
			string.Join( ",", calls ) + "}";
	}

	private string ToCsvRow( ScenarioResult result )
	{
		var traversalCalls = result.TraversalUpdates;
		return string.Join( ",", Csv( _runId ), SuiteVersion, IsSuiteComplete, "GpuOnly", Csv( System.DateTime.UtcNow.ToString( "O" ) ), Csv( Revision ), Csv( result.Name ), result.Passed, Number( result.ElapsedMilliseconds ), result.FrameCount, Number( result.AverageFramesPerSecond ), Number( result.FrameP95Milliseconds ), Number( result.FrameMaximumMilliseconds ), Number( result.GpuP95Milliseconds ), result.EditCount, result.ChangedChunkEvents, result.Streaming.CompletedChunks, result.Streaming.CompletedBatches, traversalCalls, result.Diagnostics.FailedVisualChunks, result.Diagnostics.PlayerSafetyActive );
	}

	private static string Number( double value ) => value.ToString( "0.###", CultureInfo.InvariantCulture );
	private static string Csv( string value ) => "\"" + (value ?? string.Empty).Replace( "\"", "\"\"" ) + "\"";
	private static string Json( string value ) => (value ?? string.Empty).Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" ).Replace( "\r", "\\r" ).Replace( "\n", "\\n" );

	private enum BenchmarkPhase { Idle, Warmup, WaitGeneration, Traverse, WaitAfterTraversal, Edit, WaitAfterEdit, Complete, Failed }
	private enum ScenarioKind { Generation, Traversal, PostEditTraversal, VariedEdits, BulkEdit, SustainedDig, SustainedPlace }
	private readonly record struct ScenarioDefinition( string Name, string Description, ScenarioKind Kind );
	private readonly record struct EditCommand( Vector3 LocalPosition, float Radius, float Displacement );

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
		public double AverageFramesPerSecond { get; init; }
		public double OnePercentLowFramesPerSecond { get; init; }
		public double FrameP95Milliseconds { get; init; }
		public double FrameMaximumMilliseconds { get; init; }
		public double GpuP95Milliseconds { get; init; }
		public int Exceptions { get; init; }
		public int TraversalUpdates { get; init; }
		public VoxelTerrainDiagnostics Diagnostics { get; init; }
		public VoxelChunkStreamingDiagnostics Streaming { get; init; }
		public VoxelGpuTerrainDiagnostics GpuTerrain { get; init; }
		public VoxelCallCountSnapshot CallCounts { get; init; }
	}

	private sealed class FrameSampler
	{
		private readonly List<double> _frames = new( 4096 );
		private readonly List<double> _gpuFrames = new( 4096 );
		private readonly VoxelTerrainDiagnostics _startingDiagnostics;
		private readonly VoxelCallCountSnapshot _startingCallCounts;
		private int _editCount;
		private int _changedChunkEvents;
		private int _visualCoherenceViolationFrames;
		private long _allocatedBytes;
		private long _gcPauseTicks;
		private int _exceptions;
		private bool _failed;

		public string Name { get; }
		public string Description { get; }
		public long StartingChunkTimingSequence { get; }
		public long StartingBatchTimingSequence { get; }
		public VoxelCallCountSnapshot StartingCallCounts => _startingCallCounts;

		public FrameSampler( string name, string description, VoxelTerrainDiagnostics diagnostics, VoxelCallCountSnapshot callCounts, long chunkSequence, long batchSequence )
		{
			Name = name;
			Description = description;
			_startingDiagnostics = diagnostics;
			_startingCallCounts = callCounts;
			StartingChunkTimingSequence = chunkSequence;
			StartingBatchTimingSequence = batchSequence;
		}

		public void Sample()
		{
			_frames.Add( Sandbox.Diagnostics.PerformanceStats.FrameTime * 1000.0 );
			if ( Sandbox.Diagnostics.PerformanceStats.GpuFrametime >= 0.0f ) _gpuFrames.Add( Sandbox.Diagnostics.PerformanceStats.GpuFrametime );
			_allocatedBytes += Sandbox.Diagnostics.PerformanceStats.BytesAllocated;
			_gcPauseTicks += Sandbox.Diagnostics.PerformanceStats.GcPause;
			_exceptions += (int)Sandbox.Diagnostics.PerformanceStats.Exceptions;
		}

		public void RecordEdit( int changedChunks, double elapsedMilliseconds )
		{
			_editCount++;
			_changedChunkEvents += changedChunks;
		}

		public void RecordVisualCoherenceViolation() => _visualCoherenceViolationFrames++;
		public void RecordFailure() => _failed = true;

		public ScenarioResult Complete( VoxelTerrainDiagnostics diagnostics, VoxelGpuTerrainDiagnostics gpuTerrain, VoxelCallCountSnapshot callCounts, VoxelChunkStreamingDiagnostics streaming, double elapsedMilliseconds, bool requestedPassed )
		{
			_frames.Sort();
			_gpuFrames.Sort();
			var frameAverage = Average( _frames );
			var frameP95 = Percentile( _frames, 0.95 );
			var calls = callCounts.Subtract( _startingCallCounts );
			var traversalUpdates = calls.Enumerate().FirstOrDefault( entry => entry.Name == "player.traversal_updates" ).Count;
			return new ScenarioResult
			{
				Name = Name,
				Description = Description,
				Passed = requestedPassed && !_failed && _exceptions == 0 && _visualCoherenceViolationFrames == 0 && diagnostics.FailedVisualChunks == 0 && diagnostics.PendingVisualBuilds == 0 && diagnostics.PendingCollisionBuilds == 0 && !diagnostics.PlayerSafetyActive,
				EditCount = _editCount,
				ChangedChunkEvents = _changedChunkEvents,
				VisualCoherenceViolationFrames = _visualCoherenceViolationFrames,
				ElapsedMilliseconds = elapsedMilliseconds,
				FrameCount = _frames.Count,
				AverageFramesPerSecond = frameAverage > 0.0 ? 1000.0 / frameAverage : 0.0,
				OnePercentLowFramesPerSecond = frameP95 > 0.0 ? 1000.0 / frameP95 : 0.0,
				FrameP95Milliseconds = frameP95,
				FrameMaximumMilliseconds = _frames.Count == 0 ? 0.0 : _frames[^1],
				GpuP95Milliseconds = Percentile( _gpuFrames, 0.95 ),
				Exceptions = _exceptions,
				TraversalUpdates = (int)traversalUpdates,
				Diagnostics = diagnostics with
				{
					VisualBatchBuiltChunks = (int)System.Math.Max( 0, diagnostics.TotalVisualBuildsCompleted - _startingDiagnostics.TotalVisualBuildsCompleted ),
					VisualBatchElapsedMilliseconds = System.Math.Max( 0, diagnostics.TotalVisualBatchElapsedMilliseconds - _startingDiagnostics.TotalVisualBatchElapsedMilliseconds )
				},
				Streaming = streaming,
				GpuTerrain = gpuTerrain,
				CallCounts = calls
			};
		}

		private static double Average( List<double> values ) => values.Count == 0 ? 0.0 : values.Average();
		private static double Percentile( List<double> values, double percentile ) => values.Count == 0 ? 0.0 : values[(int)System.Math.Clamp( System.Math.Ceiling( values.Count * percentile ) - 1, 0, values.Count - 1 )];
	}
}
