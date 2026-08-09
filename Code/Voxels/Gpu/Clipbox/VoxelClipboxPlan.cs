internal sealed class VoxelClipboxPlan
{
	public VoxelClipboxConfig Config { get; }
	public Vector3Int ObserverBaseBlock { get; }
	public VoxelClipboxLevelState[] Levels { get; }
	public VoxelClipboxRegularSlotAssignment[] Slots { get; }
	public int ActiveRegularCount { get; }

	internal VoxelClipboxPlan( VoxelClipboxConfig config, Vector3Int observerBaseBlock, VoxelClipboxLevelState[] levels, VoxelClipboxRegularSlotAssignment[] slots, int activeRegularCount )
	{
		Config = config;
		ObserverBaseBlock = observerBaseBlock;
		Levels = levels;
		Slots = slots;
		ActiveRegularCount = activeRegularCount;
	}
}
