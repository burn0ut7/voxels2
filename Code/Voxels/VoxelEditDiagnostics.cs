internal readonly record struct VoxelEditProofReport( bool Passed, string Failure, int Cases, int Operations, int InvalidatedBlocks );

internal static class VoxelEditDiagnostics
{
	public static VoxelEditProofReport RunScenario( string scenario )
	{
		try
		{
			return scenario switch
			{
				"phase5_sparse_edit_contract" => RunContract(),
				"phase5_deterministic_invalidation" => RunInvalidation(),
				"phase5_stale_edit_generations" => RunStaleGenerations(),
				"phase5_edit_eviction_reentry" => RunEvictionReentry(),
				"phase5_voxel_brush_raycast" => RunBrushRaycast(),
				"phase5_gpu_edit_revision_binding" => RunGpuEditRevisionBinding(),
				_ => new VoxelEditProofReport( false, $"Unknown Phase Five edit scenario '{scenario}'.", 0, 0, 0 )
			};
		}
		catch ( System.Exception exception )
		{
			return new VoxelEditProofReport( false, exception.Message, 0, 0, 0 );
		}
	}

	private static VoxelEditProofReport RunContract()
	{
		var journal = new VoxelEditJournal();
		var operations = new[]
		{
			Operation( VoxelEditShape.Sphere, VoxelCsgOperation.Subtract, Vector3.Zero, new Vector3( 4, 0, 0 ) ),
			Operation( VoxelEditShape.Capsule, VoxelCsgOperation.Add, new Vector3( 10, 0, 0 ), new Vector3( 2, 4, 0 ) ),
			Operation( VoxelEditShape.OrientedBox, VoxelCsgOperation.SmoothSubtract, new Vector3( -10, 0, 0 ), new Vector3( 3, 2, 1 ), 1.5f ),
			Operation( VoxelEditShape.Sphere, VoxelCsgOperation.MaterialPaint, new Vector3( 10, 0, 0 ), new Vector3( 3, 0, 0 ) )
		};
		for ( var index = 0; index < operations.Length; index++ )
		{
			var accepted = journal.Append( operations[index] );
			Require( accepted.EditId == (ulong)(index + 1), "edit IDs are not monotonic" );
			Require( accepted.WorldRevision == (uint)(index + 1), "world revisions are not monotonic" );
		}
		Require( journal.EvaluateDistance( Vector3.Zero, -2.0f ) > 0.0f, "sphere subtraction did not remove solid terrain" );
		Require( journal.EvaluateDistance( new Vector3( 10, 0, 0 ), 4.0f ) < 0.0f, "capsule addition did not create solid terrain" );
		Require( journal.EvaluateMaterial( new Vector3( 10, 0, 0 ), -1.0f, VoxelMaterial.Terrain ) == (VoxelMaterial)2, "material paint was not ordered over the CSG result" );
		var snapshot = journal.CreateGpuSnapshot();
		Require( snapshot.Length == operations.Length && snapshot[^1].WorldRevision == operations.Length, "GPU snapshot does not preserve the ordered journal" );
		return new VoxelEditProofReport( true, string.Empty, 12, operations.Length, 0 );
	}

	private static VoxelEditProofReport RunInvalidation()
	{
		var edit = Operation( VoxelEditShape.Sphere, VoxelCsgOperation.Subtract, new Vector3( -32, 0, 0 ), new Vector3( 4, 0, 0 ) );
		var cpu = VoxelEditInvalidation.GetCpuChunks( edit, 32 );
		Require( cpu.Contains( new Vector3Int( -2, 0, 0 ) ) && cpu.Contains( new Vector3Int( -1, 0, 0 ) ), "negative border invalidation omitted a touching CPU chunk" );
		var candidates = new[]
		{
			new VoxelVisualBlockKey( new Vector3Int( -2, 0, 0 ), 0, 1 ),
			new VoxelVisualBlockKey( new Vector3Int( -1, 0, 0 ), 0, 1 ),
			new VoxelVisualBlockKey( new Vector3Int( 0, 0, 0 ), 1, 1 ),
			new VoxelVisualBlockKey( new Vector3Int( 20, 20, 20 ), 0, 1 )
		};
		var visual = VoxelEditInvalidation.GetVisualBlocks( edit, candidates, 32 );
		Require( visual.Count >= 2 && !visual.Contains( candidates[^1] ), "visual invalidation was not local and deterministic" );
		var repeat = VoxelEditInvalidation.GetVisualBlocks( edit, candidates, 32 );
		Require( visual.SetEquals( repeat ), "visual invalidation changed between identical calls" );
		return new VoxelEditProofReport( true, string.Empty, 8, 1, visual.Count );
	}

