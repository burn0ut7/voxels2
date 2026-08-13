using System;

public readonly record struct VoxelClipboxPlannerProofReport(
	bool Passed,
	string Failure,
	int Cases,
	int Configurations,
	int ActiveRegularCount,
	int StableRegularSlots,
	long AllocatedBytesAfterWarmup,
	int TransitionCapacity = 0,
	int ActiveTransitionCount = 0,
	int ChangedTransitionSlots = 0,
	bool TransitionOwnershipValidated = false );

internal static class VoxelClipboxPlannerProof
{
	private readonly record struct ProofPosition( int X, int Y, int Z );

	public static VoxelClipboxPlannerProofReport Run()
	{
		var configurations = 0;
		var cases = 0;
		var activeRegularCount = 0;
		var stableRegularSlots = 0;
		var transitionCapacity = 0;
		var activeTransitionCount = 0;
		var changedTransitionSlots = 0;

		try
		{
			ValidateConfigurations( true, ref cases, ref configurations, ref activeRegularCount, ref stableRegularSlots );

			ValidateMovementCases( ref cases );
			ValidateReferenceEquivalence( ref cases );
			ValidateTransitionOwnership( ref cases, ref transitionCapacity, ref activeTransitionCount, ref changedTransitionSlots );
			var allocatedBytesAfterWarmup = ValidateNoAllocationAfterWarmup( ref cases );
			return new VoxelClipboxPlannerProofReport( true, string.Empty, cases, configurations, activeRegularCount, stableRegularSlots, allocatedBytesAfterWarmup, transitionCapacity, activeTransitionCount, changedTransitionSlots, true );
		}
		catch ( Exception exception )
		{
			return new VoxelClipboxPlannerProofReport( false, exception.Message, cases, configurations, activeRegularCount, stableRegularSlots, -1 );
		}
	}

	public static VoxelClipboxPlannerProofReport RunScenario( string scenario )
	{
		var configurations = 0;
		var cases = 0;
		var activeRegularCount = 0;
		var stableRegularSlots = 0;
		var transitionCapacity = 0;
		var activeTransitionCount = 0;
		var changedTransitionSlots = 0;

		try
		{
			switch ( scenario )
			{
				case "phase4_planner_counts":
					ValidateConfigurations( false, ref cases, ref configurations, ref activeRegularCount, ref stableRegularSlots );
					break;
				case "phase4_planner_reference_equivalence":
					ValidateReferenceEquivalence( ref cases );
					break;
				case "phase4_negative_coordinates":
				case "phase4_vertical_movement":
					ValidateMovementCases( ref cases );
					break;
				case "phase4_regular_coverage":
				case "phase4_no_lod_overlap":
				case "phase4_neighbor_difference":
					ValidateConfigurations( true, ref cases, ref configurations, ref activeRegularCount, ref stableRegularSlots );
					break;
				case "phase4_guaranteed_outer_coverage":
					ValidateGuaranteedOuterCoverage( ref cases );
					break;
				case "phase4_four_level_b4_movement":
					ValidateFourLevelStreaming( 4, ref cases, ref activeRegularCount, ref stableRegularSlots );
					break;
				case "phase4_four_level_b8_movement":
					ValidateFourLevelStreaming( 8, ref cases, ref activeRegularCount, ref stableRegularSlots );
					break;
				case "phase4_four_level_stationary_soak":
					ValidateFourLevelStationarySoak( ref cases );
					break;
				case "phase4_transition_ownership":
					ValidateTransitionOwnership( ref cases, ref transitionCapacity, ref activeTransitionCount, ref changedTransitionSlots );
					break;
				default:
					throw new ArgumentException( $"Unknown clipbox planner scenario '{scenario}'.", nameof( scenario ) );
			}

			return new VoxelClipboxPlannerProofReport( true, string.Empty, cases, configurations, activeRegularCount, stableRegularSlots, 0, transitionCapacity, activeTransitionCount, changedTransitionSlots, scenario == "phase4_transition_ownership" );
		}
		catch ( Exception exception )
		{
			return new VoxelClipboxPlannerProofReport( false, exception.Message, cases, configurations, activeRegularCount, stableRegularSlots, -1 );
		}
	}

