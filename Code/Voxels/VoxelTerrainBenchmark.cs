public sealed class VoxelTerrainBenchmark : Component
{
	private const string ReportDirectory = "voxel-terrain-benchmarks";
	private const string HistoryCsvPath = ReportDirectory + "/history.csv";
	private const string HistoryJsonLinesPath = ReportDirectory + "/history.jsonl";
	private const string LatestMarkdownPath = ReportDirectory + "/latest-report.md";
	private const string LatestJsonPath = ReportDirectory + "/latest-report.json";
	private const string DashboardPath = ReportDirectory + "/dashboard.html";
	private const int SuiteVersion = 2;
	private static readonly string[] RequiredScenarios =
	{
		"cold_generation",
		"varied_edits",
		"bulk_edit",
		"sustained_world_sweep_and_depth_dig_20hz",
		"sustained_world_spiral_place_20hz"
	};
	private static readonly ComparisonMetric[] ComparisonMetrics =
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
		new( "upload_ms", true )
	};

	private readonly List<ScenarioResult> _results = new();
	private readonly List<EditCommand> _variedEdits = new();
	private readonly Dictionary<string, ScenarioComparison> _comparisons = new();
	private VoxelManager _manager;
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
	private bool _isReproductionRun;
	private string _reproductionOfRunId;
	private Dictionary<string, PreviousScenarioMeasurement> _reproductionBaselines;
	private Dictionary<string, HashSet<string>> _reproductionTargets;

	[Property, Group( "Run" )]
	public bool RunOnStart { get; set; } = true;

	[Property, Group( "Run" )]
	public string Revision { get; set; } = "dfc6358+working-tree";

	[Property, Group( "Run" ), Range( 0, 240 )]
	public int WarmupFrames { get; set; } = 30;

	[Property, Group( "Run" ), Range( 10.0f, 180.0f )]
	public float ScenarioTimeoutSeconds { get; set; } = 90.0f;

	[Property, Group( "Run" ), Range( 5.0f, 100.0f )]
	public float MajorOutlierThresholdPercent { get; set; } = 20.0f;

	[Property, Group( "Sustained Editing" ), Range( 10, 500 )]
	public int SustainedEditCount { get; set; } = 80;

	[Property, Group( "Sustained Editing" ), Range( 0.01f, 0.25f )]
	public float SustainedEditIntervalSeconds { get; set; } = 0.05f;

	public string LastReportPath { get; private set; }
	public bool IsRunning => _phase is not BenchmarkPhase.Idle and not BenchmarkPhase.Complete and not BenchmarkPhase.Failed;

	protected override void OnValidate()
	{
		WarmupFrames = System.Math.Clamp( WarmupFrames, 0, 240 );
		ScenarioTimeoutSeconds = System.Math.Clamp( ScenarioTimeoutSeconds, 10.0f, 180.0f );
		MajorOutlierThresholdPercent = System.Math.Clamp( MajorOutlierThresholdPercent, 5.0f, 100.0f );
		SustainedEditCount = System.Math.Clamp( SustainedEditCount, 10, 500 );
		SustainedEditIntervalSeconds = System.Math.Clamp( SustainedEditIntervalSeconds, 0.01f, 0.25f );
	}

	protected override void OnStart()
	{
		_manager = GameObject.Components.Get<VoxelManager>();
		if ( _manager is null )
		{
			Log.Error( "Voxel terrain benchmark requires a VoxelManager on the same GameObject." );
			_phase = BenchmarkPhase.Failed;
			return;
		}

		EnsurePlayerSafetyFixture();
		_originalCaptureCallCounts = _manager.CaptureCallCounts;
		_callCountSettingCaptured = true;
		BuildVariedEditFixture();
		if ( RunOnStart )
		{
			BeginRun( false );
		}
	}

	protected override void OnDisabled()
	{
		RestoreCallCountSetting();
		DestroyPlayerSafetyFixture();
	}

	protected override void OnDestroy()
	{
		RestoreCallCountSetting();
		DestroyPlayerSafetyFixture();
	}

	private void EnsurePlayerSafetyFixture()
	{
		if ( Scene.GetAllComponents<PlayerController>().Any() ) return;
		_playerSafetyFixture = new GameObject( true, "Voxel Benchmark Player Safety Fixture" );
		_playerSafetyFixture.WorldPosition = Vector3.Zero;
		var body = _playerSafetyFixture.AddComponent<Rigidbody>();
		body.Gravity = true;
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
		if ( _runRequested && _phase == BenchmarkPhase.Idle )
		{
			BeginRun( true );
		}

		_sampler?.Sample();
		if ( !IsRunning )
		{
			return;
		}
		if ( PhaseTimedOut() )
		{
			FailRun( $"phase {_phase} exceeded {ScenarioTimeoutSeconds:F0}s" );
			return;
		}

		switch ( _phase )
		{
			case BenchmarkPhase.WaitInitialGeneration:
				if ( _manager.IsTerrainSettled ) CompleteScenarioAndWarmup( BenchmarkPhase.StartVariedEdits );
				break;
			case BenchmarkPhase.Warmup:
				if ( --_warmupFramesRemaining <= 0 ) AdvanceAfterWarmup();
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
		_runRequested = true;
		_phase = BenchmarkPhase.Idle;
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
		_results.Clear();
		_comparisons.Clear();
		_runId = System.DateTime.UtcNow.ToString( "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture );
		BeginScenario( "cold_generation", "Authoritative SDF generation, Transvoxel visual meshing, GPU upload, and nearby collision" );
		_phase = BenchmarkPhase.WaitInitialGeneration;
		if ( regenerate && !_manager.IsWorldGenerationPending )
		{
			_manager.GenerateWorld();
		}
		Log.Info( $"Voxel terrain benchmark {_runId} started at revision {Revision}." );
	}

	private void BeginScenario( string name, string description )
	{
		_sampler = new FrameSampler( name, description, _manager.CaptureTerrainDiagnostics(), _manager.CaptureCallCountSnapshot() );
		_phaseStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
	}

	private void CompleteScenario()
	{
		if ( _sampler is null ) return;
		var result = _sampler.Complete( _manager.CaptureTerrainDiagnostics(), _manager.CaptureCallCountSnapshot(), System.Diagnostics.Stopwatch.GetElapsedTime( _phaseStartTimestamp ).TotalMilliseconds );
		_results.Add( result );
		var hottest = result.CallCounts.Enumerate().OrderByDescending( entry => entry.Count ).First();
		Log.Info( $"Voxel terrain benchmark scenario {result.Name}: result={(result.Passed ? "PASS" : "FAIL")}, elapsed={result.ElapsedMilliseconds:F2}ms, FPS(avg/1%-low/0.1%-low)={result.AverageFramesPerSecond:F1}/{result.OnePercentLowFramesPerSecond:F1}/{result.PointOnePercentLowFramesPerSecond:F1}, frameMs(p95/max)={result.FrameP95Milliseconds:F2}/{result.FrameMaximumMilliseconds:F2}, stutters={result.StutterEvents:N0}, editLatency(p95/settle)={result.EditCallP95Milliseconds:F3}/{result.PostEditSettleMilliseconds:F2}ms, GPU-p95={result.GpuP95Milliseconds:F2}ms, hottest={hottest.Name}:{hottest.Count:N0}, allocated={FormatBytes( result.AllocatedBytes )}." );
		_sampler = null;
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

		for ( var index = lines.Length - 1; index >= 0 && measurements.Count < RequiredScenarios.Length; index-- )
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
				if ( string.IsNullOrWhiteSpace( scenario ) || measurements.ContainsKey( scenario ) || !RequiredScenarios.Contains( scenario ) ) continue;
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
		_ => 0.0
	};

	private ScenarioComparison GetComparison( ScenarioResult result ) =>
		_comparisons.TryGetValue( result.Name, out var comparison ) ? comparison : new ScenarioComparison();

	private string ConfigurationId => $"{_manager.ChunkRadius}:{_manager.ChunkSize}:{Number( _manager.VoxelSize )}:{_manager.CpuChunkBuildConcurrency}:{_manager.CollisionResolutionDivisor}:{SustainedEditCount}:{Number( SustainedEditIntervalSeconds )}";

	private void FailRun( string reason )
	{
		Log.Error( $"Voxel terrain benchmark {_runId} failed: {reason}." );
		if ( _sampler is not null )
		{
			CompleteScenario();
		}
		WriteReports( reason );
		_phase = BenchmarkPhase.Failed;
		RestoreCallCountSetting();
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
			return failure is null && _results.All( result => result.Passed );
		}
		catch ( System.Exception exception )
		{
			Log.Error( $"Voxel terrain benchmark could not write reports: {exception.Message}" );
			RestoreCallCountSetting();
			return false;
		}
	}

	private string GetSuiteCompletenessFailure()
	{
		if ( _results.Count != RequiredScenarios.Length )
		{
			return $"incomplete suite: expected {RequiredScenarios.Length} scenarios, recorded {_results.Count}";
		}

		for ( var index = 0; index < RequiredScenarios.Length; index++ )
		{
			if ( _results[index].Name != RequiredScenarios[index] )
			{
				return $"invalid suite order at {index}: expected {RequiredScenarios[index]}, recorded {_results[index].Name}";
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

	private void AppendHistoryCsv()
	{
		var comparisonHeader = string.Join( ",", ComparisonMetrics.Select( metric => $"change_{metric.Name}_pct" ) );
		var header = "run_id,suite_version,suite_complete,timestamp_utc,revision,engine_version,cpu,gpu,scenario,description,passed,edits,changed_chunk_events,elapsed_ms,frames,avg_fps,one_percent_low_fps,point_one_percent_low_fps,min_fps,frame_avg_ms,frame_stddev_ms,frame_p50_ms,frame_p95_ms,frame_p99_ms,frame_p999_ms,frame_max_ms,frames_over_16ms,frames_over_33ms,frames_over_50ms,frames_over_100ms,stutter_events,longest_stutter_frames,gpu_avg_ms,gpu_p95_ms,gpu_max_ms,edit_call_avg_ms,edit_call_p95_ms,edit_call_max_ms,post_edit_settle_ms,edit_throughput_per_second,update_avg_ms,update_max_ms,render_avg_ms,render_max_ms,physics_avg_ms,physics_max_ms,network_avg_ms,network_max_ms,network_out_bytes_per_second,network_in_bytes_per_second,network_ping_ms,maximum_connections,messages_sent,messages_received,allocated_bytes,gc_pause_ms,gen0_gc,gen1_gc,gen2_gc,exceptions,peak_memory_bytes,texture_pool_peak_bytes,texture_pool_non_evictable_peak_bytes,pending_streaming_requests_max,draw_calls_avg,triangles_rendered_avg,objects_rendered_avg,material_changes_avg,loaded_chunks,authoritative_sdf_bytes,uniform_sdf_chunks,visual_chunks,failed_visual_chunks,visual_batch_built_chunks,visual_vertices,visual_triangles,colliders,collision_triangles,player_safety_active,generation_ms,visual_batch_ms,snapshot_wait_ms,snapshot_copy_ms,worker_mesh_ms,upload_ms,configuration_id,comparison_baseline_run_id,comparison_has_baseline,change_max_abs_pct,outlier_detected,outlier_metrics,reproduction_of_run_id,reproduction_status," + comparisonHeader + "," +
			string.Join( ",", default(VoxelCallCountSnapshot).Enumerate().Select( entry => CallCountKey( entry.Name ) ) );
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
			builder.Append( FileSystem.Data.ReadAllText( HistoryJsonLinesPath ).TrimEnd() );
			builder.AppendLine();
		}
		foreach ( var result in _results ) builder.AppendLine( SerializeScenarioJson( result ) );
		FileSystem.Data.WriteAllText( HistoryJsonLinesPath, builder.ToString() );
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
			$"  \"major_outlier_threshold_percent\":{Number( MajorOutlierThresholdPercent )},\n" +
			$"  \"automatic_reproduction\":{_isReproductionRun.ToString().ToLowerInvariant()},\n" +
			$"  \"reproduction_of_run_id\":{(_reproductionOfRunId is null ? "null" : "\"" + Json( _reproductionOfRunId ) + "\"")},\n" +
			$"  \"required_scenarios\":[{string.Join( ",", RequiredScenarios.Select( name => "\"" + Json( name ) + "\"" ) )}],\n" +
			$"  \"executed_scenarios\":[{string.Join( ",", _results.Select( result => "\"" + Json( result.Name ) + "\"" ) )}],\n" +
			$"  \"timestamp_utc\":\"{System.DateTime.UtcNow:O}\",\n" +
			$"  \"revision\":\"{Json( Revision )}\",\n" +
			$"  \"engine_version\":\"{Json( Application.Version )}\",\n" +
			$"  \"resolution\":\"{Screen.Width:F0}x{Screen.Height:F0}\",\n" +
			$"  \"vsync\":{vsync},\n" +
			$"  \"frame_cap\":{frameCap},\n" +
			"  \"coverage\":{\"activation\":\"optional\",\"active_run_instrumentation\":\"mandatory\",\"terrain_streaming\":\"not_implemented\",\"terrain_persistence\":\"not_implemented\",\"terrain_replication\":\"not_implemented\",\"engine_network_observation\":\"measured\",\"memory_and_render_cache\":\"measured\",\"call_frequency\":\"measured\"},\n" +
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
		builder.AppendLine( $"- Suite: `v{SuiteVersion}`, completeness: **{(IsSuiteComplete ? "COMPLETE" : "INCOMPLETE")}** (`{_results.Count}/{RequiredScenarios.Length}` scenarios)" );
		builder.AppendLine( $"- Revision: `{Revision}`" );
		builder.AppendLine( $"- Engine: `{Application.Version}` ({Application.VersionDate:O})" );
		builder.AppendLine( $"- CPU: `{Sandbox.Engine.SystemInfo.ProcessorName}` ({Sandbox.Engine.SystemInfo.ProcessorCount:F0} logical processors)" );
		builder.AppendLine( $"- GPU: `{Sandbox.Engine.SystemInfo.Gpu}` ({FormatBytes( (long)Sandbox.Engine.SystemInfo.GpuMemory )})" );
		builder.AppendLine( $"- Display: `{Screen.Width:F0}x{Screen.Height:F0}`, VSync `{vsync}`, frame cap `{frameCap}`" );
		builder.AppendLine( $"- Configuration: `{_manager.ChunkRadius}` radius, `{_manager.ConfiguredChunkCount}` chunks, `{_manager.ChunkSize}^3` cells, `{_manager.CpuChunkBuildConcurrency}` visual workers, collision `{_manager.CollisionResolutionDivisor}:1`" );
		builder.AppendLine( $"- Outlier rule: absolute change `>= {MajorOutlierThresholdPercent:F1}%` on stable comparison metrics triggers one complete-suite reproduction run" );
		if ( _isReproductionRun ) builder.AppendLine( $"- Reproduction of run: `{_reproductionOfRunId}`" );
		builder.AppendLine( $"- Worst frame p95/max: `{worstP95:F2} / {worstMax:F2} ms`" );
		if ( failure is not null ) builder.AppendLine( $"- Failure: `{failure}`" );
		builder.AppendLine();
		builder.AppendLine( "## Scenario overview" );
		builder.AppendLine();
		builder.AppendLine( "| Scenario | Result | Average FPS | 1% low | 0.1% low | Frame p95 / max | Stutters | Edits | Rebuilt chunks |" );
		builder.AppendLine( "|---|---:|---:|---:|---:|---:|---:|---:|---:|" );
		foreach ( var result in _results )
		{
			builder.AppendLine( $"| {result.Name} | {(result.Passed ? "PASS" : "FAIL")} | {result.AverageFramesPerSecond:F1} | {result.OnePercentLowFramesPerSecond:F1} | {result.PointOnePercentLowFramesPerSecond:F1} | {result.FrameP95Milliseconds:F2} / {result.FrameMaximumMilliseconds:F2} ms | {result.StutterEvents:N0} | {result.EditCount:N0} | {result.Diagnostics.VisualBatchBuiltChunks:N0} |" );
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
		builder.AppendLine( "| Scenario | Generation | Visual batch | Snapshot wait | Snapshot copy | Worker mesh | Main upload | Collision triangles | Safety released |" );
		builder.AppendLine( "|---|---:|---:|---:|---:|---:|---:|---:|---:|" );
		foreach ( var result in _results )
		{
			var diagnostics = result.Diagnostics;
			builder.AppendLine( $"| {result.Name} | {diagnostics.GenerationElapsedMilliseconds:F2} ms | {diagnostics.VisualBatchElapsedMilliseconds:F2} ms | {diagnostics.SnapshotWaitMilliseconds:F2} ms | {diagnostics.SnapshotCopyMilliseconds:F2} ms | {diagnostics.WorkerMeshMilliseconds:F2} ms | {diagnostics.MainThreadUploadMilliseconds:F2} ms | {diagnostics.CollisionTriangles:N0} | {(!diagnostics.PlayerSafetyActive ? "yes" : "no")} |" );
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
		builder.AppendLine( "| Terrain streaming/player traversal | Not implemented; reserved for the streaming slice |" );
		builder.AppendLine( "| Terrain persistence and disk/cache I/O | Not implemented; no synthetic substitute reported |" );
		builder.AppendLine( "| Terrain replication bandwidth and convergence | Not implemented; requires the multiplayer terrain slice |" );
		builder.AppendLine();
		builder.AppendLine( "## History and visualization" );
		builder.AppendLine();
		builder.AppendLine( $"Open `{FileSystem.Data.GetFullPath( DashboardPath )}` for interactive historical charts. Raw history is retained in `history.csv` and `history.jsonl`; every row includes its revision and run ID." );
		builder.AppendLine();
		builder.AppendLine( "Player-driven streaming, terrain persistence/cache I/O, and terrain replication are intentionally marked unavailable until their authoritative systems exist. The scenario-based history can add those journeys without breaking prior JSONL data." );
		return builder.ToString();
	}

	private string BuildDashboardHtml()
	{
		var history = FileSystem.Data.FileExists( HistoryJsonLinesPath ) ? FileSystem.Data.ReadAllText( HistoryJsonLinesPath ) : string.Empty;
		var rows = string.Join( ",", history.Split( '\n', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries ) );
		var callCountOptions = string.Join( string.Empty, default(VoxelCallCountSnapshot).Enumerate().Select( entry => $"<option value={CallCountKey( entry.Name )}>{entry.Name}</option>" ) );
		var comparisonOptions = string.Join( string.Empty, ComparisonMetrics.Select( metric => $"<option value=change_{metric.Name}_pct>{metric.Name}</option>" ) );
		return "<!doctype html><html><head><meta charset=\"utf-8\"><title>Voxel Terrain Benchmark</title><style>" +
			"body{font:14px system-ui;background:#10151d;color:#e8eef7;margin:0;padding:28px}h1{margin-top:0}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}.card,section{background:#182231;border:1px solid #2a3a50;border-radius:9px;padding:16px;margin:14px 0}.value{font-size:24px;font-weight:700;color:#82d8a7}select{background:#10151d;color:#fff;padding:7px;border:1px solid #526780}canvas{width:100%;height:380px}table{border-collapse:collapse;width:100%;font-size:12px}th,td{padding:7px;border-bottom:1px solid #2a3a50;text-align:right}th:first-child,td:first-child{text-align:left}.muted{color:#9fb0c5}</style></head><body>" +
			"<h1>Voxel Terrain Benchmark</h1><p class=muted>Commit-tagged terrain generation, editing, frame pacing, memory, cache, network, and call-frequency history.</p><div id=cards class=cards></div><section><label>Metric <select id=metric><optgroup label='Change from previous'><option value=change_max_abs_pct selected>Largest metric change (%)</option>" + comparisonOptions + "</optgroup><optgroup label='Frame rate and pacing'><option value=avg_fps>Average FPS</option><option value=one_percent_low_fps>1% low FPS</option><option value=point_one_percent_low_fps>0.1% low FPS</option><option value=min_fps>Minimum FPS</option><option value=frame_p95_ms>Frame p95 (ms)</option><option value=frame_p99_ms>Frame p99 (ms)</option><option value=frame_max_ms>Worst frame (ms)</option><option value=frame_stddev_ms>Frame deviation (ms)</option><option value=stutter_events>Stutter events</option><option value=frames_over_16ms>Frames over 16 ms</option><option value=frames_over_33ms>Frames over 33 ms</option><option value=frames_over_50ms>Frames over 50 ms</option></optgroup><optgroup label='Latency and pipeline'><option value=gpu_p95_ms>GPU p95 (ms)</option><option value=edit_call_p95_ms>Brush-call p95 (ms)</option><option value=edit_call_max_ms>Brush-call max (ms)</option><option value=post_edit_settle_ms>Post-edit settle (ms)</option><option value=edit_throughput_per_second>Edit throughput</option><option value=elapsed_ms>Scenario elapsed (ms)</option><option value=update_max_ms>Update max (ms)</option><option value=render_max_ms>Render max (ms)</option><option value=physics_max_ms>Physics max (ms)</option><option value=worker_mesh_ms>Worker mesh total (ms)</option><option value=visual_batch_ms>Visual batch (ms)</option><option value=snapshot_copy_ms>Snapshot copy (ms)</option><option value=upload_ms>Main upload (ms)</option></optgroup><optgroup label='Memory and cache'><option value=allocated_bytes>Managed allocated bytes</option><option value=gc_pause_ms>GC pause (ms)</option><option value=peak_memory_bytes>Peak process memory</option><option value=authoritative_sdf_bytes>Authoritative SDF bytes</option><option value=texture_pool_peak_bytes>Texture pool peak</option><option value=texture_pool_non_evictable_peak_bytes>Texture pool non-evictable</option><option value=pending_streaming_requests_max>Pending asset streams</option></optgroup><optgroup label='Networking'><option value=network_avg_ms>Network CPU average (ms)</option><option value=network_max_ms>Network CPU max (ms)</option><option value=network_out_bytes_per_second>Network outbound B/s</option><option value=network_in_bytes_per_second>Network inbound B/s</option><option value=network_ping_ms>Network ping (ms)</option><option value=maximum_connections>Connections</option><option value=messages_sent>Messages sent</option><option value=messages_received>Messages received</option></optgroup><optgroup label='Terrain and rendering'><option value=changed_chunk_events>Changed chunk events</option><option value=visual_batch_built_chunks>Rebuilt chunks</option><option value=draw_calls_avg>Average draw calls</option><option value=objects_rendered_avg>Objects rendered</option><option value=triangles_rendered_avg>Triangles rendered</option><option value=visual_vertices>Visual vertices</option><option value=visual_triangles>Visual triangles</option><option value=collision_triangles>Collision triangles</option><option value=player_safety_active>Player safety active at completion</option></optgroup><optgroup label='Call frequency'>" + callCountOptions + "</optgroup></select></label><canvas id=chart width=1400 height=380></canvas></section><section><h2>All measurements</h2><div style=overflow:auto><table id=table></table></div></section><script>" +
			$"const rows=[{rows}];" +
			"const latest=rows.length?rows[rows.length-1]:null;const runs=[...new Set(rows.map(r=>r.run_id))];const lr=latest?rows.filter(r=>r.run_id===latest.run_id):[];document.getElementById('cards').innerHTML=latest?[['Latest run',latest.run_id],['Revision',latest.revision],['Suite','v'+(latest.suite_version??'legacy')],['Complete',latest.suite_complete===true?'YES':'NO'],['Scenarios',lr.length],['Lowest 1% FPS',Math.min(...lr.map(r=>r.one_percent_low_fps||0)).toFixed(1)],['Worst p95',Math.max(...lr.map(r=>r.frame_p95_ms)).toFixed(2)+' ms'],['Worst hitch',Math.max(...lr.map(r=>r.frame_max_ms)).toFixed(2)+' ms'],['Stutters',lr.reduce((n,r)=>n+(r.stutter_events||0),0)]].map(x=>`<div class=card><div class=muted>${x[0]}</div><div class=value>${x[1]}</div></div>`).join(''):'';" +
			"const colors=['#82d8a7','#72a7ff','#ffc46b','#df86ff','#ff7f86','#73d9dc'];function draw(){const key=document.getElementById('metric').value,c=document.getElementById('chart'),x=c.getContext('2d');x.clearRect(0,0,c.width,c.height);const available=rows.filter(r=>r[key]!==undefined&&Number.isFinite(Number(r[key]))),pad=55,w=c.width-pad*2,h=c.height-pad*2,values=available.map(r=>Number(r[key])),min=Math.min(0,...values),max=Math.max(0,...values),range=Math.max(1,max-min),y=v=>pad+(max-v)/range*h;x.strokeStyle='#40516a';x.beginPath();x.moveTo(pad,pad);x.lineTo(pad,pad+h);x.stroke();x.strokeStyle='#647792';x.beginPath();x.moveTo(pad,y(0));x.lineTo(pad+w,y(0));x.stroke();const scenarios=[...new Set(available.map(r=>r.scenario))];scenarios.forEach((s,si)=>{const data=available.filter(r=>r.scenario===s);x.strokeStyle=colors[si%colors.length];x.fillStyle=x.strokeStyle;x.beginPath();data.forEach((r,i)=>{const px=pad+(runs.indexOf(r.run_id)/Math.max(1,runs.length-1))*w,py=y(Number(r[key]));i?x.lineTo(px,py):x.moveTo(px,py);x.fillRect(px-3,py-3,6,6)});x.stroke();x.fillText(s,pad+si*190,20)});x.fillStyle='#9fb0c5';x.fillText(max.toFixed(2),5,pad+5);x.fillText(min.toFixed(2),5,pad+h+5);runs.forEach((r,i)=>{if(i%Math.max(1,Math.ceil(runs.length/8))===0)x.fillText(r,pad+i/Math.max(1,runs.length-1)*w-30,c.height-10)})}document.getElementById('metric').onchange=draw;draw();" +
			"const cols=['run_id','suite_version','suite_complete','revision','scenario','change_max_abs_pct','outlier_detected','outlier_metrics','reproduction_status','avg_fps','one_percent_low_fps','frame_p95_ms','frame_max_ms','stutter_events','edit_call_p95_ms','post_edit_settle_ms','allocated_bytes','network_avg_ms','authoritative_sdf_bytes','visual_triangles','worker_mesh_ms'];document.getElementById('table').innerHTML='<tr>'+cols.map(c=>`<th>${c}</th>`).join('')+'</tr>'+rows.slice().reverse().map(r=>'<tr>'+cols.map(c=>`<td>${r[c]??''}</td>`).join('')+'</tr>').join('');</script></body></html>";
	}

	private string SerializeScenarioJson( ScenarioResult result )
	{
		var d = result.Diagnostics;
		var comparison = GetComparison( result );
		return "{" +
			$"\"run_id\":\"{Json( _runId )}\",\"suite_version\":{SuiteVersion},\"suite_complete\":{IsSuiteComplete.ToString().ToLowerInvariant()},\"timestamp_utc\":\"{System.DateTime.UtcNow:O}\",\"revision\":\"{Json( Revision )}\",\"engine_version\":\"{Json( Application.Version )}\"," +
			$"\"cpu\":\"{Json( Sandbox.Engine.SystemInfo.ProcessorName )}\",\"gpu\":\"{Json( Sandbox.Engine.SystemInfo.Gpu )}\",\"scenario\":\"{Json( result.Name )}\",\"description\":\"{Json( result.Description )}\",\"passed\":{result.Passed.ToString().ToLowerInvariant()}," +
			$"\"edits\":{result.EditCount},\"changed_chunk_events\":{result.ChangedChunkEvents},\"elapsed_ms\":{Number( result.ElapsedMilliseconds )},\"frames\":{result.FrameCount},\"avg_fps\":{Number( result.AverageFramesPerSecond )},\"one_percent_low_fps\":{Number( result.OnePercentLowFramesPerSecond )},\"point_one_percent_low_fps\":{Number( result.PointOnePercentLowFramesPerSecond )},\"min_fps\":{Number( result.MinimumFramesPerSecond )},\"frame_avg_ms\":{Number( result.FrameAverageMilliseconds )},\"frame_stddev_ms\":{Number( result.FrameStandardDeviationMilliseconds )},\"frame_p50_ms\":{Number( result.FrameP50Milliseconds )},\"frame_p95_ms\":{Number( result.FrameP95Milliseconds )},\"frame_p99_ms\":{Number( result.FrameP99Milliseconds )},\"frame_p999_ms\":{Number( result.FrameP999Milliseconds )},\"frame_max_ms\":{Number( result.FrameMaximumMilliseconds )},\"frames_over_16ms\":{result.FramesOver16Milliseconds},\"frames_over_33ms\":{result.FramesOver33Milliseconds},\"frames_over_50ms\":{result.FramesOver50Milliseconds},\"frames_over_100ms\":{result.FramesOver100Milliseconds},\"stutter_events\":{result.StutterEvents},\"longest_stutter_frames\":{result.LongestStutterFrames}," +
			$"\"gpu_avg_ms\":{Number( result.GpuAverageMilliseconds )},\"gpu_p95_ms\":{Number( result.GpuP95Milliseconds )},\"gpu_max_ms\":{Number( result.GpuMaximumMilliseconds )},\"allocated_bytes\":{result.AllocatedBytes},\"gc_pause_ms\":{Number( result.GcPauseMilliseconds )},\"gen0_gc\":{result.Gen0Collections},\"gen1_gc\":{result.Gen1Collections},\"gen2_gc\":{result.Gen2Collections},\"exceptions\":{result.Exceptions},\"peak_memory_bytes\":{result.PeakMemoryBytes}," +
			$"\"edit_call_avg_ms\":{Number( result.EditCallAverageMilliseconds )},\"edit_call_p95_ms\":{Number( result.EditCallP95Milliseconds )},\"edit_call_max_ms\":{Number( result.EditCallMaximumMilliseconds )},\"post_edit_settle_ms\":{Number( result.PostEditSettleMilliseconds )},\"edit_throughput_per_second\":{Number( result.EditThroughputPerSecond )}," +
			$"\"update_avg_ms\":{Number( result.UpdateAverageMilliseconds )},\"update_max_ms\":{Number( result.UpdateMaximumMilliseconds )},\"render_avg_ms\":{Number( result.RenderAverageMilliseconds )},\"render_max_ms\":{Number( result.RenderMaximumMilliseconds )},\"physics_avg_ms\":{Number( result.PhysicsAverageMilliseconds )},\"physics_max_ms\":{Number( result.PhysicsMaximumMilliseconds )},\"network_avg_ms\":{Number( result.NetworkAverageMilliseconds )},\"network_max_ms\":{Number( result.NetworkMaximumMilliseconds )},\"network_out_bytes_per_second\":{Number( result.NetworkOutBytesPerSecondAverage )},\"network_in_bytes_per_second\":{Number( result.NetworkInBytesPerSecondAverage )},\"network_ping_ms\":{Number( result.NetworkPingMillisecondsAverage )},\"maximum_connections\":{result.MaximumConnections},\"messages_sent\":{result.MessagesSent},\"messages_received\":{result.MessagesReceived}," +
			$"\"texture_pool_peak_bytes\":{result.PeakTexturePoolUsedBytes},\"texture_pool_non_evictable_peak_bytes\":{result.PeakTexturePoolNonEvictableBytes},\"pending_streaming_requests_max\":{result.MaximumPendingStreamingRequests},\"draw_calls_avg\":{Number( result.DrawCallsAverage )},\"triangles_rendered_avg\":{Number( result.TrianglesRenderedAverage )},\"objects_rendered_avg\":{Number( result.ObjectsRenderedAverage )},\"material_changes_avg\":{Number( result.MaterialChangesAverage )}," +
			$"\"loaded_chunks\":{d.LoadedChunks},\"authoritative_sdf_bytes\":{d.AuthoritativeSdfStorageBytes},\"uniform_sdf_chunks\":{d.UniformSdfChunks},\"visual_chunks\":{d.ActiveVisualChunks},\"failed_visual_chunks\":{d.FailedVisualChunks},\"visual_batch_built_chunks\":{d.VisualBatchBuiltChunks},\"visual_vertices\":{d.VisualVertices},\"visual_triangles\":{d.VisualTriangles},\"colliders\":{d.ActiveColliders},\"collision_triangles\":{d.CollisionTriangles},\"player_safety_active\":{d.PlayerSafetyActive.ToString().ToLowerInvariant()}," +
			$"\"generation_ms\":{Number( d.GenerationElapsedMilliseconds )},\"visual_batch_ms\":{Number( d.VisualBatchElapsedMilliseconds )},\"snapshot_wait_ms\":{Number( d.SnapshotWaitMilliseconds )},\"snapshot_copy_ms\":{Number( d.SnapshotCopyMilliseconds )},\"worker_mesh_ms\":{Number( d.WorkerMeshMilliseconds )},\"upload_ms\":{Number( d.MainThreadUploadMilliseconds )}," +
			$"\"configuration_id\":\"{Json( ConfigurationId )}\",\"comparison_baseline_run_id\":{(comparison.HasBaseline ? "\"" + Json( comparison.BaselineRunId ) + "\"" : "null")},\"comparison_has_baseline\":{comparison.HasBaseline.ToString().ToLowerInvariant()},\"change_max_abs_pct\":{Number( comparison.MaximumAbsolutePercent )},\"outlier_detected\":{comparison.MajorOutlier.ToString().ToLowerInvariant()},\"outlier_metrics\":\"{Json( comparison.OutlierMetrics )}\",\"reproduction_of_run_id\":{(_reproductionOfRunId is null ? "null" : "\"" + Json( _reproductionOfRunId ) + "\"")},\"reproduction_status\":\"{Json( comparison.ReproductionStatus )}\"," +
			SerializePercentChangesJson( comparison ) + "," +
			SerializeCallCountsJson( result.CallCounts ) +
			"}";
	}

	private string ToCsvRow( ScenarioResult result )
	{
		var d = result.Diagnostics;
		var comparison = GetComparison( result );
		var baseRow = string.Join( ",",
			Csv( _runId ), SuiteVersion, IsSuiteComplete, Csv( System.DateTime.UtcNow.ToString( "O" ) ), Csv( Revision ), Csv( Application.Version ), Csv( Sandbox.Engine.SystemInfo.ProcessorName ), Csv( Sandbox.Engine.SystemInfo.Gpu ), Csv( result.Name ), Csv( result.Description ), result.Passed, result.EditCount, result.ChangedChunkEvents,
			Number( result.ElapsedMilliseconds ), result.FrameCount, Number( result.AverageFramesPerSecond ), Number( result.OnePercentLowFramesPerSecond ), Number( result.PointOnePercentLowFramesPerSecond ), Number( result.MinimumFramesPerSecond ), Number( result.FrameAverageMilliseconds ), Number( result.FrameStandardDeviationMilliseconds ), Number( result.FrameP50Milliseconds ), Number( result.FrameP95Milliseconds ), Number( result.FrameP99Milliseconds ), Number( result.FrameP999Milliseconds ), Number( result.FrameMaximumMilliseconds ), result.FramesOver16Milliseconds, result.FramesOver33Milliseconds, result.FramesOver50Milliseconds, result.FramesOver100Milliseconds, result.StutterEvents, result.LongestStutterFrames,
			Number( result.GpuAverageMilliseconds ), Number( result.GpuP95Milliseconds ), Number( result.GpuMaximumMilliseconds ), Number( result.EditCallAverageMilliseconds ), Number( result.EditCallP95Milliseconds ), Number( result.EditCallMaximumMilliseconds ), Number( result.PostEditSettleMilliseconds ), Number( result.EditThroughputPerSecond ), Number( result.UpdateAverageMilliseconds ), Number( result.UpdateMaximumMilliseconds ), Number( result.RenderAverageMilliseconds ), Number( result.RenderMaximumMilliseconds ), Number( result.PhysicsAverageMilliseconds ), Number( result.PhysicsMaximumMilliseconds ), Number( result.NetworkAverageMilliseconds ), Number( result.NetworkMaximumMilliseconds ), Number( result.NetworkOutBytesPerSecondAverage ), Number( result.NetworkInBytesPerSecondAverage ), Number( result.NetworkPingMillisecondsAverage ), result.MaximumConnections, result.MessagesSent, result.MessagesReceived,
			result.AllocatedBytes, Number( result.GcPauseMilliseconds ), result.Gen0Collections, result.Gen1Collections, result.Gen2Collections, result.Exceptions, result.PeakMemoryBytes, result.PeakTexturePoolUsedBytes, result.PeakTexturePoolNonEvictableBytes, result.MaximumPendingStreamingRequests, Number( result.DrawCallsAverage ), Number( result.TrianglesRenderedAverage ), Number( result.ObjectsRenderedAverage ), Number( result.MaterialChangesAverage ),
			d.LoadedChunks, d.AuthoritativeSdfStorageBytes, d.UniformSdfChunks, d.ActiveVisualChunks, d.FailedVisualChunks, d.VisualBatchBuiltChunks, d.VisualVertices, d.VisualTriangles, d.ActiveColliders, d.CollisionTriangles, d.PlayerSafetyActive, Number( d.GenerationElapsedMilliseconds ), Number( d.VisualBatchElapsedMilliseconds ), Number( d.SnapshotWaitMilliseconds ), Number( d.SnapshotCopyMilliseconds ), Number( d.WorkerMeshMilliseconds ), Number( d.MainThreadUploadMilliseconds ),
			Csv( ConfigurationId ), Csv( comparison.BaselineRunId ), comparison.HasBaseline, Number( comparison.MaximumAbsolutePercent ), comparison.MajorOutlier, Csv( comparison.OutlierMetrics ), Csv( _reproductionOfRunId ), Csv( comparison.ReproductionStatus )
		);
		var changes = string.Join( ",", ComparisonMetrics.Select( metric => Number( comparison.PercentChanges.GetValueOrDefault( metric.Name ) ) ) );
		var calls = string.Join( ",", result.CallCounts.Enumerate().Select( entry => entry.Count.ToString( System.Globalization.CultureInfo.InvariantCulture ) ) );
		return baseRow + "," + changes + "," + calls;
	}

	private static string SerializePercentChangesJson( ScenarioComparison comparison ) =>
		string.Join( ",", ComparisonMetrics.Select( metric => $"\"change_{metric.Name}_pct\":{Number( comparison.PercentChanges.GetValueOrDefault( metric.Name ) )}" ) );

	private static string SerializeCallCountsJson( VoxelCallCountSnapshot counts ) =>
		string.Join( ",", counts.Enumerate().Select( entry => $"\"{CallCountKey( entry.Name )}\":{entry.Count}" ) );

	private static string CallCountKey( string name ) => "calls_" + name.Replace( '.', '_' );

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
		Complete,
		Failed
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
		private double _networkOutBytesPerSecond;
		private double _networkInBytesPerSecond;
		private double _networkPingMilliseconds;
		private int _networkSampleCount;
		private int _maximumConnections;
		private ulong _peakTexturePoolUsedBytes;
		private ulong _peakTexturePoolNonEvictableBytes;
		private int _maximumPendingStreamingRequests;

		public string Name { get; }
		public string Description { get; }
		public int EditCount { get; private set; }
		public int ChangedChunkEvents { get; private set; }

		public FrameSampler( string name, string description, VoxelTerrainDiagnostics startingDiagnostics, VoxelCallCountSnapshot startingCallCounts )
		{
			Name = name;
			Description = description;
			_startingDiagnostics = startingDiagnostics;
			_startingCallCounts = startingCallCounts;
			_startingMessagesSent = SumMessagesSent();
			_startingMessagesReceived = SumMessagesReceived();
		}

		public void RecordEdit( int changedChunks, double callMilliseconds )
		{
			EditCount++;
			ChangedChunkEvents += changedChunks;
			_editCallTimes.Add( callMilliseconds );
			_lastEditTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		}

		public void Sample()
		{
			var frameMilliseconds = Sandbox.Diagnostics.PerformanceStats.FrameTime * 1000.0;
			_frameTimes.Add( frameMilliseconds );
			_framesOver16Milliseconds += frameMilliseconds >= 16.6667 ? 1 : 0;
			_framesOver33Milliseconds += frameMilliseconds >= 33.3333 ? 1 : 0;
			_framesOver50Milliseconds += frameMilliseconds >= 50.0 ? 1 : 0;
			_framesOver100Milliseconds += frameMilliseconds >= 100.0 ? 1 : 0;
			if ( frameMilliseconds >= 33.3333 )
			{
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
		}

		public ScenarioResult Complete( VoxelTerrainDiagnostics diagnostics, VoxelCallCountSnapshot callCounts, double elapsedMilliseconds )
		{
			_frameTimes.Sort();
			_gpuTimes.Sort();
			_editCallTimes.Sort();
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
			var scenarioDiagnostics = diagnostics with
			{
				VisualBatchBuiltChunks = (int)System.Math.Clamp( diagnostics.TotalVisualBuildsCompleted - _startingDiagnostics.TotalVisualBuildsCompleted, 0, int.MaxValue ),
				VisualBatchElapsedMilliseconds = System.Math.Max( 0.0, diagnostics.TotalVisualBatchElapsedMilliseconds - _startingDiagnostics.TotalVisualBatchElapsedMilliseconds ),
				SnapshotWaitMilliseconds = System.Math.Max( 0.0, diagnostics.TotalSnapshotWaitMilliseconds - _startingDiagnostics.TotalSnapshotWaitMilliseconds ),
				SnapshotCopyMilliseconds = System.Math.Max( 0.0, diagnostics.TotalSnapshotCopyMilliseconds - _startingDiagnostics.TotalSnapshotCopyMilliseconds ),
				WorkerMeshMilliseconds = System.Math.Max( 0.0, diagnostics.TotalWorkerMeshMilliseconds - _startingDiagnostics.TotalWorkerMeshMilliseconds ),
				MainThreadUploadMilliseconds = System.Math.Max( 0.0, diagnostics.TotalMainThreadUploadMilliseconds - _startingDiagnostics.TotalMainThreadUploadMilliseconds )
			};
			return new ScenarioResult
			{
				Name = Name,
				Description = Description,
				Passed = diagnostics.FailedVisualChunks == 0 && diagnostics.PendingVisualBuilds == 0 && diagnostics.PendingCollisionBuilds == 0 && !diagnostics.PlayerSafetyActive && _exceptions == 0,
				EditCount = EditCount,
				ChangedChunkEvents = ChangedChunkEvents,
				ElapsedMilliseconds = elapsedMilliseconds,
				FrameCount = count,
				FrameAverageMilliseconds = frameAverage,
				FrameP50Milliseconds = Percentile( _frameTimes, 0.50 ),
				FrameP95Milliseconds = Percentile( _frameTimes, 0.95 ),
				FrameP99Milliseconds = frameP99,
				FrameP999Milliseconds = frameP999,
				FrameMaximumMilliseconds = frameMaximum,
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
				CallCounts = callCounts.Subtract( _startingCallCounts )
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
		public double ElapsedMilliseconds { get; init; }
		public int FrameCount { get; init; }
		public double FrameAverageMilliseconds { get; init; }
		public double FrameP50Milliseconds { get; init; }
		public double FrameP95Milliseconds { get; init; }
		public double FrameP99Milliseconds { get; init; }
		public double FrameP999Milliseconds { get; init; }
		public double FrameMaximumMilliseconds { get; init; }
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
		public VoxelCallCountSnapshot CallCounts { get; init; }
	}
}
