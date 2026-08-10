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

	public static uint FaceMaskBit( VoxelClipboxFaceDirection face ) => 1u << (int)face;

	/// <summary>
	/// Converts the fine-side transition orientation to the touching face on the
	/// coarse block. Transition slots are named from the fine block's point of
	/// view, while TransitionFaceMask is stored on the coarse regular block.
	/// </summary>
	public static VoxelClipboxFaceDirection OppositeFace( VoxelClipboxFaceDirection face ) => face switch
	{
		VoxelClipboxFaceDirection.NegativeX => VoxelClipboxFaceDirection.PositiveX,
		VoxelClipboxFaceDirection.PositiveX => VoxelClipboxFaceDirection.NegativeX,
		VoxelClipboxFaceDirection.NegativeY => VoxelClipboxFaceDirection.PositiveY,
		VoxelClipboxFaceDirection.PositiveY => VoxelClipboxFaceDirection.NegativeY,
		VoxelClipboxFaceDirection.NegativeZ => VoxelClipboxFaceDirection.PositiveZ,
		VoxelClipboxFaceDirection.PositiveZ => VoxelClipboxFaceDirection.NegativeZ,
		_ => throw new System.ArgumentOutOfRangeException( nameof( face ) )
	};

	public static int GetStableSlotId( VoxelClipboxConfig config, int fineLevel, VoxelClipboxFaceDirection face, int u, int v ) =>
		checked( fineLevel * 6 * config.BlocksPerAxis * config.BlocksPerAxis + (int)face * config.BlocksPerAxis * config.BlocksPerAxis + u + config.BlocksPerAxis * v );

	public static int Populate( VoxelClipboxConfig config, VoxelClipboxLevelState[] levels, VoxelClipboxRegularSlotAssignment[] regularSlots, VoxelClipboxTransitionSlotAssignment[] destination )
	{
		var faceCellCount = checked( config.BlocksPerAxis * config.BlocksPerAxis );
		var expectedCapacity = config.StableTransitionSlotCount;
		if ( levels.Length != config.LevelCount ) throw new System.ArgumentException( "Level storage does not match the clipbox configuration.", nameof( levels ) );
		if ( regularSlots.Length != config.StableRegularSlotCount ) throw new System.ArgumentException( "Regular slot storage does not match the clipbox configuration.", nameof( regularSlots ) );
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
				var fineSlotId = VoxelClipboxCoordinates.GetSlotIndex( fineLevel, fineCoordinate, config.BlocksPerAxis );
				var coarseSlotId = VoxelClipboxCoordinates.GetSlotIndex( fineLevel + 1, coarseCoordinate, config.BlocksPerAxis );
				var editRevision = System.Math.Max( regularSlots[fineSlotId].Key.EditRevision, regularSlots[coarseSlotId].Key.EditRevision );
				destination[stableSlotId] = new VoxelClipboxTransitionSlotAssignment(
					stableSlotId,
					fineLevel,
					fineLevel + 1,
					fineSlotId,
					coarseSlotId,
					fineCoordinate,
					coarseCoordinate,
					face,
					u,
					v,
					active,
					config.RuleVersion,
					editRevision,
					regularSlots[fineSlotId].Key,
					regularSlots[coarseSlotId].Key );
				if ( active ) activeCount++;
			}
		}

		return activeCount;
	}

	internal static void ApplyCoarseFaceMasks( VoxelClipboxConfig config, VoxelClipboxLevelState[] levels, VoxelClipboxRegularSlotAssignment[] regularSlots )
	{
		if ( levels.Length != config.LevelCount ) throw new System.ArgumentException( "Level storage does not match the clipbox configuration.", nameof( levels ) );
		if ( regularSlots.Length != config.StableRegularSlotCount ) throw new System.ArgumentException( "Regular slot storage does not match the clipbox configuration.", nameof( regularSlots ) );

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
				if ( !fine.IsActive( fineCoordinate ) || fine.IsActive( neighborCoordinate ) || !coarse.IsActive( coarseCoordinate ) ) continue;
				var coarseSlot = VoxelClipboxCoordinates.GetSlotIndex( fineLevel + 1, coarseCoordinate, config.BlocksPerAxis );
				var assignment = regularSlots[coarseSlot];
				regularSlots[coarseSlot] = assignment with { Key = assignment.Key with { TransitionFaceMask = assignment.Key.TransitionFaceMask | FaceMaskBit( OppositeFace( face ) ) } };
			}
		}
	}

	internal static uint GetCoarseFaceMask( VoxelClipboxConfig config, VoxelClipboxLevelState[] levels, Vector3Int coarseCoordinate, int coarseLevel )
	{
		if ( coarseLevel <= 0 || coarseLevel >= config.LevelCount ) return 0;
		var fine = levels[coarseLevel - 1];
		var coarse = levels[coarseLevel];
		var mask = 0u;
		for ( var faceIndex = 0; faceIndex < 6; faceIndex++ )
		for ( var v = 0; v < config.BlocksPerAxis; v++ )
		for ( var u = 0; u < config.BlocksPerAxis; u++ )
		{
			var face = (VoxelClipboxFaceDirection)faceIndex;
			var fineCoordinate = GetFaceCoordinate( fine.Outer, face, u, v );
			var neighborCoordinate = fineCoordinate + GetFaceDelta( face );
			var candidateCoarseCoordinate = VoxelClipboxCoordinates.FloorDiv( neighborCoordinate, 2 );
			if ( candidateCoarseCoordinate == coarseCoordinate && fine.IsActive( fineCoordinate ) && !fine.IsActive( neighborCoordinate ) && coarse.IsActive( coarseCoordinate ) ) mask |= FaceMaskBit( OppositeFace( face ) );
		}
		return mask;
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