	private static void ValidateGuaranteedOuterCoverage( ref int cases )
	{
		const int blocksPerAxis = 8;
		foreach ( var radius in new[] { 8, 9, 16, 32, 64, 128 } )
		{
			var levelCount = VoxelClipboxConfig.GetLevelCountForGuaranteedRadius( blocksPerAxis, radius );
			var config = new VoxelClipboxConfig( blocksPerAxis, levelCount );
			if ( config.GuaranteedCoverageRadius < radius ) throw new InvalidOperationException( $"B{blocksPerAxis} L{levelCount} only guarantees {config.GuaranteedCoverageRadius} chunks for requested radius {radius}." );

			var spacing = 1 << (levelCount - 1);
			var observerBaseBlock = new Vector3Int( spacing * 2 - 1, spacing * 2 - 1, spacing * 2 - 1 );
			var origin = VoxelClipboxCoordinates.GetLevelOrigin( observerBaseBlock, levelCount - 1, blocksPerAxis );
			var maximumExclusive = (origin + blocksPerAxis) * spacing;
			var leadingCoverage = System.Math.Min( maximumExclusive.x - observerBaseBlock.x, System.Math.Min( maximumExclusive.y - observerBaseBlock.y, maximumExclusive.z - observerBaseBlock.z ) );
			if ( leadingCoverage < radius ) throw new InvalidOperationException( $"B{blocksPerAxis} L{levelCount} exposes its leading edge after {leadingCoverage} chunks for requested radius {radius}." );
			cases++;
		}
	}

	private static void ValidateConfigurations( bool validateGeometry, ref int cases, ref int configurations, ref int activeRegularCount, ref int stableRegularSlots )
	{
		for ( var blocksPerAxis = VoxelClipboxConfig.MinimumBlocksPerAxis; blocksPerAxis <= VoxelClipboxConfig.MaximumBlocksPerAxis; blocksPerAxis *= 2 )
		{
			for ( var levelCount = 1; levelCount <= VoxelClipboxConfig.MaximumLevelCount; levelCount++ )
			{
				var config = new VoxelClipboxConfig( blocksPerAxis, levelCount, 17, 23 );
				var plan = VoxelClipboxReferencePlanner.Plan( new Vector3Int( 0, 0, 0 ), config );
				if ( plan.ActiveRegularCount != config.ExpectedActiveRegularCount || plan.Slots.Length != config.StableRegularSlotCount ) throw new InvalidOperationException( $"Clipbox counts are incorrect for B={blocksPerAxis}, L={levelCount}." );
				if ( validateGeometry ) ValidatePlan( plan );
				configurations++;
				cases++;
				activeRegularCount = plan.ActiveRegularCount;
				stableRegularSlots = config.StableRegularSlotCount;
			}
		}
	}

	private static void ValidateMovementCases( ref int cases )
	{
		var config = VoxelClipboxConfig.DevelopmentDefault;
		var planner = new VoxelClipboxRuntimePlanner( config );
		var positions = new[]
		{
			new ProofPosition( 0, 0, 0 ),
			new ProofPosition( 1, 0, 0 ),
			new ProofPosition( -1, 0, 0 ),
			new ProofPosition( 0, 1, 0 ),
			new ProofPosition( 0, 0, 1 ),
			new ProofPosition( -1, -1, -1 ),
			new ProofPosition( 1, 1, 1 ),
			new ProofPosition( 64, -96, 128 ),
			new ProofPosition( -65, 97, -129 )
		};

		var first = ToCanonicalSample( positions[0] );
		if ( !planner.Update( first ) ) throw new InvalidOperationException( "Initial clipbox planner update did not produce a plan." );
		ValidateChangedSlots( planner );
		planner.Commit();
		if ( planner.Update( first ) || planner.ChangedSlotCount != 0 ) throw new InvalidOperationException( "Stationary clipbox movement changed slots." );
		cases++;

		for ( var index = 1; index < positions.Length; index++ )
		{
			planner.Update( ToCanonicalSample( positions[index] ) );
			ValidateChangedSlots( planner );
			planner.Commit();
			cases++;
		}
	}

