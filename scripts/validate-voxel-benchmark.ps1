[CmdletBinding()]
param(
	[string] $ReportDirectory = (Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\sbox\data\local\voxels2#local\voxel-terrain-benchmarks'),
	[string] $ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$fullRequiredScenarios = @(
	'cold_generation',
	'collision_backlog_frame_budget',
	'collision_proximity_edit_filter',
	'phase4_planner_counts',
	'phase4_planner_reference_equivalence',
	'phase4_negative_coordinates',
	'phase4_vertical_movement',
	'phase4_regular_coverage',
	'phase4_no_lod_overlap',
	'phase4_neighbor_difference',
	'phase4_four_level_b4_movement',
	'phase4_four_level_b8_movement',
	'phase4_four_level_stationary_soak',
	'phase4_transition_ownership',
	'phase5_sparse_edit_contract',
	'phase5_deterministic_invalidation',
	'phase5_stale_edit_generations',
	'phase5_edit_eviction_reentry',
	'phase5_voxel_brush_raycast',
	'phase5_gpu_edit_revision_binding',
	'phase5_incremental_edit_replay',
	'phase4_transition_all_512_cases',
	'phase4_transition_six_orientations',
	'phase4_transition_plane',
	'phase4_transition_sphere',
	'phase4_transition_cave',
	'phase4_transition_tangent_surface',
	'phase4_transition_watertight_edges',
	'phase4_transition_no_duplicate_faces',
	'phase4_indirect_1_to_1024',
	'phase4_indirect_boundary_49',
	'phase4_depth_opaque_parity',
	'phase4_command_list_active_range',
	'phase4_regular_b4_l2_stationary',
	'phase4_regular_b4_l4_stationary',
	'phase4_regular_radius64_match',
	'gpu_lod5_transition_ownership',
	'gpu_realtime_surface_edits_20hz',
	'gpu_transvoxel_regular_proof',
	'gpu_persistent_static_set',
	'gpu_production_render_integration',
	'gpu_player_infinity_streaming',
	'gpu_player_line_streaming',
	'gpu_player_diagonal_streaming',
	'gpu_player_clipbox_oscillation',
	'gpu_allocator_churn',
	'gpu_replacement_failure',
	'gpu_pool_exhaustion',
	'gpu_return_origin_stability',
	'gpu_async_readback_saturation',
	'gpu_resource_recreation',
	'gpu_dedicated_server_startup',
	'live_chunk_radius_reconfiguration',
	'player_infinity_streaming',
	'player_line_streaming',
	'player_diagonal_streaming',
	'chunk_seam_edit_coherence',
	'varied_edits',
	'bulk_edit',
	'sustained_world_sweep_and_depth_dig_20hz',
	'sustained_world_spiral_place_20hz',
	'player_post_edit_line_streaming',
	'phase4_regular_b8_l4_stationary'
)
$gpuRequiredScenarios = @(
	'phase4_planner_counts', 'phase4_planner_reference_equivalence', 'phase4_negative_coordinates', 'phase4_vertical_movement', 'phase4_regular_coverage', 'phase4_no_lod_overlap', 'phase4_neighbor_difference', 'phase4_four_level_b4_movement', 'phase4_four_level_b8_movement', 'phase4_four_level_stationary_soak', 'phase4_transition_ownership', 'phase5_sparse_edit_contract', 'phase5_deterministic_invalidation', 'phase5_stale_edit_generations', 'phase5_edit_eviction_reentry', 'phase5_voxel_brush_raycast', 'phase5_gpu_edit_revision_binding', 'phase5_incremental_edit_replay', 'phase4_transition_all_512_cases', 'phase4_transition_six_orientations', 'phase4_transition_plane', 'phase4_transition_sphere', 'phase4_transition_cave', 'phase4_transition_tangent_surface', 'phase4_transition_watertight_edges', 'phase4_transition_no_duplicate_faces', 'phase4_indirect_1_to_1024', 'phase4_indirect_boundary_49', 'phase4_depth_opaque_parity', 'phase4_command_list_active_range', 'phase4_regular_b4_l2_stationary', 'phase4_regular_b4_l4_stationary', 'phase4_regular_radius64_match', 'gpu_lod5_transition_ownership', 'gpu_realtime_surface_edits_20hz',
	'gpu_persistent_static_set', 'gpu_production_render_integration',
	'gpu_player_infinity_streaming', 'gpu_player_line_streaming', 'gpu_player_diagonal_streaming', 'gpu_player_clipbox_oscillation',
	'gpu_allocator_churn', 'gpu_replacement_failure', 'gpu_pool_exhaustion', 'gpu_return_origin_stability',
	'gpu_async_readback_saturation', 'gpu_resource_recreation', 'gpu_dedicated_server_startup', 'phase4_regular_b8_l4_stationary'
)
$cpuRequiredScenarios = @(
	'cold_generation', 'collision_backlog_frame_budget', 'collision_proximity_edit_filter', 'phase4_planner_counts', 'phase4_planner_reference_equivalence', 'phase4_negative_coordinates', 'phase4_vertical_movement', 'phase4_regular_coverage', 'phase4_no_lod_overlap', 'phase4_neighbor_difference', 'phase4_four_level_b4_movement', 'phase4_four_level_b8_movement', 'phase4_four_level_stationary_soak', 'phase4_transition_ownership', 'phase5_sparse_edit_contract', 'phase5_deterministic_invalidation', 'phase5_stale_edit_generations', 'phase5_edit_eviction_reentry', 'phase5_voxel_brush_raycast', 'phase5_gpu_edit_revision_binding', 'phase5_incremental_edit_replay', 'phase4_transition_all_512_cases', 'phase4_transition_six_orientations', 'phase4_transition_plane', 'phase4_transition_sphere', 'phase4_transition_cave', 'phase4_transition_tangent_surface', 'phase4_transition_watertight_edges', 'phase4_transition_no_duplicate_faces', 'phase4_indirect_1_to_1024', 'phase4_indirect_boundary_49', 'phase4_depth_opaque_parity', 'phase4_command_list_active_range', 'live_chunk_radius_reconfiguration', 'player_infinity_streaming', 'player_line_streaming',
	'player_diagonal_streaming', 'chunk_seam_edit_coherence', 'varied_edits', 'bulk_edit',
	'sustained_world_sweep_and_depth_dig_20hz', 'sustained_world_spiral_place_20hz', 'player_post_edit_line_streaming'
)
$requiredMetrics = @(
	'avg_fps', 'one_percent_low_fps', 'point_one_percent_low_fps', 'frame_p95_ms', 'frame_max_ms', 'unaccounted_frame_ms',
	'stutter_events', 'gpu_p95_ms', 'edit_call_p95_ms', 'post_edit_settle_ms', 'update_max_ms',
	'render_max_ms', 'physics_max_ms', 'allocated_bytes', 'gc_pause_ms', 'peak_memory_bytes',
	'draw_calls_avg', 'triangles_rendered_avg', 'authoritative_sdf_bytes', 'visual_triangles', 'collision_triangles', 'player_safety_active',
	'visual_coherence_violation_frames',
	'worker_mesh_ms', 'upload_ms',
	'clipbox_planner_available', 'clipbox_planner_passed', 'clipbox_planner_failure', 'clipbox_planner_cases', 'clipbox_planner_configurations',
	'clipbox_planner_active_regular_count', 'clipbox_planner_stable_regular_slots', 'clipbox_planner_allocated_after_warmup', 'clipbox_planner_transition_capacity', 'clipbox_planner_active_transition_count', 'clipbox_planner_changed_transition_slots', 'clipbox_planner_transition_ownership_validated',
	'indirect_render_available', 'indirect_render_passed', 'indirect_render_failure', 'indirect_render_tested_command_counts', 'indirect_render_maximum_command_count',
	'indirect_render_group_size', 'indirect_render_boundary_command_count', 'indirect_render_boundary_active_lists', 'indirect_render_boundary_visible_commands', 'indirect_render_maximum_active_lists',
	'gpu_terrain_clipbox_revision', 'gpu_terrain_clipbox_changed_slots', 'gpu_terrain_clipbox_pending_revision_count', 'gpu_terrain_clipbox_max_pending_revision_count', 'gpu_terrain_clipbox_stable_slots', 'gpu_terrain_clipbox_active_slots', 'gpu_terrain_clipbox_dropped_work', 'gpu_terrain_clipbox_stationary_updates', 'gpu_terrain_clipbox_transition_capacity', 'gpu_terrain_clipbox_transition_active_slots', 'gpu_terrain_clipbox_transition_changed_slots', 'gpu_terrain_clipbox_transition_pending_slots', 'gpu_terrain_clipbox_transition_dependency_mismatches', 'gpu_terrain_clipbox_transition_stationary_updates',
	'gpu_transvoxel_available', 'gpu_transvoxel_passed', 'gpu_transvoxel_failure',
	'gpu_transvoxel_vertices', 'gpu_transvoxel_indices', 'gpu_transvoxel_active_cells',
	'gpu_transvoxel_overflow_attempts', 'gpu_transvoxel_buffer_bytes', 'gpu_transvoxel_submission_ms',
	'gpu_transvoxel_completion_ms', 'gpu_transvoxel_readback_ms',
	'gpu_transvoxel_batch_size', 'gpu_transvoxel_surface_blocks', 'gpu_transvoxel_dispatches',
	'gpu_transvoxel_gpu_publication_passed', 'gpu_transvoxel_gpu_publication_ms', 'gpu_transvoxel_cpu_publication_ms',
	'gpu_lod_ownership_available', 'gpu_lod_ownership_passed', 'gpu_lod_ownership_failure', 'gpu_lod_ownership_active_transitions', 'gpu_lod_ownership_published_transitions',
	'gpu_lod_ownership_duplicate_regular_triangles', 'gpu_lod_ownership_duplicate_transition_triangles', 'gpu_lod_ownership_cross_owner_triangles', 'gpu_lod_ownership_lod5_duplicate_triangles',
	'gpu_lod_ownership_collapsed_transitions', 'gpu_lod_ownership_undeformed_coarse_vertices', 'gpu_lod_ownership_boundary_vertices', 'gpu_lod_ownership_unmatched_boundary_vertices',
	'gpu_lod_ownership_max_seam_error', 'gpu_lod_ownership_readback_bytes', 'gpu_lod_ownership_readback_ms',
	'transition_reference_available', 'transition_reference_passed', 'transition_reference_failure', 'transition_reference_cases', 'transition_reference_orientations', 'transition_reference_fixture_cases', 'transition_reference_triangles', 'transition_reference_boundary_edges', 'transition_reference_gradient_normals', 'transition_reference_position_tolerance', 'transition_reference_tables_validated',
	'transition_gpu_case_available', 'transition_gpu_case_passed', 'transition_gpu_case_failure', 'transition_gpu_case_variants', 'transition_gpu_case_buffer_bytes', 'transition_gpu_case_submission_ms', 'transition_gpu_case_completion_ms', 'transition_gpu_case_readback_ms',
	'gpu_terrain_available', 'gpu_terrain_backend', 'gpu_terrain_requested_blocks', 'gpu_terrain_resident_blocks',
	'gpu_terrain_render_shader', 'gpu_terrain_production_lighting', 'gpu_terrain_depth_prepass',
	'gpu_terrain_depth_command_lists', 'gpu_terrain_opaque_command_lists',
	'gpu_terrain_pending_count_batches', 'gpu_terrain_pending_emit_batches', 'gpu_terrain_backpressure_events',
	'gpu_terrain_allocation_failures', 'gpu_terrain_stale_publications_rejected', 'gpu_terrain_visible_draw_commands',
	'gpu_terrain_capacity_limited', 'gpu_terrain_blocked_requests', 'gpu_terrain_capacity_evictions', 'gpu_terrain_capacity_deferrals',
	'gpu_terrain_vertex_free', 'gpu_terrain_index_free', 'gpu_terrain_vertex_largest_free', 'gpu_terrain_index_largest_free',
	'gpu_terrain_vertex_free_ranges', 'gpu_terrain_index_free_ranges',
	'gpu_terrain_scratch_bytes', 'gpu_terrain_pool_capacity_bytes', 'gpu_terrain_pool_used_bytes', 'gpu_terrain_pool_peak_bytes',
	'gpu_terrain_geometry_readback_bytes', 'gpu_terrain_count_submission_ms', 'gpu_terrain_count_readback_avg_ms',
	'gpu_terrain_count_readback_count', 'gpu_terrain_emit_submission_ms', 'gpu_terrain_failure',
	'gpu_terrain_count_submission_per_block_ms', 'gpu_terrain_emit_submission_per_block_ms',
	'gpu_terrain_request_to_visible_avg_ms', 'gpu_terrain_request_to_visible_p95_ms', 'gpu_terrain_request_to_visible_max_ms',
	'gpu_terrain_batch_completion_avg_ms', 'gpu_terrain_batch_completion_p95_ms', 'gpu_terrain_batch_completion_max_ms',
	'gpu_terrain_transition_resident_blocks', 'gpu_terrain_transition_renderable_residents', 'gpu_terrain_transition_visible_draw_commands',
	'gpu_terrain_transition_pending_requests', 'gpu_terrain_transition_blocked_requests', 'gpu_terrain_transition_allocated_vertex_count',
	'gpu_terrain_transition_allocated_index_count', 'gpu_terrain_transition_allocated_bytes', 'gpu_terrain_transition_geometry_readback_bytes',
	'gpu_terrain_transition_cpu_sdf_evaluations', 'gpu_terrain_transition_stale_scheduler_rejections', 'gpu_terrain_transition_stale_dependency_rejections',
	'gpu_edit_queue_count', 'gpu_edit_queue_avg_ms', 'gpu_edit_queue_p95_ms', 'gpu_edit_queue_max_ms',
	'gpu_edit_upload_avg_ms', 'gpu_edit_upload_p95_ms', 'gpu_edit_upload_max_ms',
	'gpu_edit_planner_avg_ms', 'gpu_edit_planner_p95_ms', 'gpu_edit_planner_max_ms',
	'gpu_edit_regular_delta_avg_ms', 'gpu_edit_regular_delta_p95_ms', 'gpu_edit_regular_delta_max_ms',
	'gpu_edit_transition_metadata_avg_ms', 'gpu_edit_transition_metadata_p95_ms', 'gpu_edit_transition_metadata_max_ms',
	'gpu_edit_transition_desired_avg_ms', 'gpu_edit_transition_desired_p95_ms', 'gpu_edit_transition_desired_max_ms',
	'gpu_phase2b_available', 'gpu_phase2b_passed', 'gpu_phase2b_test', 'gpu_phase2b_failure',
	'gpu_lifecycle_budget_bytes', 'gpu_lifecycle_peak_used_bytes', 'gpu_lifecycle_churn_operations',
	'gpu_lifecycle_allocation_failures', 'gpu_lifecycle_backpressure_events',
	'gpu_lifecycle_stale_publications_rejected', 'gpu_lifecycle_retained_delta_percent',
	'gpu_phase3a_available', 'gpu_phase3a_passed', 'gpu_phase3a_test', 'gpu_phase3a_failure',
	'gpu_terrain_desired_blocks', 'gpu_terrain_resident_capacity', 'gpu_terrain_pending_request_capacity',
	'gpu_terrain_pending_request_count', 'gpu_terrain_pending_publication_count', 'gpu_terrain_queues_bounded',
	'gpu_phase3b_available', 'gpu_phase3b_passed', 'gpu_phase3b_test', 'gpu_phase3b_failure',
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
if ( $report.suite_version -ne 32 ) { Add-Failure "Expected suite version 32, found '$($report.suite_version)'" }
if ( $null -eq $report.PSObject.Properties['benchmark_mode'] ) { Add-Failure 'Latest report is missing benchmark_mode' }
$requiredScenarios = switch ( [string]$report.benchmark_mode )
{
	'GpuOnly' { $gpuRequiredScenarios; break }
	'CpuOnly' { $cpuRequiredScenarios; break }
	default { $fullRequiredScenarios }
}
if ( $report.suite_complete -ne $true ) { Add-Failure 'Latest run is marked incomplete' }
foreach ( $property in @(
	'configuration_id', 'major_outlier_threshold_percent', 'automatic_reproduction', 'reproduction_of_run_id',
	'traversal_distance', 'traversal_speed', 'traversal_loops',
	'gpu_phase2b_visual_backend', 'gpu_phase2b_procedural_rule_version', 'gpu_phase2b_lod_policy',
	'gpu_phase2b_chunk_size', 'gpu_phase2b_voxel_size', 'gpu_phase2b_batch_capacity',
	'gpu_phase2b_scratch_ring_count', 'gpu_phase2b_multi_draw_command_limit', 'gpu_phase2b_vertex_pool_capacity', 'gpu_phase2b_index_pool_capacity',
	'gpu_phase2b_vertex_addressing', 'gpu_phase2b_retirement_mechanism'
) )
{
	Test-RequiredProperty $report $property 'Latest report'
}
Test-RequiredProperty $report 'working_tree_dirty' 'Latest report'
if ( $report.working_tree_dirty -ne $false ) { Add-Failure 'Latest report was captured from a dirty Git worktree' }
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
	elseif ( $scenario.scenario -eq 'gpu_lod5_transition_ownership' )
	{
		if ( $scenario.gpu_lod_ownership_available -ne $true ) { Add-Failure "$context has no LOD ownership proof" }
		if ( $scenario.gpu_lod_ownership_passed -ne $true ) { Add-Failure "$context LOD ownership failed: $($scenario.gpu_lod_ownership_failure)" }
		if ( [long]$scenario.gpu_terrain_requested_blocks -ne 2752 -or [long]$scenario.gpu_terrain_resident_blocks -ne 2752 ) { Add-Failure "$context did not settle the expected 2,752 B8 L6 regular blocks" }
		if ( [long]$scenario.gpu_terrain_clipbox_stable_slots -ne 3072 ) { Add-Failure "$context did not retain the expected 3,072 stable regular slots" }
		if ( [long]$scenario.gpu_lod_ownership_active_transitions -ne 1920 -or [long]$scenario.gpu_lod_ownership_published_transitions -ne 1920 ) { Add-Failure "$context did not publish all 1,920 transition assignments" }
		foreach ( $metric in @('gpu_lod_ownership_duplicate_regular_triangles', 'gpu_lod_ownership_duplicate_transition_triangles', 'gpu_lod_ownership_cross_owner_triangles', 'gpu_lod_ownership_lod5_duplicate_triangles', 'gpu_lod_ownership_collapsed_transitions', 'gpu_lod_ownership_undeformed_coarse_vertices', 'gpu_lod_ownership_unmatched_boundary_vertices') )
		{
			if ( [long]$scenario.$metric -ne 0 ) { Add-Failure "$context reported '$metric' = $($scenario.$metric)" }
		}
		if ( [long]$scenario.gpu_lod_ownership_boundary_vertices -le 0 ) { Add-Failure "$context validated no transition boundary vertices" }
		if ( [long]$scenario.gpu_lod_ownership_readback_bytes -le 0 -or [double]$scenario.gpu_lod_ownership_readback_ms -le 0 ) { Add-Failure "$context recorded no geometry proof readback" }
	}
	elseif ( $scenario.scenario -in @('phase4_planner_counts', 'phase4_planner_reference_equivalence', 'phase4_negative_coordinates', 'phase4_vertical_movement', 'phase4_regular_coverage', 'phase4_no_lod_overlap', 'phase4_neighbor_difference', 'phase4_four_level_b4_movement', 'phase4_four_level_b8_movement', 'phase4_four_level_stationary_soak') )
	{
		if ( $scenario.clipbox_planner_available -ne $true ) { Add-Failure "$context has no Phase 4 planner proof result" }
		if ( $scenario.clipbox_planner_passed -ne $true ) { Add-Failure "$context Phase 4 planner proof failed: $($scenario.clipbox_planner_failure)" }
		if ( [int]$scenario.clipbox_planner_cases -le 0 ) { Add-Failure "$context recorded no planner proof cases" }
		if ( [int]$scenario.clipbox_planner_configurations -lt 0 ) { Add-Failure "$context reported an invalid planner configuration count" }
		if ( [long]$scenario.clipbox_planner_allocated_after_warmup -ne 0 ) { Add-Failure "$context allocated after planner warm-up" }
	}
	elseif ( $scenario.scenario -eq 'phase4_transition_ownership' )
	{
		if ( $scenario.clipbox_planner_available -ne $true ) { Add-Failure "$context has no transition ownership proof result" }
		if ( $scenario.clipbox_planner_passed -ne $true ) { Add-Failure "$context transition ownership proof failed: $($scenario.clipbox_planner_failure)" }
		if ( $scenario.clipbox_planner_transition_ownership_validated -ne $true ) { Add-Failure "$context did not validate transition ownership" }
		if ( [int]$scenario.clipbox_planner_transition_capacity -ne 1152 ) { Add-Failure "$context did not report the B8 L4 transition capacity" }
		if ( [int]$scenario.clipbox_planner_active_transition_count -le 0 -or [int]$scenario.clipbox_planner_active_transition_count -gt 1152 ) { Add-Failure "$context reported an invalid B8 L4 active transition count" }
		if ( [int]$scenario.clipbox_planner_changed_transition_slots -le 0 ) { Add-Failure "$context recorded no transition slot movement" }
	}
	elseif ( $scenario.scenario -in @('phase4_transition_all_512_cases', 'phase4_transition_six_orientations', 'phase4_transition_plane', 'phase4_transition_sphere', 'phase4_transition_cave', 'phase4_transition_tangent_surface', 'phase4_transition_watertight_edges', 'phase4_transition_no_duplicate_faces') )
	{
		if ( $scenario.transition_reference_available -ne $true ) { Add-Failure "$context has no Transvoxel transition reference result" }
		if ( $scenario.transition_reference_passed -ne $true ) { Add-Failure "$context transition reference failed: $($scenario.transition_reference_failure)" }
		if ( $scenario.transition_reference_tables_validated -ne $true ) { Add-Failure "$context did not validate official transition tables" }
		if ( [int]$scenario.transition_reference_cases -ne 512 ) { Add-Failure "$context did not validate all 512 transition cases" }
		if ( [int]$scenario.transition_reference_orientations -ne 3072 ) { Add-Failure "$context did not validate all six orientations for all cases" }
		if ( [int]$scenario.transition_reference_fixture_cases -le 0 ) { Add-Failure "$context recorded no transition fixture cases" }
		if ( [int]$scenario.transition_reference_triangles -le 0 ) { Add-Failure "$context recorded no transition triangles" }
		if ( [int]$scenario.transition_reference_boundary_edges -le 0 ) { Add-Failure "$context recorded no watertight boundary edges" }
		if ( [int]$scenario.transition_reference_gradient_normals -le 0 ) { Add-Failure "$context recorded no gradient normals" }
		if ( $scenario.scenario -eq 'phase4_transition_all_512_cases' )
		{
			if ( $scenario.transition_gpu_case_available -ne $true ) { Add-Failure "$context has no GPU transition case proof result" }
			if ( $scenario.transition_gpu_case_passed -ne $true ) { Add-Failure "$context GPU transition case proof failed: $($scenario.transition_gpu_case_failure)" }
			if ( [int]$scenario.transition_gpu_case_variants -ne 6144 ) { Add-Failure "$context did not validate all GPU transition case/orientation/winding variants" }
			if ( [long]$scenario.transition_gpu_case_buffer_bytes -le 0 ) { Add-Failure "$context reported no GPU transition proof allocation" }
		}
	}
	elseif ( $scenario.scenario -in @('phase4_indirect_1_to_1024', 'phase4_indirect_boundary_49', 'phase4_depth_opaque_parity', 'phase4_command_list_active_range') )
	{
		if ( $scenario.indirect_render_available -ne $true ) { Add-Failure "$context has no Phase 4 indirect-render proof result" }
		if ( $scenario.indirect_render_passed -ne $true ) { Add-Failure "$context indirect-render proof failed: $($scenario.indirect_render_failure)" }
		if ( [int]$scenario.indirect_render_tested_command_counts -le 0 ) { Add-Failure "$context recorded no indirect command counts" }
		if ( [int]$scenario.indirect_render_maximum_command_count -lt 1024 ) { Add-Failure "$context did not cover 1,024 indirect commands" }
		if ( [int]$scenario.indirect_render_group_size -le 0 ) { Add-Failure "$context has no indirect command-group capability result" }
		if ( [int]$scenario.indirect_render_boundary_visible_commands -ne 49 ) { Add-Failure "$context did not validate the 49-command boundary" }
	}
	elseif ( $scenario.scenario -in @('phase4_regular_b4_l2_stationary', 'phase4_regular_b4_l4_stationary', 'phase4_regular_b8_l4_stationary') )
	{
		$expectedRegular = switch ( $scenario.scenario )
		{
			'phase4_regular_b4_l2_stationary' { @{ Policy = 'regular_clipbox_b4_l2'; Active = 120; Stable = 128 } ; break }
			'phase4_regular_b4_l4_stationary' { @{ Policy = 'regular_clipbox_b4_l4'; Active = 232; Stable = 256 } ; break }
			'phase4_regular_b8_l4_stationary' { @{ Policy = 'regular_clipbox_b8_l4'; Active = 1856; Stable = 2048 } ; break }
		}
		if ( $scenario.gpu_phase2b_available -ne $true ) { Add-Failure "$context has no GPU regular clipbox proof result" }
		if ( $scenario.gpu_phase2b_passed -ne $true ) { Add-Failure "$context regular clipbox proof failed: $($scenario.gpu_phase2b_failure)" }
		if ( $scenario.gpu_terrain_lod_policy -ne $expectedRegular.Policy ) { Add-Failure "$context reported LOD policy '$($scenario.gpu_terrain_lod_policy)'" }
		if ( [long]$scenario.gpu_terrain_requested_blocks -ne $expectedRegular.Active -or [long]$scenario.gpu_terrain_resident_blocks -ne $expectedRegular.Active ) { Add-Failure "$context did not settle $($expectedRegular.Active) active regular residents" }
		if ( [long]$scenario.gpu_terrain_clipbox_stable_slots -ne $expectedRegular.Stable -or [long]$scenario.gpu_terrain_clipbox_active_slots -ne $expectedRegular.Active ) { Add-Failure "$context reported incorrect clipbox stable/active counts" }
		if ( [long]$scenario.gpu_terrain_clipbox_dropped_work -ne 0 ) { Add-Failure "$context dropped clipbox work" }
		if ( [long]$scenario.gpu_terrain_clipbox_max_pending_revision_count -gt 1 ) { Add-Failure "$context exceeded one pending clipbox revision" }
		if ( [long]$scenario.gpu_terrain_pending_request_count -ne 0 -or [long]$scenario.gpu_terrain_pending_publication_count -ne 0 ) { Add-Failure "$context completed with pending regular clipbox work" }
		if ( [long]$scenario.gpu_terrain_transition_resident_blocks -ne [long]$scenario.gpu_terrain_clipbox_transition_active_slots ) { Add-Failure "$context did not publish every active transition resident" }
		if ( [long]$scenario.gpu_terrain_transition_pending_requests -ne 0 -or [long]$scenario.gpu_terrain_transition_blocked_requests -ne 0 ) { Add-Failure "$context completed with pending transition work" }
		if ( [long]$scenario.gpu_terrain_transition_geometry_readback_bytes -ne 0 -or [long]$scenario.gpu_terrain_transition_cpu_sdf_evaluations -ne 0 ) { Add-Failure "$context used a forbidden production transition readback or CPU SDF path" }
		if ( [long]$scenario.gpu_terrain_transition_allocated_bytes -lt 0 ) { Add-Failure "$context reported a negative transition allocation" }
		if ( $scenario.scenario -eq 'phase4_regular_b4_l2_stationary' -and [long]$scenario.gpu_terrain_transition_renderable_residents -le 0 ) { Add-Failure "$context did not emit production transition geometry" }
	}
	elseif ( $scenario.scenario -eq 'gpu_production_render_integration' )
	{
		if ( $scenario.gpu_phase3a_available -ne $true ) { Add-Failure "$context has no Phase 3A proof result" }
		if ( $scenario.gpu_phase3a_passed -ne $true ) { Add-Failure "$context Phase 3A proof failed: $($scenario.gpu_phase3a_failure)" }
		if ( $scenario.gpu_terrain_available -ne $true ) { Add-Failure "$context has no production GPU terrain diagnostics" }
		if ( [string]::IsNullOrWhiteSpace( [string]$scenario.gpu_terrain_render_shader ) ) { Add-Failure "$context has no production render shader" }
		if ( $scenario.gpu_terrain_production_lighting -ne $true ) { Add-Failure "$context did not use standard production lighting" }
		if ( $scenario.gpu_terrain_depth_prepass -ne $true ) { Add-Failure "$context did not attach a depth prepass" }
		if ( [int]$scenario.gpu_terrain_depth_command_lists -le 0 -or [int]$scenario.gpu_terrain_opaque_command_lists -le 0 ) { Add-Failure "$context did not attach bounded depth and opaque command lists" }
		if ( [long]$scenario.gpu_terrain_geometry_readback_bytes -ne 0 ) { Add-Failure "$context read back production geometry" }
	}
	elseif ( $scenario.scenario -in @('gpu_player_infinity_streaming', 'gpu_player_line_streaming', 'gpu_player_diagonal_streaming', 'gpu_player_clipbox_oscillation') )
	{
		if ( $scenario.gpu_phase3b_available -ne $true ) { Add-Failure "$context has no Phase 3B movement proof result" }
		if ( $scenario.gpu_phase3b_passed -ne $true ) { Add-Failure "$context Phase 3B movement proof failed: $($scenario.gpu_phase3b_failure)" }
		if ( $scenario.gpu_terrain_available -ne $true ) { Add-Failure "$context has no persistent GPU terrain diagnostics" }
		if ( $scenario.gpu_terrain_capacity_limited -ne $true -and [long]$scenario.gpu_terrain_resident_blocks -ne [long]$scenario.gpu_terrain_desired_blocks ) { Add-Failure "$context did not publish every desired block" }
		if ( $scenario.gpu_terrain_capacity_limited -eq $true -and [long]$scenario.gpu_terrain_blocked_requests -ne ([long]$scenario.gpu_terrain_desired_blocks - [long]$scenario.gpu_terrain_resident_blocks) ) { Add-Failure "$context capacity-limited admission does not account for all missing residents" }
		if ( [long]$scenario.gpu_terrain_pending_request_count -ne 0 -or [long]$scenario.gpu_terrain_pending_publication_count -ne 0 ) { Add-Failure "$context completed with pending GPU work" }
		if ( $scenario.gpu_terrain_queues_bounded -ne $true ) { Add-Failure "$context exceeded a bounded GPU queue" }
		if ( [long]$scenario.gpu_terrain_geometry_readback_bytes -ne 0 ) { Add-Failure "$context read back production geometry" }
		if ( [long]$scenario.gpu_terrain_transition_resident_blocks -ne [long]$scenario.gpu_terrain_clipbox_transition_active_slots ) { Add-Failure "$context did not settle all active transition residents" }
		if ( [long]$scenario.gpu_terrain_transition_pending_requests -ne 0 -or [long]$scenario.gpu_terrain_transition_blocked_requests -ne 0 ) { Add-Failure "$context completed with pending or blocked transition work" }
		if ( [long]$scenario.gpu_terrain_transition_geometry_readback_bytes -ne 0 -or [long]$scenario.gpu_terrain_transition_cpu_sdf_evaluations -ne 0 ) { Add-Failure "$context used a forbidden production transition path" }
		if ( [long]$scenario.calls_player_traversal_updates -le 0 ) { Add-Failure "$context did not move the actual player" }
	}
	elseif ( $scenario.scenario -eq 'gpu_realtime_surface_edits_20hz' )
	{
		if ( $scenario.gpu_terrain_available -ne $true ) { Add-Failure "$context has no persistent GPU terrain diagnostics" }
		if ( [int]$scenario.edits -ne 80 -or [int]$scenario.changed_chunk_events -le 0 ) { Add-Failure "$context did not execute eighty visible surface edits" }
		if ( [int]$scenario.gpu_edit_queue_count -lt 80 ) { Add-Failure "$context did not capture every GPU edit queue timing" }
		if ( [int]$scenario.gpu_terrain_clipbox_dropped_work -ne 0 ) { Add-Failure "$context dropped clipbox edit work" }
		if ( [int]$scenario.gpu_terrain_clipbox_transition_dependency_mismatches -ne 0 ) { Add-Failure "$context completed with transition dependency mismatches" }
		if ( [int]$scenario.gpu_terrain_pending_request_count -ne 0 -or [int]$scenario.gpu_terrain_pending_publication_count -ne 0 ) { Add-Failure "$context completed with pending GPU edit work" }
	}
	elseif ( $scenario.scenario -like 'gpu_*' )
	{
		if ( $scenario.gpu_phase2b_available -ne $true ) { Add-Failure "$context has no Phase 2B proof result" }
		if ( $scenario.gpu_phase2b_passed -ne $true ) { Add-Failure "$context Phase 2B proof failed: $($scenario.gpu_phase2b_failure)" }
		if ( $scenario.scenario -in @('gpu_persistent_static_set', 'gpu_async_readback_saturation', 'gpu_resource_recreation') )
		{
			if ( $scenario.gpu_terrain_available -ne $true ) { Add-Failure "$context has no persistent GPU terrain diagnostics" }
			if ($scenario.gpu_terrain_capacity_limited -ne $true -and [long]$scenario.gpu_terrain_resident_blocks -ne [long]$scenario.gpu_terrain_requested_blocks) { Add-Failure "$context did not publish every requested block" }
			if ($scenario.gpu_terrain_capacity_limited -eq $true -and [long]$scenario.gpu_terrain_blocked_requests -ne ([long]$scenario.gpu_terrain_requested_blocks - [long]$scenario.gpu_terrain_resident_blocks)) { Add-Failure "$context capacity-limited admission does not account for all missing residents" }
			if ([long]$scenario.gpu_terrain_geometry_readback_bytes -ne 0) { Add-Failure "$context read back production geometry" }
			if ($scenario.gpu_terrain_capacity_limited -ne $true -and [long]$scenario.gpu_terrain_allocation_failures -ne 0) { Add-Failure "$context exhausted its configured production pool" }
			if ([long]$scenario.gpu_terrain_pool_used_bytes -gt [long]$scenario.gpu_terrain_pool_capacity_bytes) { Add-Failure "$context exceeded persistent pool capacity" }
			if ([double]$scenario.gpu_terrain_request_to_visible_p95_ms -le 0) { Add-Failure "$context recorded no request-to-visible latency" }
			if ([double]$scenario.gpu_terrain_batch_completion_p95_ms -le 0) { Add-Failure "$context recorded no batch completion latency" }
		}
		if ( $scenario.scenario -eq 'gpu_async_readback_saturation' -and [int]$scenario.gpu_terrain_count_readback_count -lt 2 ) { Add-Failure "$context did not complete two compact readbacks" }
	}
	elseif ( $scenario.scenario -eq 'collision_proximity_edit_filter' )
	{
		if ( [long]$scenario.calls_brush_samples_changed -le 0 ) { Add-Failure "$context did not change authoritative terrain samples" }
		foreach ( $metric in @('calls_collision_builds_queued', 'calls_collision_builds_started', 'calls_collision_builds_completed', 'calls_collision_uploads') )
		{
			if ( [long]$scenario.$metric -ne 0 ) { Add-Failure "$context recorded forbidden distant collision work in '$metric': $($scenario.$metric)" }
		}
		if ( [long]$scenario.calls_safety_activations -ne 0 -or [long]$scenario.calls_safety_players_repositioned -ne 0 ) { Add-Failure "$context disturbed player safety for a distant edit" }
	}
	elseif ( $scenario.scenario -notlike 'player_*_streaming' -and $scenario.scenario -notlike 'phase5_*' -and [long]$scenario.calls_safety_players_repositioned -le 0 )
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
