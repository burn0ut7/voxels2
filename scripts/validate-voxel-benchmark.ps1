[CmdletBinding()]
param(
	[string] $ReportDirectory = (Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\sbox\data\local\voxels2#local\voxel-terrain-benchmarks'),
	[string] $ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredScenarios = @(
	'cold_generation',
	'player_natural_traversal',
	'varied_edits',
	'bulk_edit',
	'sustained_world_sweep_and_depth_dig_20hz',
	'sustained_world_spiral_place_20hz',
	'player_post_edit_revisit'
)

$latestJsonPath = Join-Path $ReportDirectory 'latest-report.json'
$historyJsonlPath = Join-Path $ReportDirectory 'history.jsonl'
$historyCsvPath = Join-Path $ReportDirectory 'history.csv'
$latestMarkdownPath = Join-Path $ReportDirectory 'latest-report.md'
$dashboardPath = Join-Path $ReportDirectory 'dashboard.html'
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure([string] $Message) { $failures.Add($Message) }
function Require-Path([string] $Path) { if (-not (Test-Path -LiteralPath $Path)) { Add-Failure "Missing report artifact: $Path" } }
function Require-Property($Object, [string] $Name, [string] $Context) {
	if ($null -eq $Object.PSObject.Properties[$Name]) { Add-Failure "$Context is missing '$Name'" }
}

foreach ($path in @($latestJsonPath, $historyJsonlPath, $historyCsvPath, $latestMarkdownPath, $dashboardPath)) { Require-Path $path }
if ($failures.Count -gt 0) { throw ($failures -join "`n") }

$report = Get-Content -LiteralPath $latestJsonPath -Raw | ConvertFrom-Json
if ($report.benchmark_mode -ne 'GpuOnly') { Add-Failure "Latest report benchmark_mode is '$($report.benchmark_mode)', expected GpuOnly" }
if ($report.suite_complete -ne $true) { Add-Failure 'Latest report is not marked complete' }
if ([int]$report.suite_version -lt 43) { Add-Failure 'Latest report uses the legacy suite version' }

$requiredFromReport = @($report.required_scenarios)
$executedFromReport = @($report.executed_scenarios)
if (($requiredFromReport -join '|') -ne ($requiredScenarios -join '|')) { Add-Failure 'required_scenarios does not match the rebuilt manifest order' }
if (($executedFromReport -join '|') -ne ($requiredScenarios -join '|')) { Add-Failure 'executed_scenarios does not match the rebuilt manifest order' }
if ($executedFromReport.Count -ne $requiredScenarios.Count) { Add-Failure "Executed $($executedFromReport.Count) scenarios; expected $($requiredScenarios.Count)" }
if (@($executedFromReport | Select-Object -Unique).Count -ne $executedFromReport.Count) { Add-Failure 'Executed scenario manifest contains duplicates' }

$scenarios = @($report.scenarios)
if ($scenarios.Count -ne $requiredScenarios.Count) { Add-Failure "Latest JSON contains $($scenarios.Count) scenario rows; expected $($requiredScenarios.Count)" }
for ($index = 0; $index -lt [Math]::Min($scenarios.Count, $requiredScenarios.Count); $index++) {
	$scenario = $scenarios[$index]
	$context = "Scenario[$index] '$($scenario.scenario)'"
	if ($scenario.scenario -ne $requiredScenarios[$index]) { Add-Failure "$context is out of order; expected '$($requiredScenarios[$index])'" }
	if ($scenario.passed -ne $true) { Add-Failure "$context did not pass" }
	foreach ($metric in @('avg_fps','one_percent_low_fps','frame_p95_ms','frame_max_ms','gpu_p95_ms','frames','elapsed_ms','edits','changed_chunk_events','failed_visual_chunks','pending_visual_builds','pending_collision_builds','visual_coherence_violation_frames','exceptions','gpu_terrain_available','gpu_terrain_requested_blocks','gpu_terrain_resident_blocks')) { Require-Property $scenario $metric $context }
	if ([double]$scenario.avg_fps -le 0 -or [int]$scenario.frames -le 0) { Add-Failure "$context has no frame sample" }
	if ([int]$scenario.failed_visual_chunks -ne 0 -or [int]$scenario.pending_visual_builds -ne 0 -or [int]$scenario.pending_collision_builds -ne 0) { Add-Failure "$context ended with pending or failed terrain work" }
	if ([int]$scenario.visual_coherence_violation_frames -ne 0) { Add-Failure "$context recorded visual coherence violations" }
	if ([int]$scenario.exceptions -ne 0) { Add-Failure "$context recorded exceptions" }
	if ($scenario.gpu_terrain_available -ne $true -or [int]$scenario.gpu_terrain_requested_blocks -le 0) { Add-Failure "$context lacks live GPU terrain diagnostics" }
	if ($scenario.scenario -like 'player_*') {
		Require-Property $scenario 'calls_player_traversal_updates' $context
		if ([long]$scenario.calls_player_traversal_updates -le 0) { Add-Failure "$context did not move a real PlayerController" }
	}
	if ($scenario.scenario -in @('varied_edits','bulk_edit','sustained_world_sweep_and_depth_dig_20hz','sustained_world_spiral_place_20hz')) {
		if ([int]$scenario.edits -le 0) { Add-Failure "$context did not deform terrain" }
	}
}

$historyRows = @(Get-Content -LiteralPath $historyJsonlPath | Where-Object { $_ -match ('"run_id":"' + [regex]::Escape([string]$report.run_id) + '"') } | ForEach-Object { $_ | ConvertFrom-Json })
if ($historyRows.Count -ne $requiredScenarios.Count) { Add-Failure "JSONL contains $($historyRows.Count) rows for latest run; expected $($requiredScenarios.Count)" }
$csvRows = @(Import-Csv -LiteralPath $historyCsvPath)
$latestCsvRows = @($csvRows | Where-Object { $_.run_id -eq $report.run_id })
if ($latestCsvRows.Count -ne $requiredScenarios.Count) { Add-Failure "CSV contains $($latestCsvRows.Count) rows for latest run; expected $($requiredScenarios.Count)" }

$markdown = Get-Content -LiteralPath $latestMarkdownPath -Raw
if ($markdown -notmatch 'completeness: \*\*COMPLETE\*\*') { Add-Failure 'Markdown report is not marked complete' }
$dashboard = Get-Content -LiteralPath $dashboardPath -Raw
if ($dashboard -notmatch 'data-dashboard-version="3"') { Add-Failure 'HTML dashboard is not the version 3 dashboard' }
if ($dashboard -notmatch 'player_natural_traversal') { Add-Failure 'HTML dashboard does not contain the rebuilt suite scenario' }
if ($dashboard -match 'CpuOnly|cpu_only') { Add-Failure 'HTML dashboard contains a CPU-only benchmark path' }

if ($failures.Count -gt 0) { throw ("Voxel benchmark corpus validation failed:`n - " + ($failures -join "`n - ")) }

[PSCustomObject]@{
	Passed = $true
	RunId = $report.run_id
	Revision = $report.revision
	SuiteVersion = $report.suite_version
	Scenarios = $scenarios.Count
	LatestJsonlRows = $historyRows.Count
	LatestCsvRows = $latestCsvRows.Count
	ReportDirectory = (Resolve-Path -LiteralPath $ReportDirectory).Path
}