	private static void ValidateReferenceEquivalence( ref int cases )
	{
		var config = VoxelClipboxConfig.DevelopmentDefault;
		var planner = new VoxelClipboxRuntimePlanner( config );
		var state = 0x13579BDFu;

		for ( var index = 0; index < 10000; index++ )
		{
			var position = new Vector3Int( NextCoordinate( ref state ), NextCoordinate( ref state ), NextCoordinate( ref state ) );
			var reference = VoxelClipboxReferencePlanner.Plan( position, config );
			planner.Update( position );
			ValidateEquivalentPlans( reference, planner );
			ValidateChangedSlots( planner );
			planner.Commit();
		}

		cases++;
	}

	private static void ValidateFourLevelStreaming( int blocksPerAxis, ref int cases, ref int activeRegularCount, ref int stableRegularSlots )
	{
		var config = new VoxelClipboxConfig( blocksPerAxis, 4, 17, 23 );
		var planner = new VoxelClipboxRuntimePlanner( config );
		var positions = new[]
		{
			new Vector3Int( 7 * VoxelClipboxConfig.CellsPerBlock + 7, 11 * VoxelClipboxConfig.CellsPerBlock + 11, 19 * VoxelClipboxConfig.CellsPerBlock + 19 ),
			new Vector3Int( 128 + 7, -96 + 11, 64 + 19 ),
			new Vector3Int( -257, 193, -129 ),
			new Vector3Int( -2049, 1027, 3075 ),
			new Vector3Int( 0, 0, 0 )
		};

		if ( !planner.Update( positions[0] ) ) throw new InvalidOperationException( $"B{blocksPerAxis} L4 did not produce its initial plan." );
		if ( planner.ActiveRegularCount != config.ExpectedActiveRegularCount || planner.CurrentSlots.Length != config.StableRegularSlotCount ) throw new InvalidOperationException( $"B{blocksPerAxis} L4 reported incorrect active or stable counts." );
		ValidateChangedSlots( planner );
		planner.Commit();
		for ( var index = 1; index < positions.Length; index++ )
		{
			planner.Update( positions[index] );
			if ( planner.ActiveRegularCount != config.ExpectedActiveRegularCount ) throw new InvalidOperationException( $"B{blocksPerAxis} L4 changed active count during movement." );
			ValidateChangedSlots( planner );
			planner.Commit();
		}
		planner.Update( positions[0] );
		if ( planner.ChangedSlotCount == 0 ) throw new InvalidOperationException( $"B{blocksPerAxis} L4 return-to-origin did not reassign exposed slots." );
		ValidateChangedSlots( planner );
		planner.Commit();
		planner.Update( positions[0] );
		if ( planner.ChangedSlotCount != 0 ) throw new InvalidOperationException( $"B{blocksPerAxis} L4 return-to-origin remained unstable." );
		planner.Commit();
		activeRegularCount = config.ExpectedActiveRegularCount;
		stableRegularSlots = config.StableRegularSlotCount;
		cases++;
	}

