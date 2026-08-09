internal enum VoxelClipboxFaceDirection
{
	NegativeX,
	PositiveX,
	NegativeY,
	PositiveY,
	NegativeZ,
	PositiveZ
}

internal readonly record struct VoxelClipboxTransitionSlotAssignment(
	int StableSlotId,
	int FineLevel,
	int CoarseLevel,
	int FineRegularSlotId,
	int CoarseRegularSlotId,
	Vector3Int FineCoordinate,
	Vector3Int CoarseCoordinate,
	VoxelClipboxFaceDirection Face,
	int FaceU,
	int FaceV,
	bool Active,
	ushort RuleVersion,
	uint EditRevision,
	VoxelVisualBlockKey Key,
	VoxelVisualBlockKey CoarseKey );

internal static class VoxelClipboxTransitionPlanner
{
	public static VoxelVisualBlockKey GetVisualKey( VoxelClipboxTransitionSlotAssignment assignment ) =>
		new( assignment.FineCoordinate, assignment.FineLevel, assignment.RuleVersion, assignment.EditRevision, assignment.StableSlotId );

	public static int GetStableSlotId( VoxelClipboxConfig config, int fineLevel, VoxelClipboxFaceDirection face, int u, int v ) =>
		checked( fineLevel * 6 * config.BlocksPerAxis * config.BlocksPerAxis + (int)face * config.BlocksPerAxis * config.BlocksPerAxis + u + config.BlocksPerAxis * v );

	public static int Populate( VoxelClipboxConfig config, VoxelClipboxLevelState[] levels, VoxelClipboxTransitionSlotAssignment[] destination )
	{
		var faceCellCount = checked( config.BlocksPerAxis * config.BlocksPerAxis );
		var expectedCapacity = config.StableTransitionSlotCount;
		if ( levels.Length != config.LevelCount ) throw new System.ArgumentException( "Level storage does not match the clipbox configuration.", nameof( levels ) );
		if ( destination.Length != expectedCapacity ) throw new System.ArgumentException( "Transition storage does not match the clipbox configuration.", nameof( destination ) );

		var activeCount = 0;
		for ( var fineLevel = 0; fineLevel < config.LevelCount - 1; fineLevel++ )
		{
			var fine = levels[fineLevel];
			var coarse = levels[fineLevel + 1];
			for ( var faceIndex = 0; faceIndex < 6; faceIndex++ )
			for ( var v = 0; v < config.BlocksPerAxis; v++ )
			for ( var u = 0; u < config.BlocksPerAxis; u++ )
			{
				var face = (VoxelClipboxFaceDirection)faceIndex;
				var fineCoordinate = GetFaceCoordinate( fine.Outer, face, u, v );
				var neighborCoordinate = fineCoordinate + GetFaceDelta( face );
				var coarseCoordinate = VoxelClipboxCoordinates.FloorDiv( neighborCoordinate, 2 );
				var active = fine.IsActive( fineCoordinate ) && !fine.IsActive( neighborCoordinate ) && coarse.IsActive( coarseCoordinate );
				var stableSlotId = GetStableSlotId( config, fineLevel, face, u, v );
				destination[stableSlotId] = new VoxelClipboxTransitionSlotAssignment(
					stableSlotId,
					fineLevel,
					fineLevel + 1,
					VoxelClipboxCoordinates.GetSlotIndex( fineLevel, fineCoordinate, config.BlocksPerAxis ),
					VoxelClipboxCoordinates.GetSlotIndex( fineLevel + 1, coarseCoordinate, config.BlocksPerAxis ),
					fineCoordinate,
					coarseCoordinate,
					face,
					u,
					v,
					active,
					config.RuleVersion,
					config.EditRevision,
					new VoxelVisualBlockKey( fineCoordinate, fineLevel, config.RuleVersion, config.EditRevision ),
					new VoxelVisualBlockKey( coarseCoordinate, fineLevel + 1, config.RuleVersion, config.EditRevision ) );
				if ( active ) activeCount++;
			}
		}

		return activeCount;
	}

	private static Vector3Int GetFaceCoordinate( VoxelClipboxBounds outer, VoxelClipboxFaceDirection face, int u, int v ) => face switch
	{
		VoxelClipboxFaceDirection.NegativeX => new Vector3Int( outer.Min.x, outer.Min.y + u, outer.Min.z + v ),
		VoxelClipboxFaceDirection.PositiveX => new Vector3Int( outer.MaxExclusive.x - 1, outer.Min.y + u, outer.Min.z + v ),
		VoxelClipboxFaceDirection.NegativeY => new Vector3Int( outer.Min.x + u, outer.Min.y, outer.Min.z + v ),
		VoxelClipboxFaceDirection.PositiveY => new Vector3Int( outer.Min.x + u, outer.MaxExclusive.y - 1, outer.Min.z + v ),
		VoxelClipboxFaceDirection.NegativeZ => new Vector3Int( outer.Min.x + u, outer.Min.y + v, outer.Min.z ),
		VoxelClipboxFaceDirection.PositiveZ => new Vector3Int( outer.Min.x + u, outer.Min.y + v, outer.MaxExclusive.z - 1 ),
		_ => throw new System.ArgumentOutOfRangeException( nameof( face ) )
	};

	private static Vector3Int GetFaceDelta( VoxelClipboxFaceDirection face ) => face switch
	{
		VoxelClipboxFaceDirection.NegativeX => new Vector3Int( -1, 0, 0 ),
		VoxelClipboxFaceDirection.PositiveX => new Vector3Int( 1, 0, 0 ),
		VoxelClipboxFaceDirection.NegativeY => new Vector3Int( 0, -1, 0 ),
		VoxelClipboxFaceDirection.PositiveY => new Vector3Int( 0, 1, 0 ),
		VoxelClipboxFaceDirection.NegativeZ => new Vector3Int( 0, 0, -1 ),
		VoxelClipboxFaceDirection.PositiveZ => new Vector3Int( 0, 0, 1 ),
		_ => throw new System.ArgumentOutOfRangeException( nameof( face ) )
	};
}
