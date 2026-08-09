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
			foreach ( var transition in planner.Transitions )
			{
				if ( transition.Fine.Lod + 1 != transition.Coarse.Lod )
					return new( false, planner.Levels.Count, count, planner.Transitions.Count, "transition spans more than one LOD" );
				if ( !transitionOwners.Add( (transition.Fine, transition.Face) ) )
					return new( false, planner.Levels.Count, count, planner.Transitions.Count, "transition face has duplicate ownership" );
			}

			return new( true, planner.Levels.Count, count, planner.Transitions.Count, string.Empty );
		}
		catch ( System.Exception exception )
		{
			return new( false, 0, 0, 0, exception.Message );
		}
	}
}