	private static void ValidateFourLevelStationarySoak( ref int cases )
	{
		foreach ( var blocksPerAxis in new[] { 4, 8 } )
		{
			var config = new VoxelClipboxConfig( blocksPerAxis, 4 );
			var planner = new VoxelClipboxRuntimePlanner( config );
			var observer = new Vector3Int( 7, 11, 19 );
			planner.Update( observer );
			planner.Commit();
			for ( var frame = 0; frame < 3600; frame++ )
			{
				if ( planner.Update( observer ) || planner.ChangedSlotCount != 0 ) throw new InvalidOperationException( $"B{blocksPerAxis} L4 stationary soak produced residency work at frame {frame}." );
				planner.Commit();
			}
		}
		cases++;
	}

	private static void ValidateTransitionOwnership( ref int cases, ref int transitionCapacity, ref int activeTransitionCount, ref int changedTransitionSlots )
	{
		foreach ( var blocksPerAxis in new[] { 4, 8 } )
		foreach ( var levelCount in new[] { 2, 4 } )
		{
			var config = new VoxelClipboxConfig( blocksPerAxis, levelCount, 17, 23 );
			var planner = new VoxelClipboxRuntimePlanner( config );
			var observer = new Vector3Int( 7 * VoxelClipboxConfig.CellsPerBlock + 7, 11 * VoxelClipboxConfig.CellsPerBlock + 11, 19 * VoxelClipboxConfig.CellsPerBlock + 19 );
			if ( !planner.Update( observer ) ) throw new InvalidOperationException( $"B{blocksPerAxis} L{levelCount} transition plan did not initialize." );
			var expectedCapacity = checked( (levelCount - 1) * 6 * blocksPerAxis * blocksPerAxis );
			if ( planner.StableTransitionSlotCount != expectedCapacity || planner.DesiredTransitions.Length != expectedCapacity ) throw new InvalidOperationException( $"B{blocksPerAxis} L{levelCount} reported the wrong transition capacity." );
			if ( planner.ActiveTransitionCount <= 0 || planner.ActiveTransitionCount > expectedCapacity ) throw new InvalidOperationException( $"B{blocksPerAxis} L{levelCount} activated an invalid number of transition faces: {planner.ActiveTransitionCount}/{expectedCapacity}." );
			ValidateTransitionAssignments( planner, config );
			transitionCapacity = expectedCapacity;
			activeTransitionCount = planner.ActiveTransitionCount;
			changedTransitionSlots = System.Math.Max( changedTransitionSlots, planner.ChangedTransitionSlotCount );
			ValidateChangedTransitionSlots( planner );
			planner.Commit();
			if ( planner.Update( observer ) || planner.ChangedTransitionSlotCount != 0 ) throw new InvalidOperationException( $"B{blocksPerAxis} L{levelCount} stationary movement produced transition work." );
			planner.Commit();
			cases++;
		}
	}

	private static void ValidateTransitionAssignments( VoxelClipboxRuntimePlanner planner, VoxelClipboxConfig config )
	{
		var owners = new System.Collections.Generic.HashSet<(int Slot, VoxelClipboxFaceDirection Face)>();
		foreach ( var transition in planner.DesiredTransitions )
		{
			if ( transition.StableSlotId != VoxelClipboxTransitionPlanner.GetStableSlotId( config, transition.FineLevel, transition.Face, transition.FaceU, transition.FaceV ) ) throw new InvalidOperationException( $"Transition slot {transition.StableSlotId} is not stable." );
			if ( transition.CoarseLevel != transition.FineLevel + 1 || transition.FineLevel >= config.LevelCount - 1 ) throw new InvalidOperationException( $"Transition slot {transition.StableSlotId} has an invalid LOD pair." );
			if ( !transition.Active ) continue;
			if ( !owners.Add( (transition.FineRegularSlotId, transition.Face) ) ) throw new InvalidOperationException( $"Transition slot {transition.StableSlotId} duplicated a fine-side owner." );
			var fine = planner.DesiredSlots[transition.FineRegularSlotId];
			var coarse = planner.DesiredSlots[transition.CoarseRegularSlotId];
			if ( !fine.Active || fine.Lod != transition.FineLevel || fine.Coordinate != transition.FineCoordinate ) throw new InvalidOperationException( $"Transition slot {transition.StableSlotId} has an invalid fine owner." );
			if ( !coarse.Active || coarse.Lod != transition.CoarseLevel || coarse.Coordinate != transition.CoarseCoordinate ) throw new InvalidOperationException( $"Transition slot {transition.StableSlotId} has an invalid coarse dependency." );
			var coarseFace = VoxelClipboxTransitionPlanner.OppositeFace( transition.Face );
			if ( (coarse.Key.TransitionFaceMask & VoxelClipboxTransitionPlanner.FaceMaskBit( coarseFace )) == 0 ) throw new InvalidOperationException( $"Transition slot {transition.StableSlotId} did not reserve the touching coarse {coarseFace} boundary layer." );
			if ( IsFineNeighborActive( planner.DesiredLevels[transition.FineLevel], transition.FineCoordinate, transition.Face ) ) throw new InvalidOperationException( $"Transition slot {transition.StableSlotId} crosses an active same-LOD neighbor." );
		}
	}

