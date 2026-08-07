public readonly struct Voxel
{
	public float Distance { get; }
	public VoxelMaterial Material { get; }

	public bool IsSolid => Distance < 0.0f;

	public Voxel( float distance, VoxelMaterial material )
	{
		Distance = distance;
		Material = material;
	}
}
