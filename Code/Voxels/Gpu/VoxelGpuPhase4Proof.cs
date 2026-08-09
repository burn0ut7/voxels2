internal readonly record struct VoxelGpuPhase4ProofResult( bool Passed, int Levels, int Residents, int Transitions, string Failure );

internal static class VoxelGpuPhase4Proof
{
	public static VoxelGpuPhase4ProofResult ValidateClipbox( int radius, int levelCount, int ruleVersion )
	{
		try
		{
			var planner = new VoxelClipboxLodPlanner( radius, levelCount );
			var desired = new System.Collections.Generic.HashSet<VoxelVisualBlockKey>();
			var count = planner.Plan( Vector3Int.Zero, ruleVersion, desired );
			if ( count == 0 ) return new( false, planner.Levels.Count, 0, 0, "clipbox planner produced no residents" );
			for ( var index = 0; index < planner.Levels.Count; index++ )
			{
				var level = planner.Levels[index];
				if ( level.Lod != index || level.SampleSpacing != (1 << index) )
					return new( false, planner.Levels.Count, count, planner.Transitions.Count, "LOD spacing is not a power-of-two sequence" );
			}

			var transitionOwners = new System.Collections.Generic.HashSet<(VoxelVisualBlockKey Fine, VoxelLodFaceDirection Face)>();
			var transitionFaces = new System.Collections.Generic.HashSet<VoxelLodFaceDirection>();
			foreach ( var transition in planner.Transitions )
			{
				if ( transition.Fine.Lod + 1 != transition.Coarse.Lod )
					return new( false, planner.Levels.Count, count, planner.Transitions.Count, "transition spans more than one LOD" );
				if ( !transitionOwners.Add( (transition.Fine, transition.Face) ) )
					return new( false, planner.Levels.Count, count, planner.Transitions.Count, "transition face has duplicate ownership" );
				transitionFaces.Add( transition.Face );
			}
			if ( planner.Levels.Count > 1 && transitionFaces.Count != 6 )
				return new( false, planner.Levels.Count, count, planner.Transitions.Count, "clipbox did not exercise all six transition orientations" );

			var entering = new System.Collections.Generic.List<VoxelVisualBlockKey>();
			var leaving = new System.Collections.Generic.List<VoxelVisualBlockKey>();
			var maximumLevelVolume = planner.Levels.Max( level => level.Dimension * level.Dimension * level.Dimension );
			var movedCount = 0;
			var observerSequence = new[]
			{
				new Vector3Int( 1, 1, 1 ), new Vector3Int( 2, 1, 1 ), new Vector3Int( 2, 2, 1 ),
				new Vector3Int( -2, 2, 1 ), new Vector3Int( -2, -2, 1 ), new Vector3Int( 3, -3, 4 ),
				new Vector3Int( 4, -4, 8 ), new Vector3Int( 0, 0, 0 )
			};
			for ( var sequenceIndex = 0; sequenceIndex < observerSequence.Length; sequenceIndex++ )
			{
				var observer = observerSequence[sequenceIndex];
				entering.Clear();
				leaving.Clear();
				movedCount = planner.PlanDelta( observer, ruleVersion, desired, entering, leaving );
				var referencePlanner = new VoxelClipboxLodPlanner( radius, levelCount );
				var reference = new System.Collections.Generic.HashSet<VoxelVisualBlockKey>();
				referencePlanner.Plan( observer, ruleVersion, reference );
				if ( movedCount != reference.Count || !desired.SetEquals( reference ) )
				{
					var missing = reference.Count( key => !desired.Contains( key ) );
					var extra = desired.Count( key => !reference.Contains( key ) );
					var missingKey = reference.FirstOrDefault( key => !desired.Contains( key ) );
					var extraKey = desired.FirstOrDefault( key => !reference.Contains( key ) );
					return new( false, planner.Levels.Count, movedCount, planner.Transitions.Count, $"slab reassignment diverged from a fresh clipbox plan at sequence {sequenceIndex} (missing={missing}, extra={extra}, missingKey={missingKey}, extraKey={extraKey})" );
				}
				if ( sequenceIndex > 0 && observer - observerSequence[sequenceIndex - 1] == new Vector3Int( 1, 1, 1 ) &&
					(entering.Count >= maximumLevelVolume || leaving.Count >= maximumLevelVolume) )
					return new( false, planner.Levels.Count, movedCount, planner.Transitions.Count, "ordinary movement rebuilt an entire clipbox level" );
			}

			var transitionResidency = new VoxelLodTransitionResidency();
			transitionResidency.Update( planner.Transitions );
			if ( !transitionResidency.TryPublishCoherent( desired ) || !transitionResidency.IsCoherent )
				return new( false, planner.Levels.Count, movedCount, planner.Transitions.Count, "transition publication was not coherent with regular residents" );
			if ( transitionResidency.Entering.Count == 0 && transitionResidency.DesiredCount != 0 )
				return new( false, planner.Levels.Count, movedCount, planner.Transitions.Count, "transition set did not report its initial entering work" );
			if ( transitionResidency.DesiredCount > 0 )
			{
				var incomplete = new System.Collections.Generic.HashSet<VoxelVisualBlockKey>( desired );
				var dependency = planner.Transitions.First();
				incomplete.Remove( dependency.Fine );
				var publishedBefore = transitionResidency.PublishedCount;
				if ( transitionResidency.TryPublishCoherent( incomplete ) || transitionResidency.PublishedCount != publishedBefore )
					return new( false, planner.Levels.Count, movedCount, planner.Transitions.Count, "partial regular publication replaced a coherent transition set" );
				if ( !transitionResidency.TryPublishCoherent( desired ) || !transitionResidency.IsCoherent )
					return new( false, planner.Levels.Count, movedCount, planner.Transitions.Count, "transition set did not recover after dependencies became resident" );
			}

			var teleportEntering = new System.Collections.Generic.List<VoxelVisualBlockKey>();
			var teleportLeaving = new System.Collections.Generic.List<VoxelVisualBlockKey>();
			planner.PlanDelta( new Vector3Int( radius * 8, radius * 8, radius * 8 ), ruleVersion, desired, teleportEntering, teleportLeaving );
			if ( teleportEntering.Count == 0 || teleportLeaving.Count == 0 )
				return new( false, planner.Levels.Count, desired.Count, planner.Transitions.Count, "teleport did not invalidate and repopulate clipbox slots" );

			return new( true, planner.Levels.Count, count, planner.Transitions.Count, string.Empty );
		}
		catch ( System.Exception exception )
		{
			return new( false, 0, 0, 0, exception.Message );
		}
	}
}