	private static bool IsFineNeighborActive( VoxelClipboxLevelState level, Vector3Int coordinate, VoxelClipboxFaceDirection face )
	{
		var delta = face switch
		{
			VoxelClipboxFaceDirection.NegativeX => new Vector3Int( -1, 0, 0 ),
			VoxelClipboxFaceDirection.PositiveX => new Vector3Int( 1, 0, 0 ),
			VoxelClipboxFaceDirection.NegativeY => new Vector3Int( 0, -1, 0 ),
			VoxelClipboxFaceDirection.PositiveY => new Vector3Int( 0, 1, 0 ),
			VoxelClipboxFaceDirection.NegativeZ => new Vector3Int( 0, 0, -1 ),
			VoxelClipboxFaceDirection.PositiveZ => new Vector3Int( 0, 0, 1 ),
			_ => throw new ArgumentOutOfRangeException( nameof( face ) )
		};
		return level.IsActive( coordinate + delta );
	}

	private static void ValidateChangedTransitionSlots( VoxelClipboxRuntimePlanner planner )
	{
		for ( var index = 0; index < planner.ChangedTransitionSlotCount; index++ )
		{
			var slotId = planner.GetChangedTransitionSlotId( index );
			var current = planner.CurrentTransitions[slotId];
			var desired = planner.DesiredTransitions[slotId];
			if ( !current.Active && !desired.Active ) throw new InvalidOperationException( $"Inactive transition slot {slotId} was reported as changed." );
			if ( current == desired ) throw new InvalidOperationException( $"Unchanged transition slot {slotId} was reported as changed." );
		}
	}

	private static long ValidateNoAllocationAfterWarmup( ref int cases )
	{
		var config = VoxelClipboxConfig.FastCorrectness;
		var planner = new VoxelClipboxRuntimePlanner( config );
		var state = 0x2468ACE1u;
		for ( var index = 0; index < 16; index++ )
		{
			planner.Update( new Vector3Int( NextCoordinate( ref state ), NextCoordinate( ref state ), NextCoordinate( ref state ) ) );
			planner.Commit();
		}

		var before = Sandbox.Diagnostics.PerformanceStats.BytesAllocated;
		for ( var index = 0; index < 1024; index++ )
		{
			planner.Update( new Vector3Int( NextCoordinate( ref state ), NextCoordinate( ref state ), NextCoordinate( ref state ) ) );
			planner.Commit();
		}
		var allocated = System.Math.Max( 0L, Sandbox.Diagnostics.PerformanceStats.BytesAllocated - before );
		if ( allocated != 0 ) throw new InvalidOperationException( $"Runtime clipbox planner allocated {allocated} bytes after warm-up." );
		cases++;
		return allocated;
	}

