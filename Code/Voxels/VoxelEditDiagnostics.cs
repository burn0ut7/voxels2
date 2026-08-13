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
				"phase5_incremental_edit_replay" => RunIncrementalEditReplay(),
				"phase5_edit_journal_bake_threshold" => RunEditJournalBakeThreshold(),
				"phase5_baked_rule_version_mismatch" => RunBakedRuleVersionMismatch(),
				"phase5_edit_persistence_roundtrip" => RunEditPersistenceRoundtrip(),
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
		var interiorEdgeEdit = Operation( VoxelEditShape.Sphere, VoxelCsgOperation.Subtract, new Vector3( 31.5f, 0, 0 ), new Vector3( 0.25f, 0, 0 ) );
		var storageChunks = VoxelEditInvalidation.GetCpuChunks( interiorEdgeEdit, 32 );
		var visualChunks = VoxelEditInvalidation.GetCpuVisualChunks( interiorEdgeEdit, 32 );
		Require( storageChunks.Contains( new Vector3Int( 0, 0, 0 ) ), "interior edit lost its owning storage chunk" );
		Require( visualChunks.Count >= storageChunks.Count && visualChunks.Contains( new Vector3Int( 1, 0, 0 ) ), "one-sample halo did not invalidate the neighbouring visual chunk" );
		return new VoxelEditProofReport( true, string.Empty, 10, 2, visual.Count + visualChunks.Count );
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
		var compactedOperations = new VoxelGpuEditOp[3];
		compactedOperations[0].WorldRevision = 80;
		compactedOperations[1].WorldRevision = 81;
		compactedOperations[2].WorldRevision = 90;
		Require( VoxelGpuEditDispatch.GetActiveOperationCount( compactedOperations, 3, 81 ) == 2, "compacted GPU journal did not resolve a world revision to the active suffix prefix" );
		return new VoxelEditProofReport( true, string.Empty, 6, 3, 1 );
	}

	private static VoxelEditProofReport RunIncrementalEditReplay()
	{
		var operations = new[]
		{
			Operation( VoxelEditShape.Sphere, VoxelCsgOperation.Subtract, new Vector3( -2, 1, 0 ), new Vector3( 4, 0, 0 ) ),
			Operation( VoxelEditShape.OrientedBox, VoxelCsgOperation.SmoothAdd, new Vector3( 3, -1, 1 ), new Vector3( 3, 2, 2 ), 1.25f ),
			Operation( VoxelEditShape.Capsule, VoxelCsgOperation.MaterialPaint, new Vector3( 2, 0, 0 ), new Vector3( 3, 4, 0 ) )
		};
		var journal = new VoxelEditJournal();
		foreach ( var operation in operations ) journal.Append( operation );
		for ( var index = 0; index < 128; index++ )
		{
			journal.Append( Operation( VoxelEditShape.Sphere, VoxelCsgOperation.Subtract, new Vector3( 1000 + index * 32, 1000, 0 ), new Vector3( 4, 0, 0 ) ) );
		}
		var replayBounds = new BBox( new Vector3( -8, -8, -6 ), new Vector3( 8, 8, 6 ) );
		var localOperations = journal.CreateOperationSnapshot( replayBounds );
		Require( localOperations.Length == operations.Length, $"spatial replay selected {localOperations.Length} operations instead of {operations.Length}" );
		for ( var index = 0; index < operations.Length; index++ )
			Require( localOperations[index].EditId == (ulong)(index + 1), "spatial replay did not preserve journal order" );
		var allOperations = journal.CreateOperationSnapshot();
		var cases = 0;
		for ( var z = -6; z <= 6; z++ )
		for ( var y = -8; y <= 8; y++ )
		for ( var x = -8; x <= 8; x++ )
		{
			var sample = new Vector3( x, y, z );
			var proceduralDistance = z - 0.25f * x;
			var incrementalDistance = proceduralDistance;
			var incrementalMaterial = proceduralDistance < 0.0f ? VoxelMaterial.Terrain : VoxelMaterial.Air;
			foreach ( var operation in localOperations )
			{
				incrementalDistance = VoxelEditJournal.ApplyDistance( operation, sample, incrementalDistance );
				var sourceMaterial = incrementalMaterial == VoxelMaterial.Air ? VoxelMaterial.Terrain : incrementalMaterial;
				incrementalMaterial = VoxelEditJournal.ApplyMaterial( operation, sample, incrementalDistance, sourceMaterial );
			}

			var replayDistance = VoxelEditJournal.EvaluateDistance( allOperations, sample, proceduralDistance );
			var replayMaterial = VoxelEditJournal.EvaluateMaterial( allOperations, sample, replayDistance, VoxelMaterial.Terrain );
			Require( incrementalDistance == replayDistance, $"incremental distance diverged at {sample}" );
			Require( incrementalMaterial == replayMaterial, $"incremental material diverged at {sample}" );
			cases++;
		}
		return new VoxelEditProofReport( true, string.Empty, cases + operations.Length, allOperations.Length, 0 );
	}

	private static VoxelEditProofReport RunEditJournalBakeThreshold()
	{
		var journal = new VoxelEditJournal();
		for ( var index = 0; index < VoxelEditJournal.DefaultBakeThreshold; index++ )
			journal.Append( Operation( VoxelEditShape.Sphere, VoxelCsgOperation.Subtract, new Vector3( 0, 0, 0 ), new Vector3( 4, 0, 0 ) ) );
		var sample = new Vector3( 0, 0, 0 );
		var before = journal.EvaluateDistance( sample, -1.0f );
		var report = journal.BakeIfNeeded( point => point.z, 1 );
		Require( report.Baked && report.BakedOperations == VoxelEditJournal.DefaultBakeThreshold - VoxelEditJournal.RetainedRecentOperations, "edit journal did not compact the oldest operations at the configured threshold" );
		Require( journal.Count == VoxelEditJournal.RetainedRecentOperations && journal.BakedBrickCount > 0, "bake did not leave a bounded recent journal and sparse bricks" );
		var after = journal.EvaluateDistance( sample, -1.0f );
		Require( System.MathF.Abs( before - after ) <= 0.00001f, $"baked replay changed authoritative SDF: before={before}, after={after}" );
		return new VoxelEditProofReport( true, string.Empty, report.BakedBrickCount * VoxelEditBrick.SampleCount, report.BakedOperations + report.RemainingOperations, report.BakedBrickCount );
	}

	private static VoxelEditProofReport RunBakedRuleVersionMismatch()
	{
		var operations = new List<VoxelEditOp>();
		for ( var index = 0; index < 4; index++ )
		{
			var operation = Operation( VoxelEditShape.Sphere, VoxelCsgOperation.Subtract, Vector3.Zero, new Vector3( 3, 0, 0 ) );
			operation.WorldRevision = (uint)(index + 1);
			operation.EditId = (ulong)(index + 1);
			operations.Add( operation );
		}
		var store = new VoxelEditBrickStore();
		var bake = store.Bake( operations, operations.Count, point => point.z, 7 );
		Require( bake.Baked, "rule-version fixture could not create a baked brick" );
		using var stream = new System.IO.MemoryStream();
		using ( var writer = new System.IO.BinaryWriter( stream, System.Text.Encoding.UTF8, true ) ) store.Write( writer );
		stream.Position = 0;
		using var reader = new System.IO.BinaryReader( stream, System.Text.Encoding.UTF8, true );
		_ = VoxelEditBrickStore.Read( reader, 8, out var mismatch );
		Require( mismatch, "baked edit bricks accepted a mismatched terrain rule version" );
		return new VoxelEditProofReport( true, string.Empty, VoxelEditBrick.SampleCount, operations.Count, bake.BakedBrickCount );
	}

	private static VoxelEditProofReport RunEditPersistenceRoundtrip()
	{
		const string path = "voxel-terrain-edits/phase5-proof-roundtrip.bin";
		try
		{
			var source = new VoxelEditJournal();
			for ( var index = 0; index < VoxelEditJournal.DefaultBakeThreshold; index++ )
				source.Append( Operation( VoxelEditShape.Sphere, VoxelCsgOperation.SmoothSubtract, new Vector3( 2, -3, 0 ), new Vector3( 4, 0, 0 ), 0.5f ) );
			source.BakeIfNeeded( point => point.z, 3 );
			var sourceDistance = source.EvaluateDistance( new Vector3( 2, -3, 0 ), -1.0f );
			var save = source.Save( path, 3 );
			Require( save.Succeeded, $"edit journal persistence save failed: {save.Failure}" );
			var loaded = new VoxelEditJournal();
			var load = loaded.Load( path, 3 );
			Require( load.Succeeded && load.Found && loaded.BakedBrickCount == source.BakedBrickCount && loaded.Count == source.Count, "edit journal persistence did not restore active and baked state" );
			var loadedDistance = loaded.EvaluateDistance( new Vector3( 2, -3, 0 ), -1.0f );
			Require( System.MathF.Abs( sourceDistance - loadedDistance ) <= 0.00001f, "edit journal persistence changed the authoritative SDF" );
			var mismatch = new VoxelEditJournal().Load( path, 4 );
			Require( mismatch.RuleVersionMismatch && !mismatch.Succeeded, "edit journal persistence accepted a rule-version mismatch" );
			return new VoxelEditProofReport( true, string.Empty, loaded.BakedBrickCount * VoxelEditBrick.SampleCount + loaded.Count, loaded.Count, loaded.BakedBrickCount );
		}
		finally
		{
			if ( FileSystem.Data.FileExists( path ) ) FileSystem.Data.DeleteFile( path );
		}
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
