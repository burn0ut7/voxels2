internal readonly record struct VoxelClipboxConfig
{
	public const int CellsPerBlock = 32;
	public const int MinimumBlocksPerAxis = 4;
	public const int MaximumBlocksPerAxis = 8;
	public const int MaximumLevelCount = 7;

	public int BlocksPerAxis { get; }
	public int LevelCount { get; }
	public ushort RuleVersion { get; }
	public uint EditRevision { get; }
	public int StableRegularSlotCount => checked( LevelCount * BlocksPerAxis * BlocksPerAxis * BlocksPerAxis );
	public int StableTransitionSlotCount => checked( (LevelCount - 1) * 6 * BlocksPerAxis * BlocksPerAxis );
	public int ExpectedActiveRegularCount => checked( BlocksPerAxis * BlocksPerAxis * BlocksPerAxis +
		(LevelCount - 1) * (BlocksPerAxis * BlocksPerAxis * BlocksPerAxis - (BlocksPerAxis / 2) * (BlocksPerAxis / 2) * (BlocksPerAxis / 2)) );

	public VoxelClipboxConfig( int blocksPerAxis, int levelCount, ushort ruleVersion = 0, uint editRevision = 0 )
	{
		if ( blocksPerAxis is not (4 or 8) )
			throw new System.ArgumentOutOfRangeException( nameof( blocksPerAxis ), blocksPerAxis, "Clipbox supports 4 or 8 blocks per axis." );
		if ( levelCount < 1 || levelCount > MaximumLevelCount )
			throw new System.ArgumentOutOfRangeException( nameof( levelCount ), levelCount, $"Clipbox supports one through {MaximumLevelCount} levels." );
		if ( (blocksPerAxis & (blocksPerAxis - 1)) != 0 || blocksPerAxis % 4 != 0 )
			throw new System.ArgumentException( "Blocks per axis must be a power of two divisible by four.", nameof( blocksPerAxis ) );

		BlocksPerAxis = blocksPerAxis;
		LevelCount = levelCount;
		RuleVersion = ruleVersion;
		EditRevision = editRevision;
	}

	public static VoxelClipboxConfig DevelopmentDefault => new( 8, 4 );
	public static VoxelClipboxConfig FastCorrectness => new( 4, 2 );
}