	private static void ValidatePlan( VoxelClipboxPlan plan )
	{
		var config = plan.Config;
		if ( plan.ActiveRegularCount != config.ExpectedActiveRegularCount ) throw new InvalidOperationException( $"Expected {config.ExpectedActiveRegularCount} active regular blocks, got {plan.ActiveRegularCount}." );
		if ( plan.Slots.Length != config.StableRegularSlotCount ) throw new InvalidOperationException( "Clipbox stable slot count is incorrect." );
		ValidatePlanArrays( plan, plan.Levels, plan.Slots, config );
		ValidateCoverageAndNeighbors( plan );
	}

	private static void ValidatePlanArrays( VoxelClipboxPlan plan, VoxelClipboxLevelState[] levels, VoxelClipboxRegularSlotAssignment[] slots, VoxelClipboxConfig config )
	{
		var activeCount = 0;
		var activeCoordinates = new System.Collections.Generic.HashSet<(int Lod, Vector3Int Coordinate)>();
		for ( var level = 0; level < config.LevelCount; level++ )
		{
			var state = levels[level];
			if ( state.Level != level || state.StableSlotBase != level * config.BlocksPerAxis * config.BlocksPerAxis * config.BlocksPerAxis ) throw new InvalidOperationException( $"Level {level} has an invalid stable slot range." );
			if ( state.Outer.SizeX != config.BlocksPerAxis || state.Outer.SizeY != config.BlocksPerAxis || state.Outer.SizeZ != config.BlocksPerAxis ) throw new InvalidOperationException( $"Level {level} outer bounds are not {config.BlocksPerAxis} blocks wide." );
			if ( level > 0 )
			{
				var parent = levels[level - 1];
				var expectedInner = new VoxelClipboxBounds( parent.Origin / 2, (parent.Origin + config.BlocksPerAxis) / 2 );
				if ( state.Inner != expectedInner ) throw new InvalidOperationException( $"Level {level} is not aligned with its parent." );
				if ( state.Inner.SizeX != config.BlocksPerAxis / 2 || state.Inner.SizeY != config.BlocksPerAxis / 2 || state.Inner.SizeZ != config.BlocksPerAxis / 2 ) throw new InvalidOperationException( $"Level {level} inner bounds have the wrong size." );
			}

			var levelActiveCount = 0;
			for ( var slot = state.StableSlotBase; slot < state.StableSlotBase + state.StableSlotCount; slot++ )
			{
				var assignment = slots[slot];
				if ( assignment.StableSlotId != slot || assignment.Lod != level ) throw new InvalidOperationException( $"Slot {slot} has an invalid identity." );
				var expectedSlot = VoxelClipboxCoordinates.GetSlotIndex( level, assignment.Coordinate, config.BlocksPerAxis );
				if ( expectedSlot != slot ) throw new InvalidOperationException( $"Slot {slot} does not match its toroidal coordinate." );
				var expectedActive = state.IsActive( assignment.Coordinate );
				if ( assignment.Active != expectedActive ) throw new InvalidOperationException( $"Slot {slot} has an incorrect active mask." );
				var expectedMask = VoxelClipboxTransitionPlanner.GetCoarseFaceMask( config, levels, assignment.Coordinate, level );
				if ( assignment.Key != new VoxelVisualBlockKey( assignment.Coordinate, level, config.RuleVersion, config.EditRevision, TransitionFaceMask: expectedMask ) ) throw new InvalidOperationException( $"Slot {slot} has an incorrect GPU identity or transition mask." );
				if ( assignment.Active )
				{
					levelActiveCount++;
					if ( !activeCoordinates.Add( (level, assignment.Coordinate) ) ) throw new InvalidOperationException( $"Active clipbox coordinates overlap at LOD {level}, {assignment.Coordinate}." );
				}
			}

			if ( state.ActiveCount != levelActiveCount ) throw new InvalidOperationException( $"Level {level} reports {state.ActiveCount} active blocks, counted {levelActiveCount}." );
			activeCount += levelActiveCount;
		}

		if ( activeCount != plan.ActiveRegularCount ) throw new InvalidOperationException( "Plan active count does not match its slots." );
	}

