internal static class VoxelGpuCanonicalCoordinates
{
	public static Vector3Int GlobalTerrainOriginSamples( int chunkSize ) => new( 0, 0, -chunkSize );

	public static int SampleStep( int lod )
	{
		if ( lod < 0 || lod > 30 ) throw new System.ArgumentOutOfRangeException( nameof( lod ) );
		return 1 << lod;
	}

	public static Vector3Int CanonicalBlockOriginSamples( Vector3Int coordinate, int lod, int chunkSize )
	{
		if ( chunkSize <= 0 ) throw new System.ArgumentOutOfRangeException( nameof( chunkSize ) );
		return coordinate * checked( chunkSize * SampleStep( lod ) ) + GlobalTerrainOriginSamples( chunkSize );
	}

	public static Vector3 WorldFromCanonicalSamples( Vector3 sample, float voxelSize ) => sample * voxelSize;

	public static void AssertContract( int chunkSize )
	{
		for ( var lod = 0; lod <= 6; lod++ )
		{
			var parent = CanonicalBlockOriginSamples( new Vector3Int( -3, 2, -5 ), lod + 1, chunkSize );
			var child = CanonicalBlockOriginSamples( new Vector3Int( -6, 4, -10 ), lod, chunkSize );
			if ( parent != child ) throw new System.InvalidOperationException( $"Canonical parent/child origin mismatch at LOD {lod}." );

			foreach ( var axis in new[] { new Vector3Int( 1, 0, 0 ), new Vector3Int( 0, 1, 0 ), new Vector3Int( 0, 0, 1 ) } )
			{
				var origin = CanonicalBlockOriginSamples( new Vector3Int( -7, 5, -2 ), lod, chunkSize );
				var adjacent = CanonicalBlockOriginSamples( new Vector3Int( -7, 5, -2 ) + axis, lod, chunkSize );
				if ( adjacent - origin != axis * checked( chunkSize * SampleStep( lod ) ) )
					throw new System.InvalidOperationException( $"Canonical adjacent origin mismatch at LOD {lod}, axis {axis}." );
			}
		}
	}
}
