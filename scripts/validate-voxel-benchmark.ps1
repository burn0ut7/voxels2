[CmdletBinding()]
param(
	[string] $ReportDirectory = (Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\sbox\data\local\voxels2#local\voxel-terrain-benchmarks'),
	[string] $ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredScenarios = @(
	'cold_generation',
	'varied_edits',
	'bulk_edit',
	'sustained_world_sweep_and_depth_dig_20hz',
	'sustained_world_spiral_place_20hz'
)
$requiredMetrics = @(
	'avg_fps', 'one_percent_low_fps', 'point_one_percent_low_fps', 'frame_p95_ms', 'frame_max_ms',
	'stutter_events', 'gpu_p95_ms', 'edit_call_p95_ms', 'post_edit_settle_ms', 'update_max_ms',
	'render_max_ms', 'physics_max_ms', 'allocated_bytes', 'gc_pause_ms', 'peak_memory_bytes',
	'draw_calls_avg', 'triangles_rendered_avg', 'authoritative_sdf_bytes', 'visual_triangles', 'collision_triangles',
	'worker_mesh_ms', 'upload_ms'
)
$requiredCallMetrics = @(
	'calls_manager_updates', 'calls_generation_requests', 'calls_generation_polls', 'calls_generation_chunks',
	'calls_brush_requests', 'calls_brush_chunk_tests', 'calls_brush_samples_tested', 'calls_brush_samples_changed',
	'calls_visual_world_starts', 'calls_visual_queue_pumps', 'calls_visual_builds_queued', 'calls_visual_builds_started',
	'calls_visual_halo_snapshots', 'calls_visual_halo_samples_copied', 'calls_visual_builds_completed', 'calls_visual_uploads',
	'calls_collision_interest_refreshes', 'calls_collision_queue_pumps', 'calls_collision_builds_queued',
	'calls_collision_builds_started', 'calls_collision_snapshot_samples_copied', 'calls_collision_builds_completed',
	'calls_collision_uploads'
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
if ( $report.suite_version -ne 1 ) { Add-Failure "Expected suite version 1, found '$($report.suite_version)'" }
if ( $report.suite_complete -ne $true ) { Add-Failure 'Latest run is marked incomplete' }
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
	foreach ( $metric in $requiredMetrics + $requiredCallMetrics )
	{
		Test-RequiredProperty $scenario $metric $context
	}
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
	foreach ( $column in @('suite_version', 'suite_complete') + $requiredMetrics + $requiredCallMetrics )
	{
		Test-RequiredProperty $csvRows[-1] $column 'CSV'
	}
}

$dashboard = Get-Content -LiteralPath $dashboardPath -Raw
if ( $dashboard -notmatch "label='Call frequency'" ) { Add-Failure 'Dashboard has no call-frequency graph group' }
if ( $dashboard -notmatch 'suite_complete' ) { Add-Failure 'Dashboard does not expose suite completeness' }
$markdown = Get-Content -LiteralPath $latestMarkdownPath -Raw
if ( $markdown -notmatch '## Call frequency' ) { Add-Failure 'Markdown report has no call-frequency section' }
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