	internal static void ValidateEquivalentPlans( VoxelClipboxPlan reference, VoxelClipboxRuntimePlanner planner )
	{
		if ( reference.ActiveRegularCount != planner.ActiveRegularCount || reference.Levels.Length != planner.DesiredLevels.Length || reference.Slots.Length != planner.DesiredSlots.Length )
			throw new InvalidOperationException( "Runtime planner counts differ from the reference plan." );
		for ( var index = 0; index < reference.Levels.Length; index++ )
			if ( reference.Levels[index] != planner.DesiredLevels[index] ) throw new InvalidOperationException( $"Runtime planner level {index} differs from the reference plan." );
		for ( var index = 0; index < reference.Slots.Length; index++ )
			if ( reference.Slots[index] != planner.DesiredSlots[index] ) throw new InvalidOperationException( $"Runtime planner slot {index} differs from the reference plan." );
	}

	private static void ValidateCoverageAndNeighbors( VoxelClipboxPlan plan )
	{
		var config = plan.Config;
		var outermost = plan.Levels[config.LevelCount - 1];
		var spacing = 1 << (config.LevelCount - 1);
		var extent = checked( config.BlocksPerAxis * spacing );
		var coverage = new int[checked( extent * extent * extent )];
		Array.Fill( coverage, -1 );

		for ( var slot = 0; slot < plan.Slots.Length; slot++ )
		{
			var assignment = plan.Slots[slot];
			if ( !assignment.Active ) continue;
			var blockSpacing = 1 << assignment.Lod;
			var min = assignment.Coordinate * blockSpacing;
			var localMin = (min - outermost.Origin * spacing) / 1;
			for ( var z = 0; z < blockSpacing; z++ )
			for ( var y = 0; y < blockSpacing; y++ )
			for ( var x = 0; x < blockSpacing; x++ )
			{
				var localX = localMin.x + x;
				var localY = localMin.y + y;
				var localZ = localMin.z + z;
				if ( localX < 0 || localX >= extent || localY < 0 || localY >= extent || localZ < 0 || localZ >= extent ) throw new InvalidOperationException( $"LOD {assignment.Lod} block {assignment.Coordinate} falls outside the outermost clipbox." );
				var coverageIndex = localX + extent * (localY + extent * localZ);
				if ( coverage[coverageIndex] != -1 ) throw new InvalidOperationException( $"Regular clipbox volumes overlap at local cell ({localX},{localY},{localZ})." );
				coverage[coverageIndex] = assignment.Lod;
			}
		}

		for ( var z = 0; z < extent; z++ )
		for ( var y = 0; y < extent; y++ )
		for ( var x = 0; x < extent; x++ )
		{
			var index = x + extent * (y + extent * z);
			if ( coverage[index] == -1 ) throw new InvalidOperationException( $"Regular clipbox coverage has a hole at local cell ({x},{y},{z})." );
			if ( x + 1 < extent ) ValidateNeighbor( coverage[index], coverage[index + 1] );
			if ( y + 1 < extent ) ValidateNeighbor( coverage[index], coverage[index + extent] );
			if ( z + 1 < extent ) ValidateNeighbor( coverage[index], coverage[index + extent * extent] );
		}
	}

	private static void ValidateNeighbor( int firstLod, int secondLod )
	{
		if ( Math.Abs( firstLod - secondLod ) > 1 ) throw new InvalidOperationException( $"Neighboring regular blocks differ by LOD {firstLod} and {secondLod}." );
	}

