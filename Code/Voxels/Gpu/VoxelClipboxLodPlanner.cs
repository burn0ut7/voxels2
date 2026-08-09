internal enum VoxelLodFaceDirection
{
	NegativeX,
	PositiveX,
	NegativeY,
	PositiveY,
	NegativeZ,
	PositiveZ
}

internal readonly record struct VoxelClipboxLodLevel( int Lod, int SampleSpacing, int Dimension, int Radius );

internal readonly record struct VoxelLodTransitionDescriptor(
	VoxelVisualBlockKey Fine,
	VoxelVisualBlockKey Coarse,
	VoxelLodFaceDirection Face );

/// <summary>
/// Produces the fixed-size 3D clipbox residency set. Coordinates are in the
/// level's logical block grid; the GPU request converts them to aligned world
/// sample coordinates using SampleSpacing.
/// </summary>
internal sealed class VoxelClipboxLodPlanner
{
	private readonly List<VoxelClipboxLodLevel> _levels = new();
	private readonly List<VoxelLodTransitionDescriptor> _transitions = new();
	private readonly Dictionary<(int Lod, Vector3Int Coordinate), VoxelVisualBlockKey> _lookup = new();

	public IReadOnlyList<VoxelClipboxLodLevel> Levels => _levels;
	public IReadOnlyList<VoxelLodTransitionDescriptor> Transitions => _transitions;

	public VoxelClipboxLodPlanner( int radius, int levelCount )
	{
		radius = System.Math.Clamp( radius, 1, 128 );
		levelCount = System.Math.Clamp( levelCount, 1, 4 );
		var dimension = checked( radius * 2 + 2 );
		for ( var lod = 0; lod < levelCount; lod++ )
			_levels.Add( new VoxelClipboxLodLevel( lod, 1 << lod, dimension, radius ) );
	}

	public int Plan( Vector3Int observer, int ruleVersion, HashSet<VoxelVisualBlockKey> destination )
	{
		if ( destination is null ) throw new System.ArgumentNullException( nameof( destination ) );
		destination.Clear();
		_transitions.Clear();
		_lookup.Clear();

		foreach ( var level in _levels )
		{
			var center = new Vector3Int(
				FloorDiv( observer.x, level.SampleSpacing ),
				FloorDiv( observer.y, level.SampleSpacing ),
				FloorDiv( observer.z, level.SampleSpacing ) );
			var half = level.Dimension / 2;
			for ( var z = -half; z < level.Dimension - half; z++ )
			for ( var y = -half; y < level.Dimension - half; y++ )
			for ( var x = -half; x < level.Dimension - half; x++ )
			{
				var coordinate = new Vector3Int( center.x + x, center.y + y, center.z + z );
				if ( level.Lod > 0 && IsCoveredByFinerLevel( coordinate, center, level ) ) continue;
				var key = new VoxelVisualBlockKey( coordinate, level.Lod, ruleVersion );
				destination.Add( key );
				_lookup.Add( (level.Lod, coordinate), key );
			}
		}

		BuildTransitions( destination );
		return destination.Count;
	}

	private void BuildTransitions( HashSet<VoxelVisualBlockKey> desired )
	{
		foreach ( var fine in desired )
		{
			if ( fine.Lod >= _levels.Count - 1 ) continue;
			var spacing = 1 << fine.Lod;
			foreach ( var (face, direction) in Directions() )
			{
				var coarseSpacing = spacing * 2;
				var worldNeighbor = new Vector3Int(
					fine.Coordinate.x * spacing + direction.x,
					fine.Coordinate.y * spacing + direction.y,
					fine.Coordinate.z * spacing + direction.z );
				var coarseCoordinate = new Vector3Int(
					FloorDiv( worldNeighbor.x, coarseSpacing ),
					FloorDiv( worldNeighbor.y, coarseSpacing ),
					FloorDiv( worldNeighbor.z, coarseSpacing ) );
				if ( !_lookup.TryGetValue( (fine.Lod + 1, coarseCoordinate), out var coarse ) ) continue;
				_transitions.Add( new VoxelLodTransitionDescriptor( fine, coarse, face ) );
			}
		}
	}

	private static bool IsCoveredByFinerLevel( Vector3Int coordinate, Vector3Int center, VoxelClipboxLodLevel level )
	{
		var scale = level.SampleSpacing;
		var worldCenter = new Vector3(
			(coordinate.x + 0.5f) * scale,
			(coordinate.y + 0.5f) * scale,
			(coordinate.z + 0.5f) * scale );
		var observerCenter = new Vector3( center.x * scale, center.y * scale, center.z * scale );
		var finerExtent = (level.Radius + 0.5f) * scale * 0.5f;
		return System.MathF.Abs( worldCenter.x - observerCenter.x ) <= finerExtent &&
			System.MathF.Abs( worldCenter.y - observerCenter.y ) <= finerExtent &&
			System.MathF.Abs( worldCenter.z - observerCenter.z ) <= finerExtent;
	}

	private static IEnumerable<(VoxelLodFaceDirection Face, Vector3Int Direction)> Directions()
	{
		yield return (VoxelLodFaceDirection.NegativeX, new Vector3Int( -1, 0, 0 ));
		yield return (VoxelLodFaceDirection.PositiveX, new Vector3Int( 1, 0, 0 ));
		yield return (VoxelLodFaceDirection.NegativeY, new Vector3Int( 0, -1, 0 ));
		yield return (VoxelLodFaceDirection.PositiveY, new Vector3Int( 0, 1, 0 ));
		yield return (VoxelLodFaceDirection.NegativeZ, new Vector3Int( 0, 0, -1 ));
		yield return (VoxelLodFaceDirection.PositiveZ, new Vector3Int( 0, 0, 1 ));
	}

	private static int FloorDiv( int value, int divisor )
	{
		var quotient = value / divisor;
		var remainder = value % divisor;
		return remainder < 0 ? quotient - 1 : quotient;
	}
}
