public sealed class VoxelChunk
{
	private readonly Voxel[] _voxels;

	public Vector3Int Coordinate { get; }
	public int Size { get; }
	public int SampleSize { get; }
	public int SampleCount => _voxels.Length;

	internal Voxel[] Voxels => _voxels;

	public VoxelChunk( Vector3Int coordinate, int size )
	{
		if ( size < 1 )
		{
			throw new System.ArgumentOutOfRangeException( nameof( size ) );
		}

		Coordinate = coordinate;
		Size = size;
		SampleSize = size + 1;
		_voxels = new Voxel[checked( SampleSize * SampleSize * SampleSize )];
	}

	public Voxel GetVoxel( int x, int y, int z )
	{
		return _voxels[GetIndex( x, y, z )];
	}

	public void SetVoxel( int x, int y, int z, Voxel voxel )
	{
		_voxels[GetIndex( x, y, z )] = voxel;
	}

	public int GetIndex( int x, int y, int z )
	{
		if ( (uint)x >= SampleSize || (uint)y >= SampleSize || (uint)z >= SampleSize )
		{
			throw new System.ArgumentOutOfRangeException( nameof( x ), "Voxel sample coordinates must be inside the chunk." );
		}

		return x + SampleSize * (y + SampleSize * z);
	}
}
