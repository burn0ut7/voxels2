internal static class VoxelClipboxCoordinates
{
	public static Vector3Int GetObserverBaseBlock( Vector3Int canonicalSample ) => new(
		FloorDiv( canonicalSample.x, VoxelClipboxConfig.CellsPerBlock ),
		FloorDiv( canonicalSample.y, VoxelClipboxConfig.CellsPerBlock ),
		FloorDiv( canonicalSample.z, VoxelClipboxConfig.CellsPerBlock ) );

	public static Vector3Int GetLevelOrigin( Vector3Int observerBaseBlock, int level, int blocksPerAxis )
	{
		var spacing = 1 << level;
		var observerLodBlock = FloorDiv( observerBaseBlock, spacing );
		var snappedCenter = FloorDiv( observerLodBlock, 2 ) * 2;
		return snappedCenter - blocksPerAxis / 2;
	}

	public static Vector3Int GetLodBlockCoordinate( Vector3Int baseBlock, int level ) => FloorDiv( baseBlock, 1 << level );

	public static Vector3Int GetCanonicalSampleOrigin( Vector3Int lodBlockCoordinate, int level )
	{
		var baseBlockScale = 1 << level;
		return lodBlockCoordinate * (baseBlockScale * VoxelClipboxConfig.CellsPerBlock);
	}

	public static int GetSlotIndex( int level, Vector3Int lodBlockCoordinate, int blocksPerAxis )
	{
		var local = new Vector3Int(
			PositiveModulo( lodBlockCoordinate.x, blocksPerAxis ),
			PositiveModulo( lodBlockCoordinate.y, blocksPerAxis ),
			PositiveModulo( lodBlockCoordinate.z, blocksPerAxis ) );
		var levelBase = checked( level * blocksPerAxis * blocksPerAxis * blocksPerAxis );
		return checked( levelBase + local.x + blocksPerAxis * (local.y + blocksPerAxis * local.z) );
	}

	public static int FloorDiv( int value, int divisor )
	{
		if ( divisor <= 0 ) throw new System.ArgumentOutOfRangeException( nameof( divisor ) );
		var quotient = value / divisor;
		var remainder = value % divisor;
		return remainder < 0 ? quotient - 1 : quotient;
	}

	public static Vector3Int FloorDiv( Vector3Int value, int divisor ) => new(
		FloorDiv( value.x, divisor ), FloorDiv( value.y, divisor ), FloorDiv( value.z, divisor ) );

	public static int PositiveModulo( int value, int modulus )
	{
		if ( modulus <= 0 ) throw new System.ArgumentOutOfRangeException( nameof( modulus ) );
		var remainder = value % modulus;
		return remainder < 0 ? remainder + modulus : remainder;
	}

	public static bool IsExactlyDivisible( Vector3Int value, int divisor ) =>
		value.x % divisor == 0 && value.y % divisor == 0 && value.z % divisor == 0;
}