	private static void ValidateChangedSlots( VoxelClipboxRuntimePlanner planner )
	{
		for ( var index = 0; index < planner.ChangedSlotCount; index++ )
		{
			var slotId = planner.GetChangedSlotId( index );
			var current = planner.CurrentSlots[slotId];
			var desired = planner.DesiredSlots[slotId];
			if ( !current.Active && !desired.Active ) throw new InvalidOperationException( $"Inactive slot {slotId} was reported as changed." );
			if ( current == desired ) throw new InvalidOperationException( $"Unchanged slot {slotId} was reported as changed." );
		}
	}

	private static Vector3Int ToCanonicalSample( ProofPosition position ) => new(
		checked( position.X * VoxelClipboxConfig.CellsPerBlock + 7 ),
		checked( position.Y * VoxelClipboxConfig.CellsPerBlock + 11 ),
		checked( position.Z * VoxelClipboxConfig.CellsPerBlock + 19 ) );

	private static int NextCoordinate( ref uint state )
	{
		state = unchecked( state * 1664525u + 1013904223u );
		return (int)(state % 20001u) - 10000;
	}
}

public static class VoxelClipboxDiagnostics
{
	public static VoxelClipboxPlannerProofReport RunPlannerProof() => VoxelClipboxPlannerProof.Run();
	public static VoxelClipboxPlannerProofReport RunPlannerScenario( string scenario ) => VoxelClipboxPlannerProof.RunScenario( scenario );
	internal static VoxelClipboxReferenceEquivalenceRunner StartReferenceEquivalence() => new();
}

internal sealed class VoxelClipboxReferenceEquivalenceRunner
{
	private const int TotalPositions = 10_000;
	private readonly VoxelClipboxConfig _config = VoxelClipboxConfig.DevelopmentDefault;
	private readonly VoxelClipboxRuntimePlanner _planner;
	private uint _state = 0x13579BDFu;
	private int _positionIndex;
	private VoxelClipboxPlannerProofReport _report;

	public bool IsComplete { get; private set; }
	public VoxelClipboxPlannerProofReport Report => _report;

	public VoxelClipboxReferenceEquivalenceRunner()
	{
		_planner = new VoxelClipboxRuntimePlanner( _config );
	}

	public void Step( double budgetMilliseconds )
	{
		if ( IsComplete ) return;
		var started = System.Diagnostics.Stopwatch.GetTimestamp();
		try
		{
			do
			{
				var position = new Vector3Int( NextCoordinate(), NextCoordinate(), NextCoordinate() );
				var reference = VoxelClipboxReferencePlanner.Plan( position, _config );
				_planner.Update( position );
				VoxelClipboxPlannerProof.ValidateEquivalentPlans( reference, _planner );
				ValidateChangedSlots( _planner );
				_planner.Commit();
				_positionIndex++;
			}
			while ( _positionIndex < TotalPositions && System.Diagnostics.Stopwatch.GetElapsedTime( started ).TotalMilliseconds < budgetMilliseconds );

			if ( _positionIndex >= TotalPositions )
			{
				_report = new VoxelClipboxPlannerProofReport( true, string.Empty, 1, 0, 0, 0, 0 );
				IsComplete = true;
			}
		}
		catch ( Exception exception )
		{
			_report = new VoxelClipboxPlannerProofReport( false, exception.Message, 0, 0, 0, 0, -1 );
			IsComplete = true;
		}
	}

	private int NextCoordinate()
	{
		_state = unchecked( _state * 1664525u + 1013904223u );
		return (int)(_state % 20001u) - 10000;
	}

	private static void ValidateChangedSlots( VoxelClipboxRuntimePlanner planner )
	{
		for ( var index = 0; index < planner.ChangedSlotCount; index++ )
		{
			var slotId = planner.GetChangedSlotId( index );
			var current = planner.CurrentSlots[slotId];
			var desired = planner.DesiredSlots[slotId];
			if ( !current.Active && !desired.Active ) throw new InvalidOperationException( $"Inactive slot {slotId} was reported as changed." );
			if ( current == desired ) throw new InvalidOperationException( $"Unchanged slot {slotId} was reported as changed." );
		}
	}

}
