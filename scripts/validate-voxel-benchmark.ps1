[CmdletBinding()]
param(
	[string] $ReportDirectory = (Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\sbox\data\local\voxels2#local\voxel-terrain-benchmarks'),
	[string] $ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredScenarios = @(
	'cold_generation',
	'gpu_transvoxel_regular_proof',
	'live_chunk_radius_reconfiguration',
	'player_infinity_streaming',
	'player_line_streaming',
	'player_diagonal_streaming',
	'chunk_seam_edit_coherence',
	'varied_edits',
	'bulk_edit',
	'sustained_world_sweep_and_depth_dig_20hz',
	'sustained_world_spiral_place_20hz'
)
$requiredMetrics = @(
	'avg_fps', 'one_percent_low_fps', 'point_one_percent_low_fps', 'frame_p95_ms', 'frame_max_ms',
	'stutter_events', 'gpu_p95_ms', 'edit_call_p95_ms', 'post_edit_settle_ms', 'update_max_ms',
	'render_max_ms', 'physics_max_ms', 'allocated_bytes', 'gc_pause_ms', 'peak_memory_bytes',
	'draw_calls_avg', 'triangles_rendered_avg', 'authoritative_sdf_bytes', 'visual_triangles', 'collision_triangles', 'player_safety_active',
	'visual_coherence_violation_frames',
	'worker_mesh_ms', 'upload_ms',
	'gpu_transvoxel_available', 'gpu_transvoxel_passed', 'gpu_transvoxel_failure',
	'gpu_transvoxel_vertices', 'gpu_transvoxel_indices', 'gpu_transvoxel_active_cells',
	'gpu_transvoxel_overflow_attempts', 'gpu_transvoxel_buffer_bytes', 'gpu_transvoxel_submission_ms',
	'gpu_transvoxel_completion_ms', 'gpu_transvoxel_readback_ms',
	'gpu_transvoxel_batch_size', 'gpu_transvoxel_surface_blocks', 'gpu_transvoxel_dispatches',
	'gpu_transvoxel_gpu_publication_passed', 'gpu_transvoxel_gpu_publication_ms', 'gpu_transvoxel_cpu_publication_ms',
	'stream_chunks_completed', 'stream_chunks_fresh', 'stream_chunks_cached', 'stream_batches_completed',
	'stream_sdf_generation_avg_ms', 'stream_sdf_generation_p95_ms', 'stream_sdf_generation_max_ms',
	'stream_mesh_queue_avg_ms', 'stream_mesh_queue_p95_ms', 'stream_mesh_queue_max_ms',
	'stream_snapshot_avg_ms', 'stream_snapshot_p95_ms', 'stream_snapshot_max_ms',
	'stream_worker_mesh_avg_ms', 'stream_worker_mesh_p95_ms', 'stream_worker_mesh_max_ms',
	'stream_publication_wait_avg_ms', 'stream_publication_wait_p95_ms', 'stream_publication_wait_max_ms',
	'stream_upload_avg_ms', 'stream_upload_p95_ms', 'stream_upload_max_ms',
	'stream_chunk_ready_avg_ms', 'stream_chunk_ready_p95_ms', 'stream_chunk_ready_max_ms',
	'stream_batch_avg_ms', 'stream_batch_p95_ms', 'stream_batch_max_ms'
)
$requiredCallMetrics = @(
	'calls_manager_updates', 'calls_generation_requests', 'calls_generation_polls', 'calls_generation_chunks',
	'calls_brush_requests', 'calls_brush_chunk_tests', 'calls_brush_samples_tested', 'calls_brush_samples_changed',
	'calls_visual_world_starts', 'calls_visual_queue_pumps', 'calls_visual_builds_queued', 'calls_visual_builds_started',
	'calls_visual_halo_snapshots', 'calls_visual_halo_samples_copied', 'calls_visual_builds_completed', 'calls_visual_uploads',
	'calls_collision_interest_refreshes', 'calls_collision_queue_pumps', 'calls_collision_builds_queued',
	'calls_collision_builds_started', 'calls_collision_snapshot_samples_copied', 'calls_collision_builds_completed',
	'calls_collision_uploads', 'calls_safety_activations', 'calls_safety_update_calls', 'calls_safety_players_repositioned',
	'calls_player_traversal_updates'
)
$requiredComparisonMetrics = @(
	'configuration_id', 'comparison_baseline_run_id', 'comparison_has_baseline', 'change_max_abs_pct',
	'outlier_detected', 'outlier_metrics', 'reproduction_of_run_id', 'reproduction_status',
	'change_avg_fps_pct', 'change_one_percent_low_fps_pct', 'change_frame_p95_ms_pct',
	'change_frame_max_ms_pct', 'change_gpu_p95_ms_pct', 'change_edit_call_p95_ms_pct',
	'change_post_edit_settle_ms_pct', 'change_allocated_bytes_pct', 'change_visual_batch_ms_pct',
	'change_worker_mesh_ms_pct', 'change_upload_ms_pct', 'change_stream_chunk_ready_p95_ms_pct', 'change_stream_batch_p95_ms_pct'
)
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure( [string] $Message )
{
	$failures.Add( $Message )
}

function Test-RequiredProperty( [object] $Object, [string] $Name, [string] $Context )
{
	if ( $null -eq $Object.PSObject.Properties[$Name] )
	{
		Add-Failure "$Context is missing '$Name'"
	}
}

$latestJsonPath = Join-Path $ReportDirectory 'latest-report.json'
$historyJsonLinesPath = Join-Path $ReportDirectory 'history.jsonl'
$historyCsvPath = Join-Path $ReportDirectory 'history.csv'
$latestMarkdownPath = Join-Path $ReportDirectory 'latest-report.md'
$dashboardPath = Join-Path $ReportDirectory 'dashboard.html'

foreach ( $path in @($latestJsonPath, $historyJsonLinesPath, $historyCsvPath, $latestMarkdownPath, $dashboardPath) )
{
	if ( -not (Test-Path -LiteralPath $path -PathType Leaf) )
	{
		Add-Failure "Missing report artifact: $path"
	}
}

if ( $failures.Count -gt 0 )
{
	throw ($failures -join [Environment]::NewLine)
}

$report = Get-Content -LiteralPath $latestJsonPath -Raw | ConvertFrom-Json
if ( $report.suite_version -ne 9 ) { Add-Failure "Expected suite version 9, found '$($report.suite_version)'" }
if ( $report.suite_complete -ne $true ) { Add-Failure 'Latest run is marked incomplete' }
foreach ( $property in @('configuration_id', 'major_outlier_threshold_percent', 'automatic_reproduction', 'reproduction_of_run_id', 'traversal_distance', 'traversal_speed', 'traversal_loops') )
{
	Test-RequiredProperty $report $property 'Latest report'
}
$headRevision = (& git -C $ProjectRoot rev-parse HEAD).Trim()
if ( $LASTEXITCODE -ne 0 ) { throw "Could not resolve Git HEAD for '$ProjectRoot'" }
$workingChanges = @(& git -C $ProjectRoot status --porcelain)
if ( $LASTEXITCODE -ne 0 ) { throw "Could not inspect Git status for '$ProjectRoot'" }
if ( $workingChanges.Count -gt 0 ) { Add-Failure 'The Git worktree is dirty; corpus data would not identify one immutable commit' }
if ( [string]::IsNullOrWhiteSpace( [string]$report.revision ) -or $report.revision -notmatch '^[0-9a-fA-F]{7,40}$' )
{
	Add-Failure "Revision '$($report.revision)' is not an immutable Git commit identifier"
}
elseif ( -not $headRevision.StartsWith( [string]$report.revision, [System.StringComparison]::OrdinalIgnoreCase ) )
{
	Add-Failure "Report revision '$($report.revision)' does not match current HEAD '$headRevision'"
}

$actualRequired = @($report.required_scenarios)
$actualExecuted = @($report.executed_scenarios)
if ( ($actualRequired -join '|') -ne ($requiredScenarios -join '|') ) { Add-Failure 'Required scenario manifest differs from the validator contract' }
if ( ($actualExecuted -join '|') -ne ($requiredScenarios -join '|') ) { Add-Failure 'Executed scenarios are missing, duplicated, or out of order' }

$scenarios = @($report.scenarios)
if ( $scenarios.Count -ne $requiredScenarios.Count )
{
	Add-Failure "Expected $($requiredScenarios.Count) scenario records, found $($scenarios.Count)"
}

foreach ( $scenario in $scenarios )
{
	$context = "Scenario '$($scenario.scenario)'"
	if ( $scenario.passed -ne $true ) { Add-Failure "$context failed" }
	if ( [int]$scenario.frames -le 0 ) { Add-Failure "$context recorded no frames" }
	if ( [int]$scenario.visual_coherence_violation_frames -ne 0 ) { Add-Failure "$context published a partial visual edit" }
	if ( $scenario.player_safety_active -ne $false ) { Add-Failure "$context completed while player safety was still active" }
	if ( $scenario.scenario -like 'player_*_streaming' )
	{
		if ( [long]$scenario.calls_player_traversal_updates -le 0 ) { Add-Failure "$context did not move the actual player" }
		if ( [long]$scenario.stream_chunks_completed -le 0 ) { Add-Failure "$context recorded no completed chunk streaming lifecycles" }
	}
	if ( $scenario.scenario -eq 'gpu_transvoxel_regular_proof' )
	{
		if ( $scenario.gpu_transvoxel_available -ne $true ) { Add-Failure "$context has no GPU proof result" }
		if ( $scenario.gpu_transvoxel_passed -ne $true ) { Add-Failure "$context GPU proof failed: $($scenario.gpu_transvoxel_failure)" }
		if ( [long]$scenario.gpu_transvoxel_vertices -le 0 -or [long]$scenario.gpu_transvoxel_indices -le 0 ) { Add-Failure "$context generated no mesh geometry" }
		if ( [long]$scenario.gpu_transvoxel_overflow_attempts -ne 0 ) { Add-Failure "$context overflowed GPU output buffers" }
		if ( [int]$scenario.gpu_transvoxel_batch_size -ne 128 ) { Add-Failure "$context did not validate the required 128-block batch" }
		if ( $scenario.gpu_transvoxel_gpu_publication_passed -ne $true ) { Add-Failure "$context did not publish GPU-authored indirect arguments" }
		if ( [int]$scenario.gpu_transvoxel_dispatches -ge 128 ) { Add-Failure "$context dispatch count did not demonstrate batching" }
	}
	elseif ( $scenario.scenario -notlike 'player_*_streaming' -and [long]$scenario.calls_safety_players_repositioned -le 0 )
	{
		Add-Failure "$context did not exercise player safety repositioning"
	}
	foreach ( $metric in $requiredMetrics + $requiredCallMetrics + $requiredComparisonMetrics )
	{
		Test-RequiredProperty $scenario $metric $context
	}
	$percentageMetrics = @($requiredComparisonMetrics | Where-Object { $_ -like 'change_*_pct' })
	if ( $scenario.comparison_has_baseline -eq $true )
	{
		foreach ( $metric in @('change_max_abs_pct', 'change_avg_fps_pct') )
		{
			if ( $null -eq $scenario.$metric ) { Add-Failure "$context has a baseline but '$metric' is null" }
		}
	}
	else
	{
		foreach ( $metric in $percentageMetrics )
		{
			if ( $null -ne $scenario.$metric ) { Add-Failure "$context has no baseline but '$metric' contains a synthetic value" }
		}
	}
	if ( [string]$scenario.reproduction_status -eq 'scheduled' ) { Add-Failure "$context is still awaiting its required reproduction run" }
}

$historyRows = @(
	Get-Content -LiteralPath $historyJsonLinesPath |
		Where-Object { -not [string]::IsNullOrWhiteSpace( $_ ) } |
		ForEach-Object { $_ | ConvertFrom-Json }
)
$latestHistoryRows = @($historyRows | Where-Object run_id -eq $report.run_id)
if ( $latestHistoryRows.Count -ne $requiredScenarios.Count )
{
	Add-Failure "JSONL has $($latestHistoryRows.Count) rows for latest run '$($report.run_id)', expected $($requiredScenarios.Count)"
}

$csvRows = @(Import-Csv -LiteralPath $historyCsvPath)
$latestCsvRows = @($csvRows | Where-Object run_id -eq $report.run_id)
if ( $latestCsvRows.Count -ne $requiredScenarios.Count )
{
	Add-Failure "CSV has $($latestCsvRows.Count) rows for latest run '$($report.run_id)', expected $($requiredScenarios.Count)"
}
if ( $csvRows.Count -gt 0 )
{
	foreach ( $column in @('suite_version', 'suite_complete') + $requiredMetrics + $requiredCallMetrics + $requiredComparisonMetrics )
	{
		Test-RequiredProperty $csvRows[-1] $column 'CSV'
	}
}

$dashboard = Get-Content -LiteralPath $dashboardPath -Raw
if ( $dashboard -notmatch 'data-dashboard-version="3"' ) { Add-Failure 'Dashboard is not the version 3 diagnostics application' }
foreach ( $surface in @('view-overview', 'view-trends', 'view-scenario', 'view-runs', 'view-glossary', 'trendMetric', 'chart', 'scenarioView', 'runsView', 'glossaryView') )
{
	if ( $dashboard -notmatch ('id="' + [regex]::Escape( $surface ) + '"') ) { Add-Failure "Dashboard is missing the '$surface' diagnostic surface" }
}
foreach ( $capability in @('What needs attention', 'Biggest movers and first occurrence', 'Machine-readable diagnosis', 'Average FPS change', 'Average FPS', 'Hottest calls and work units', 'Runs and direct comparison', 'Test and metric guide', 'Omit this run', 'Omitted runs', 'Restore all', 'Display preference only') )
{
	if ( $dashboard -notmatch [regex]::Escape( $capability ) ) { Add-Failure "Dashboard is missing '$capability'" }
}
if ( $dashboard -notmatch "metric:'change_max_abs_pct'" ) { Add-Failure 'Largest percentage change is not the default trend metric' }
if ( $dashboard -notmatch 'r\[state\.metric\]' ) { Add-Failure 'Dashboard does not exclude missing historical values from trend charts' }
if ( $dashboard -notmatch 'comparison_has_baseline===true' ) { Add-Failure 'Dashboard does not exclude synthetic legacy changes without a compatible baseline' }
if ( $dashboard -notmatch 'No compatible baseline comparisons' ) { Add-Failure 'Dashboard does not explain missing percentage-change data' }
if ( $dashboard -notmatch 'window\.voxelBenchmarkDashboard=' ) { Add-Failure 'Dashboard does not expose its structured model for automated diagnosis' }
if ( $dashboard -notmatch 'const allRows=\[\{' ) { Add-Failure 'Dashboard contains no embedded historical corpus rows' }
if ( $dashboard -notmatch 'localStorage\.setItem\(omissionStorageKey' ) { Add-Failure 'Dashboard does not persist omitted-run preferences' }
if ( $dashboard -notmatch 'rows=allRows\.filter' ) { Add-Failure 'Dashboard analysis does not exclude omitted runs' }
if ( $dashboard -notmatch 'restoreAllRuns' ) { Add-Failure 'Dashboard cannot restore omitted runs' }
$markdown = Get-Content -LiteralPath $latestMarkdownPath -Raw
if ( $markdown -notmatch '## Call frequency' ) { Add-Failure 'Markdown report has no call-frequency section' }
if ( $markdown -notmatch '## Percentage change and outliers' ) { Add-Failure 'Markdown report has no percentage-change section' }
if ( $markdown -notmatch 'completeness: \*\*COMPLETE\*\*' ) { Add-Failure 'Markdown report is not marked complete' }

if ( $failures.Count -gt 0 )
{
	throw ("Voxel benchmark corpus validation failed:`n - " + ($failures -join "`n - "))
}

[PSCustomObject]@{
	Passed = $true
	RunId = $report.run_id
	Revision = $report.revision
	SuiteVersion = $report.suite_version
	Scenarios = $scenarios.Count
	HistoryRows = $historyRows.Count
	CsvRows = $csvRows.Count
	ReportDirectory = (Resolve-Path -LiteralPath $ReportDirectory).Path
}
