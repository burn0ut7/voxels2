internal static class VoxelTransvoxelTransitionOrientation
{
	private static readonly Vector3Int[] SampleCoordinates =
	{
		new( 0, 0, 0 ), new( 1, 0, 0 ), new( 2, 0, 0 ),
		new( 0, 1, 0 ), new( 1, 1, 0 ), new( 2, 1, 0 ),
		new( 0, 2, 0 ), new( 1, 2, 0 ), new( 2, 2, 0 )
	};

	/// <summary>
	/// Returns the canonical local position used by the Transvoxel table. The
	/// first nine samples are on the fine face; the final four are one coarse
	/// sample inward on the touching coarse side.
	/// </summary>
	public static Vector3 CellSamplePosition( int sample, VoxelClipboxFaceDirection face, float fineStep )
	{
		if ( (uint)sample >= 13 ) throw new System.ArgumentOutOfRangeException( nameof( sample ) );
		if ( fineStep <= 0.0f ) throw new System.ArgumentOutOfRangeException( nameof( fineStep ) );

		var coarseSample = sample >= 9;
		var coordinate = sample < 9 ? SampleCoordinates[sample] : sample switch
		{
			9 => new Vector3Int( 0, 0, 0 ),
			10 => new Vector3Int( 2, 0, 0 ),
			11 => new Vector3Int( 0, 2, 0 ),
			12 => new Vector3Int( 2, 2, 0 ),
			_ => throw new System.ArgumentOutOfRangeException( nameof( sample ) )
		};

		var position = face switch
		{
			VoxelClipboxFaceDirection.NegativeX => new Vector3( coarseSample ? -2.0f : 0.0f, coordinate.x, coordinate.y ),
			VoxelClipboxFaceDirection.PositiveX => new Vector3( coarseSample ? 4.0f : 2.0f, coordinate.y, coordinate.x ),
			VoxelClipboxFaceDirection.NegativeY => new Vector3( coordinate.y, coarseSample ? -2.0f : 0.0f, coordinate.x ),
			VoxelClipboxFaceDirection.PositiveY => new Vector3( coordinate.x, coarseSample ? 4.0f : 2.0f, coordinate.y ),
			VoxelClipboxFaceDirection.NegativeZ => new Vector3( coordinate.x, coordinate.y, coarseSample ? -2.0f : 0.0f ),
			VoxelClipboxFaceDirection.PositiveZ => new Vector3( coordinate.y, coordinate.x, coarseSample ? 4.0f : 2.0f ),
			_ => throw new System.ArgumentOutOfRangeException( nameof( face ) )
		};
		return position * fineStep;
	}
}