	private static VoxelEditProofReport RunStaleGenerations()
	{
		var scheduler = new VoxelGpuBatchScheduler( 8 );
		var oldKey = new VoxelVisualBlockKey( Vector3Int.Zero, 0, 1, 1 );
		var newKey = oldKey with { EditRevision = 2 };
		Require( scheduler.TryEnqueue( oldKey, out var oldGeneration ), "initial edit generation was not queued" );
		scheduler.Cancel( oldKey );
		Require( !scheduler.IsCurrent( oldKey, oldGeneration ), "cancelled edit generation remained current" );
		Require( scheduler.TryEnqueue( newKey, out var newGeneration ) && scheduler.IsCurrent( newKey, newGeneration ), "new edit revision was not independently current" );
		return new VoxelEditProofReport( true, string.Empty, 4, 2, 1 );
	}

	private static VoxelEditProofReport RunEvictionReentry()
	{
		var journal = new VoxelEditJournal();
		var operation = journal.Append( Operation( VoxelEditShape.Sphere, VoxelCsgOperation.SmoothSubtract, new Vector3( 17, -31, 0 ), new Vector3( 6, 0, 0 ), 1.0f ) );
		var samples = new[] { new Vector3( 17, -31, 0 ), new Vector3( 20, -31, 0 ), new Vector3( 24, -31, 0 ) };
		var first = samples.Select( sample => journal.EvaluateDistance( sample, sample.z ) ).ToArray();
		var second = samples.Select( sample => journal.EvaluateDistance( sample, sample.z ) ).ToArray();
		for ( var index = 0; index < first.Length; index++ ) Require( first[index] == second[index], "journal replay changed after simulated eviction/re-entry" );

		var planner = new VoxelClipboxRuntimePlanner( VoxelClipboxConfig.FastCorrectness );
		planner.Update( Vector3Int.Zero );
		planner.Commit();
		planner.ApplyEdit( operation, operation.WorldRevision, VoxelClipboxConfig.CellsPerBlock );
		planner.Commit();
		planner.Update( new Vector3Int( 4096, 4096, 0 ) );
		planner.Commit();
		planner.Update( Vector3Int.Zero );
		var candidates = planner.DesiredSlots.Where( slot => slot.Active ).Select( slot => slot.Key ).ToArray();
		var affected = VoxelEditInvalidation.GetVisualBlocks( operation, candidates, VoxelClipboxConfig.CellsPerBlock );
		Require( affected.Count > 0 && affected.All( key => key.EditRevision == operation.WorldRevision ), "clipbox re-entry lost the edit revision" );
		return new VoxelEditProofReport( true, string.Empty, samples.Length + affected.Count, 1, affected.Count );
	}

	private static VoxelEditProofReport RunBrushRaycast()
	{
		Require( VoxelSdfRaycast.TryTrace( new Vector3( 0, 0, 10 ), Vector3.Down, 20, 0.25f, point => point.z, out var hit ), "brush ray did not find the authoritative surface" );
		Require( System.MathF.Abs( hit.z ) <= 0.25f, "brush ray converged outside its surface tolerance" );
		Require( !VoxelSdfRaycast.TryTrace( new Vector3( 0, 0, 10 ), Vector3.Up, 20, 0.25f, point => point.z, out _ ), "brush ray reported a surface behind its aim direction" );
		Require( VoxelSdfRaycast.TryTrace( new Vector3( 0, 0, -10 ), Vector3.Up, 20, 0.25f, point => point.z, out _ ), "brush ray could not exit solid terrain" );
		return new VoxelEditProofReport( true, string.Empty, 4, 0, 0 );
	}

	private static VoxelEditProofReport RunGpuEditRevisionBinding()
	{
		Require( VoxelGpuEditDispatch.GetOperationCount( 0, 3 ) == 0, "unedited GPU blocks did not bind the empty journal prefix" );
		Require( VoxelGpuEditDispatch.GetOperationCount( 1, 3 ) == 1, "GPU blocks did not bind their first edit revision" );
		Require( VoxelGpuEditDispatch.GetOperationCount( 3, 3 ) == 3, "GPU blocks did not bind the complete uploaded journal" );
		var rejected = false;
		try { VoxelGpuEditDispatch.GetOperationCount( 4, 3 ); }
		catch ( System.InvalidOperationException ) { rejected = true; }
		Require( rejected, "GPU blocks accepted an edit revision beyond the uploaded journal" );
		return new VoxelEditProofReport( true, string.Empty, 4, 3, 1 );
	}

	private static VoxelEditOp Operation( VoxelEditShape shape, VoxelCsgOperation operation, Vector3 position, Vector3 size, float smoothness = 0.0f ) => new()
	{
		Shape = shape,
		Operation = operation,
		Position = position,
		Rotation = Rotation.Identity,
		Size = size,
		Smoothness = smoothness,
		MaterialId = 2
	};

	private static void Require( bool condition, string failure )
	{
		if ( !condition ) throw new System.InvalidOperationException( failure );
	}
}
