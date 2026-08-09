using System;

public readonly record struct VoxelClipboxPlannerProofReport(
	bool Passed,
	string Failure,
	int Cases,
	int Configurations,
	int ActiveRegularCount,
	int StableRegularSlots,
	long AllocatedBytesAfterWarmup );

internal static class VoxelClipboxPlannerProof
{
	private readonly record struct ProofPosition( int X, int Y, int Z );

	public static VoxelClipboxPlannerProofReport Run()
	{
		var configurations = 0;
		var cases = 0;
		var activeRegularCount = 0;
		var stableRegularSlots = 0;

		try
		{
			ValidateConfigurations( true, ref cases, ref configurations, ref activeRegularCount, ref stableRegularSlots );

			ValidateMovementCases( ref cases );
			ValidateReferenceEquivalence( ref cases );
			var allocatedBytesAfterWarmup = ValidateNoAllocationAfterWarmup( ref cases );
			return new VoxelClipboxPlannerProofReport( true, string.Empty, cases, configurations, activeRegularCount, stableRegularSlots, allocatedBytesAfterWarmup );
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
				default:
					throw new ArgumentException( $"Unknown clipbox planner scenario '{scenario}'.", nameof( scenario ) );
			}

			return new VoxelClipboxPlannerProofReport( true, string.Empty, cases, configurations, activeRegularCount, stableRegularSlots, 0 );
		}
		catch ( Exception exception )
		{
			return new VoxelClipboxPlannerProofReport( false, exception.Message, cases, configurations, activeRegularCount, stableRegularSlots, -1 );
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
			ValidatePlanArrays( reference, planner.DesiredLevels, planner.DesiredSlots, config );
			ValidateChangedSlots( planner );
			planner.Commit();
		}

		cases++;
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

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		var before = GC.GetAllocatedBytesForCurrentThread();
		for ( var index = 0; index < 1024; index++ )
		{
			planner.Update( new Vector3Int( NextCoordinate( ref state ), NextCoordinate( ref state ), NextCoordinate( ref state ) ) );
			planner.Commit();
		}
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
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
				if ( assignment.Key != new VoxelVisualBlockKey( assignment.Coordinate, level, config.RuleVersion, config.EditRevision ) ) throw new InvalidOperationException( $"Slot {slot} has an incorrect GPU identity." );
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
}
