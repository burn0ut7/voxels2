internal static class VoxelClipboxReferencePlanner
{
	public static VoxelClipboxPlan Plan( Vector3Int observerCanonicalSample, VoxelClipboxConfig config )
	{
		var observerBaseBlock = VoxelClipboxCoordinates.GetObserverBaseBlock( observerCanonicalSample );
		var levels = new VoxelClipboxLevelState[config.LevelCount];
		var slots = new VoxelClipboxRegularSlotAssignment[config.StableRegularSlotCount];
		var activeCount = Populate( observerBaseBlock, config, levels, slots );
		return new VoxelClipboxPlan( config, observerBaseBlock, levels, slots, activeCount );
	}

	internal static int Populate( Vector3Int observerBaseBlock, VoxelClipboxConfig config, VoxelClipboxLevelState[] levels, VoxelClipboxRegularSlotAssignment[] slots )
	{
		if ( levels.Length != config.LevelCount ) throw new System.ArgumentException( "Level storage does not match the clipbox configuration.", nameof( levels ) );
		if ( slots.Length != config.StableRegularSlotCount ) throw new System.ArgumentException( "Slot storage does not match the clipbox configuration.", nameof( slots ) );

		var activeCount = 0;
		for ( var level = 0; level < config.LevelCount; level++ )
		{
			var origin = VoxelClipboxCoordinates.GetLevelOrigin( observerBaseBlock, level, config.BlocksPerAxis );
			var outer = new VoxelClipboxBounds( origin, origin + config.BlocksPerAxis );
			var inner = default( VoxelClipboxBounds );
			if ( level > 0 )
			{
				var parentOrigin = levels[level - 1].Origin;
				if ( !VoxelClipboxCoordinates.IsExactlyDivisible( parentOrigin, 2 ) || !VoxelClipboxCoordinates.IsExactlyDivisible( parentOrigin + config.BlocksPerAxis, 2 ) )
					throw new System.InvalidOperationException( $"Clipbox level {level} boundary is not aligned to its parent grid." );
				inner = new VoxelClipboxBounds( parentOrigin / 2, (parentOrigin + config.BlocksPerAxis) / 2 );
			}
			var levelBase = level * config.BlocksPerAxis * config.BlocksPerAxis * config.BlocksPerAxis;
			var state = new VoxelClipboxLevelState( level, origin, outer, inner, 0, levelBase, 1 << level );
			levels[level] = state;

			var levelActiveCount = 0;
			for ( var z = 0; z < config.BlocksPerAxis; z++ )
			for ( var y = 0; y < config.BlocksPerAxis; y++ )
			for ( var x = 0; x < config.BlocksPerAxis; x++ )
			{
				var coordinate = origin + new Vector3Int( x, y, z );
				var slotId = VoxelClipboxCoordinates.GetSlotIndex( level, coordinate, config.BlocksPerAxis );
				var active = state.IsActive( coordinate );
				slots[slotId] = new VoxelClipboxRegularSlotAssignment(
					slotId, level, coordinate, active,
					new VoxelVisualBlockKey( coordinate, level, config.RuleVersion, config.EditRevision ) );
				if ( active )
				{
					levelActiveCount++;
					activeCount++;
				}
			}
			levels[level] = state with { ActiveCount = levelActiveCount };
		}

		VoxelClipboxTransitionPlanner.ApplyCoarseFaceMasks( config, levels, slots );

		return activeCount;
	}
}
