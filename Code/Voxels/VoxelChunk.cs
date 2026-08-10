public sealed class VoxelChunk
{
	private const float QuantizedMaximum = 32767.0f;

	private short[] _distances;
	private byte[] _materials;
	private short _uniformDistance;
	private byte _uniformMaterial;

	public Vector3Int Coordinate { get; }
	public int Size { get; }
	public int SampleSize { get; }
	public int SampleCount { get; }
	public float DistanceClamp { get; }
	public float MaximumQuantizationError => DistanceClamp / (QuantizedMaximum * 2.0f);
	public bool IsUniform => _distances is null;
	public long EstimatedStorageBytes => IsUniform ? sizeof( short ) + sizeof( byte ) : (long)SampleCount * (sizeof( short ) + sizeof( byte ));

	public VoxelChunk( Vector3Int coordinate, int size, float distanceClamp )
	{
		if ( size < 1 )
		{
			throw new System.ArgumentOutOfRangeException( nameof( size ) );
		}

		if ( !float.IsFinite( distanceClamp ) || distanceClamp <= 0.0f )
		{
			throw new System.ArgumentOutOfRangeException( nameof( distanceClamp ) );
		}

		Coordinate = coordinate;
		Size = size;
		SampleSize = size + 1;
		SampleCount = checked( SampleSize * SampleSize * SampleSize );
		DistanceClamp = distanceClamp;
		_uniformDistance = EncodeDistance( 0.0f );
		_uniformMaterial = (byte)VoxelMaterial.Air;
	}

	public Voxel GetVoxel( int x, int y, int z )
	{
		return GetVoxelByIndex( GetIndex( x, y, z ) );
	}

	public void SetVoxel( int x, int y, int z, Voxel voxel )
	{
		SetVoxelByIndex( GetIndex( x, y, z ), voxel );
	}

	internal bool SetVoxelIfChanged( int x, int y, int z, Voxel voxel )
	{
		return SetVoxelByIndexIfChanged( GetIndex( x, y, z ), voxel );
	}

	internal Voxel GetVoxelByIndex( int index )
	{
		ValidateIndex( index );
		var encodedDistance = IsUniform ? _uniformDistance : _distances[index];
		var material = IsUniform ? _uniformMaterial : _materials[index];
		return new Voxel( DecodeDistance( encodedDistance ), (VoxelMaterial)material );
	}

	internal float GetDistanceByIndex( int index )
	{
		ValidateIndex( index );
		return DecodeDistance( IsUniform ? _uniformDistance : _distances[index] );
	}

	internal void SetVoxelByIndex( int index, Voxel voxel )
	{
		SetVoxelByIndexIfChanged( index, voxel );
	}

	internal bool SetVoxelByIndexIfChanged( int index, Voxel voxel )
	{
		ValidateIndex( index );
		var encodedDistance = EncodeDistance( voxel.Distance );
		var encodedMaterial = (byte)voxel.Material;
		var existingDistance = IsUniform ? _uniformDistance : _distances[index];
		var existingMaterial = IsUniform ? _uniformMaterial : _materials[index];
		if ( encodedDistance == existingDistance && encodedMaterial == existingMaterial )
		{
			return false;
		}

		EnsureExpanded();
		_distances[index] = encodedDistance;
		_materials[index] = encodedMaterial;
		return true;
	}

	internal void Fill( Voxel voxel )
	{
		_uniformDistance = EncodeDistance( voxel.Distance );
		_uniformMaterial = (byte)voxel.Material;
		_distances = null;
		_materials = null;
	}

	internal void FillLayer( int z, Voxel voxel )
	{
		if ( (uint)z >= SampleSize )
		{
			throw new System.ArgumentOutOfRangeException( nameof( z ) );
		}

		var encodedDistance = EncodeDistance( voxel.Distance );
		var encodedMaterial = (byte)voxel.Material;
		if ( IsUniform && encodedDistance == _uniformDistance && encodedMaterial == _uniformMaterial )
		{
			return;
		}

		EnsureExpanded();
		var layerLength = SampleSize * SampleSize;
		var layerStart = z * layerLength;
		System.Array.Fill( _distances, encodedDistance, layerStart, layerLength );
		System.Array.Fill( _materials, encodedMaterial, layerStart, layerLength );
	}

	internal void CopyDistancesTo( System.Span<float> destination )
	{
		if ( destination.Length < SampleCount )
		{
			throw new System.ArgumentException( "Destination is smaller than the chunk sample count.", nameof( destination ) );
		}

		if ( IsUniform )
		{
			destination[..SampleCount].Fill( DecodeDistance( _uniformDistance ) );
			return;
		}

		for ( var index = 0; index < SampleCount; index++ )
		{
			destination[index] = DecodeDistance( _distances[index] );
		}
	}

	internal void CopyDistanceRowTo( int y, int z, float[] destination, int destinationIndex )
	{
		if ( (uint)y >= SampleSize || (uint)z >= SampleSize )
		{
			throw new System.ArgumentOutOfRangeException( nameof( y ), "Voxel sample row must be inside the chunk." );
		}

		if ( destination is null || destinationIndex < 0 || destinationIndex > destination.Length - SampleSize )
		{
			throw new System.ArgumentException( "Destination cannot contain the chunk sample row.", nameof( destination ) );
		}

		if ( IsUniform )
		{
			System.Array.Fill( destination, DecodeDistance( _uniformDistance ), destinationIndex, SampleSize );
			return;
		}

		var sourceIndex = SampleSize * (y + SampleSize * z);
		for ( var x = 0; x < SampleSize; x++ )
		{
			destination[destinationIndex + x] = DecodeDistance( _distances[sourceIndex + x] );
		}
	}

	public int GetIndex( int x, int y, int z )
	{
		if ( (uint)x >= SampleSize || (uint)y >= SampleSize || (uint)z >= SampleSize )
		{
			throw new System.ArgumentOutOfRangeException( nameof( x ), "Voxel sample coordinates must be inside the chunk." );
		}

		return x + SampleSize * (y + SampleSize * z);
	}

	private short EncodeDistance( float distance )
	{
		var normalized = System.Math.Clamp( distance / DistanceClamp, -1.0f, 1.0f );
		return (short)System.MathF.Round( normalized * QuantizedMaximum );
	}

	private float DecodeDistance( short distance )
	{
		return distance * (DistanceClamp / QuantizedMaximum);
	}

	private void EnsureExpanded()
	{
		if ( !IsUniform )
		{
			return;
		}

		_distances = new short[SampleCount];
		_materials = new byte[SampleCount];
		System.Array.Fill( _distances, _uniformDistance );
		System.Array.Fill( _materials, _uniformMaterial );
	}

	private void ValidateIndex( int index )
	{
		if ( (uint)index >= SampleCount )
		{
			throw new System.ArgumentOutOfRangeException( nameof( index ) );
		}
	}
}
