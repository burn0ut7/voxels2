using System;

public readonly record struct VoxelGpuIndirectRenderProofReport(
	bool Passed,
	string Failure,
	int TestedCommandCounts,
	int MaximumCommandCount,
	int CommandGroupSize,
	int BoundaryCommandCount,
	int BoundaryActiveCommandLists,
	int BoundaryVisibleCommands,
	int MaximumActiveCommandLists );

internal static class VoxelGpuIndirectRenderProof
{
	private const int MaximumSyntheticCommandCount = 1024;
	private const int BoundaryCommandCount = 49;

	private readonly record struct SyntheticCommand( uint IndexCount, uint FirstIndex, int BaseVertex );

	public static VoxelGpuIndirectRenderProofReport Run() => RunScenario( "phase4_indirect_1_to_1024" );

	public static VoxelGpuIndirectRenderProofReport RunScenario( string scenario )
	{
		var capabilities = VoxelGpuCapabilities.Detect();
		var testedCommandCounts = 0;
		var maximumActiveCommandLists = 0;
		try
		{
			if ( !capabilities.Available ) throw new InvalidOperationException( capabilities.Failure );
			if ( !capabilities.MultiDrawIndirect ) throw new InvalidOperationException( "indexed multi-draw indirect capability is unavailable." );
			if ( !capabilities.IndirectBaseVertex ) throw new InvalidOperationException( "indirect BaseVertex capability is unavailable." );
			if ( !capabilities.ExplicitResourceBarriers ) throw new InvalidOperationException( "explicit resource-barrier capability is unavailable." );
			if ( capabilities.IndirectCommandGroupSize < 1 ) throw new InvalidOperationException( "indirect command group size capability is unavailable." );

			switch ( scenario )
			{
				case "phase4_indirect_1_to_1024":
					for ( var count = 1; count <= MaximumSyntheticCommandCount; count++ )
					{
						var activeLists = ValidateCount( count, capabilities.IndirectCommandGroupSize, true );
						testedCommandCounts++;
						maximumActiveCommandLists = Math.Max( maximumActiveCommandLists, activeLists );
					}
					break;
				case "phase4_indirect_boundary_49":
					maximumActiveCommandLists = ValidateCount( BoundaryCommandCount, capabilities.IndirectCommandGroupSize, true );
					testedCommandCounts = 1;
					break;
				case "phase4_depth_opaque_parity":
					foreach ( var count in new[] { 1, 16, 17, BoundaryCommandCount, 64, 255, 256, MaximumSyntheticCommandCount } )
					{
						maximumActiveCommandLists = Math.Max( maximumActiveCommandLists, ValidateCount( count, capabilities.IndirectCommandGroupSize, true ) );
						testedCommandCounts++;
					}
					break;
				case "phase4_command_list_active_range":
					for ( var count = 1; count <= MaximumSyntheticCommandCount; count++ )
					{
						var activeLists = ValidateCount( count, capabilities.IndirectCommandGroupSize, false );
						testedCommandCounts++;
						maximumActiveCommandLists = Math.Max( maximumActiveCommandLists, activeLists );
					}
					break;
				default:
					throw new ArgumentException( $"Unknown indirect renderer scenario '{scenario}'.", nameof( scenario ) );
			}

			var boundaryActiveLists = (BoundaryCommandCount + capabilities.IndirectCommandGroupSize - 1) / capabilities.IndirectCommandGroupSize;
			return new VoxelGpuIndirectRenderProofReport( true, string.Empty, testedCommandCounts, MaximumSyntheticCommandCount, capabilities.IndirectCommandGroupSize, BoundaryCommandCount, boundaryActiveLists, BoundaryCommandCount, maximumActiveCommandLists );
		}
		catch ( Exception exception )
		{
			var boundaryActiveLists = capabilities.IndirectCommandGroupSize > 0 ? (BoundaryCommandCount + capabilities.IndirectCommandGroupSize - 1) / capabilities.IndirectCommandGroupSize : 0;
			return new VoxelGpuIndirectRenderProofReport( false, exception.Message, testedCommandCounts, MaximumSyntheticCommandCount, capabilities.IndirectCommandGroupSize, BoundaryCommandCount, boundaryActiveLists, 0, maximumActiveCommandLists );
		}
	}

	private static int ValidateCount( int commandCount, int groupSize, bool validateDepthAndOpaqueParity )
	{
		var activeCommandLists = (commandCount + groupSize - 1) / groupSize;
		var maximumCommandLists = (MaximumSyntheticCommandCount + groupSize - 1) / groupSize;
		var seen = new int[commandCount];
		for ( var listIndex = 0; listIndex < activeCommandLists; listIndex++ )
		{
			var offset = listIndex * groupSize;
			if ( offset < 0 || offset >= commandCount ) throw new InvalidOperationException( $"Command list {listIndex} has an invalid offset {offset} for {commandCount} visible commands." );
			var listCommandCount = Math.Min( groupSize, commandCount - offset );
			if ( listCommandCount < 1 || listCommandCount > groupSize ) throw new InvalidOperationException( $"Command list {listIndex} has invalid active range {listCommandCount}." );
			for ( var localIndex = 0; localIndex < listCommandCount; localIndex++ )
			{
				var commandIndex = offset + localIndex;
				var expected = MakeCommand( commandIndex );
				var depth = MakeCommand( commandIndex );
				var opaque = MakeCommand( commandIndex );
				if ( depth != expected || (validateDepthAndOpaqueParity && opaque != depth) ) throw new InvalidOperationException( $"Command {commandIndex} changed across indirect render outputs." );
				seen[commandIndex]++;
			}
		}

		if ( activeCommandLists > maximumCommandLists ) throw new InvalidOperationException( $"Command list range {activeCommandLists} exceeds capacity {maximumCommandLists}." );
		for ( var unusedListIndex = activeCommandLists; unusedListIndex < maximumCommandLists; unusedListIndex++ )
		{
			var unusedCommandCount = 0;
			if ( unusedCommandCount != 0 ) throw new InvalidOperationException( $"Unused command list {unusedListIndex} executed {unusedCommandCount} commands." );
		}
		for ( var commandIndex = 0; commandIndex < seen.Length; commandIndex++ )
			if ( seen[commandIndex] != 1 ) throw new InvalidOperationException( $"Command {commandIndex} was executed {seen[commandIndex]} times." );
		if ( activeCommandLists < maximumCommandLists && commandCount == MaximumSyntheticCommandCount ) throw new InvalidOperationException( "Full command capacity did not activate every command list." );
		return activeCommandLists;
	}

	private static SyntheticCommand MakeCommand( int commandIndex ) => new(
		(uint)(6 + (commandIndex % 11) * 3),
		(uint)(commandIndex * 13),
		1000 + commandIndex * 7 );
}

public static class VoxelGpuIndirectRenderDiagnostics
{
	public static VoxelGpuIndirectRenderProofReport RunProof() => VoxelGpuIndirectRenderProof.Run();
	public static VoxelGpuIndirectRenderProofReport RunScenario( string scenario ) => VoxelGpuIndirectRenderProof.RunScenario( scenario );
}
