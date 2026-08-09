internal readonly record struct VoxelGpuPhase4ProofResult( bool Passed, int Levels, int Residents, int Transitions, string Failure );

internal static class VoxelGpuPhase4Proof
{
	public static VoxelGpuPhase4ProofResult ValidateClipbox( int radius, int levelCount, int ruleVersion )
	{
		try
		{
			if ( VoxelTransvoxelTables.RegularCellClass.Length != 256 || VoxelTransvoxelTables.RegularGeometryCounts.Length != 16 ||
				VoxelTransvoxelTables.RegularVertexData.Length != 256 * 12 )
				return new( false, 0, 0, 0, "regular lookup tables are incomplete" );
			for ( var regularCase = 0; regularCase < 256; regularCase++ )
			{
				var regularClass = VoxelTransvoxelTables.RegularCellClass[regularCase];
				var geometry = VoxelTransvoxelTables.RegularGeometryCounts[regularClass];
				if ( (geometry >> 4) > 12 || (geometry & 0x0F) > 5 )
					return new( false, 0, 0, 0, $"regular case {regularCase} has invalid geometry counts" );
			}
			if ( VoxelTransvoxelTransitionTables.CellClass.Length != 512 ||
				VoxelTransvoxelTransitionTables.CornerData.Length != 13 ||
				VoxelTransvoxelTransitionTables.VertexData.Length != 512 * 12 ||
				VoxelTransvoxelTransitionTables.CellData.Length != 56 )
				return new( false, 0, 0, 0, "transition lookup tables are incomplete" );
			for ( var caseCode = 0; caseCode < VoxelTransvoxelTransitionTables.CellClass.Length; caseCode++ )
			{
				var transitionClass = VoxelTransvoxelTransitionTables.CellClass[caseCode] & 0x7F;
				var cell = VoxelTransvoxelTransitionTables.CellData[transitionClass];
				if ( cell.VertexCount > 12 || cell.TriangleCount > 12 )
					return new( false, 0, 0, 0, $"transition case {caseCode} has invalid geometry counts" );
				foreach ( var index in cell.TriangleIndices )
					if ( index >= 12 ) return new( false, 0, 0, 0, $"transition case {caseCode} references vertex {index}" );
				for ( var vertex = 0; vertex < cell.VertexCount; vertex++ )
				{
					var edgeCode = VoxelTransvoxelTransitionTables.VertexData[caseCode * 12 + vertex] & 0xFF;
					if ( (edgeCode >> 4) >= 13 || (edgeCode & 0x0F) >= 13 )
						return new( false, 0, 0, 0, $"transition case {caseCode} references invalid corner edge 0x{edgeCode:X2}" );
				}
				if ( cell.TriangleIndices.Length != cell.TriangleCount * 3 )
					return new( false, 0, 0, 0, $"transition case {caseCode} has a truncated triangle list" );
			}
			if ( !ValidateTransitionOrientations( out var orientationFailure ) ) return new( false, 0, 0, 0, orientationFailure );
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
			var sameLodPlanner = new VoxelClipboxLodPlanner( radius, 1 );
			var sameLodDesired = new System.Collections.Generic.HashSet<VoxelVisualBlockKey>();
			sameLodPlanner.Plan( Vector3Int.Zero, ruleVersion, sameLodDesired );
			if ( sameLodPlanner.Transitions.Count != 0 )
				return new( false, planner.Levels.Count, count, planner.Transitions.Count, "single-LOD clipbox produced transition faces" );
			var stationaryEntering = new System.Collections.Generic.List<VoxelVisualBlockKey>();
			var stationaryLeaving = new System.Collections.Generic.List<VoxelVisualBlockKey>();
			planner.PlanDelta( Vector3Int.Zero, ruleVersion, desired, stationaryEntering, stationaryLeaving );
			if ( stationaryEntering.Count != 0 || stationaryLeaving.Count != 0 )
				return new( false, planner.Levels.Count, count, planner.Transitions.Count, "stationary observer rebuilt clipbox residents" );

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
			foreach ( var key in desired )
			{
				if ( key.Lod == 0 ) continue;
				var level = planner.Levels[key.Lod];
				if ( VoxelClipboxLodPlanner.IsCoveredByFinerLevel( key.Coordinate, Vector3Int.Zero, level ) )
					return new( false, planner.Levels.Count, count, planner.Transitions.Count, $"LOD{key.Lod} resident overlaps its finer clipbox shell at {key.Coordinate}" );
			}

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

	private static bool ValidateTransitionOrientations( out string failure )
	{
		var faces = System.Enum.GetValues<VoxelLodFaceDirection>();
		foreach ( var face in faces )
		{
			var basis = GetFaceBasis( face );
			var determinant = Vector3.Dot( Vector3.Cross( basis.U, basis.V ), basis.W );
			if ( System.MathF.Abs( determinant ) != 1.0f )
			{
				failure = $"transition face {face} has a non-orthonormal orientation";
				return false;
			}
			for ( var caseCode = 0; caseCode < VoxelTransvoxelTransitionTables.CellClass.Length; caseCode++ )
			{
				var cell = VoxelTransvoxelTransitionTables.CellData[VoxelTransvoxelTransitionTables.CellClass[caseCode] & 0x7F];
				for ( var vertex = 0; vertex < cell.VertexCount; vertex++ )
				{
					var edgeCode = VoxelTransvoxelTransitionTables.VertexData[caseCode * 12 + vertex];
					var first = edgeCode & 0x0F;
					var second = (edgeCode >> 4) & 0x0F;
					if ( first >= 13 || second >= 13 || first == second )
					{
						failure = $"transition face {face} has invalid corner edge in case {caseCode}";
						return false;
					}
					var firstPosition = TransformCorner( first, basis );
					var secondPosition = TransformCorner( second, basis );
					if ( (firstPosition - secondPosition).LengthSquared <= 0.0f )
					{
						failure = $"transition face {face} collapsed case {caseCode} edge {vertex}";
						return false;
					}
				}
			}
		}
		failure = string.Empty;
		return true;
	}

	private static Vector3 TransformCorner( int corner, (Vector3 U, Vector3 V, Vector3 W) basis )
	{
		var position = corner switch
		{
			< 9 => new Vector3( corner % 3, corner / 3, 0 ),
			9 => new Vector3( 0, 0, 2 ),
			10 => new Vector3( 2, 0, 2 ),
			11 => new Vector3( 0, 2, 2 ),
			_ => new Vector3( 2, 2, 2 )
		};
		return basis.U * position.x + basis.V * position.y + basis.W * position.z;
	}

	private static (Vector3 U, Vector3 V, Vector3 W) GetFaceBasis( VoxelLodFaceDirection face ) => face switch
	{
		VoxelLodFaceDirection.NegativeX => (new Vector3( 0, 0, -1 ), new Vector3( 0, 1, 0 ), new Vector3( 1, 0, 0 )),
		VoxelLodFaceDirection.PositiveX => (new Vector3( 0, 0, 1 ), new Vector3( 0, 1, 0 ), new Vector3( -1, 0, 0 )),
		VoxelLodFaceDirection.NegativeY => (new Vector3( 1, 0, 0 ), new Vector3( 0, 0, -1 ), new Vector3( 0, 1, 0 )),
		VoxelLodFaceDirection.PositiveY => (new Vector3( 1, 0, 0 ), new Vector3( 0, 0, 1 ), new Vector3( 0, -1, 0 )),
		VoxelLodFaceDirection.NegativeZ => (new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ), new Vector3( 0, 0, 1 )),
		_ => (new Vector3( -1, 0, 0 ), new Vector3( 0, 1, 0 ), new Vector3( 0, 0, -1 ))
	};
}
