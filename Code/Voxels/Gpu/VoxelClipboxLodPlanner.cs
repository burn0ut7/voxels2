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
	private const int LogicalBlockSize = 32;
	private readonly List<VoxelClipboxLodLevel> _levels = new();
	private readonly List<VoxelLodTransitionDescriptor> _transitions = new();
	private readonly Dictionary<(int Lod, Vector3Int Coordinate), VoxelVisualBlockKey> _lookup = new();
	private Vector3Int _lastObserver;
	private int _lastRuleVersion;
	private bool _hasPlan;

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
				if ( level.Lod > 0 && IsCoveredByFinerLevel( coordinate, observer, level ) ) continue;
				var key = new VoxelVisualBlockKey( coordinate, level.Lod, ruleVersion );
				destination.Add( key );
				_lookup.Add( (level.Lod, coordinate), key );
			}
		}

		BuildTransitions( destination );
		_lastObserver = observer;
		_lastRuleVersion = ruleVersion;
		_hasPlan = true;
		return destination.Count;
	}

	/// <summary>
	/// Reassigns only the entering toroidal slabs for ordinary movement. A
	/// teleport or a rule change falls back to a complete plan because no slot
	/// mapping remains valid in that case.
	/// </summary>
	public int PlanDelta( Vector3Int observer, int ruleVersion, HashSet<VoxelVisualBlockKey> destination,
		List<VoxelVisualBlockKey> entering, List<VoxelVisualBlockKey> leaving )
	{
		if ( destination is null ) throw new System.ArgumentNullException( nameof( destination ) );
		if ( entering is null ) throw new System.ArgumentNullException( nameof( entering ) );
		if ( leaving is null ) throw new System.ArgumentNullException( nameof( leaving ) );
		entering.Clear();
		leaving.Clear();
		if ( !_hasPlan || ruleVersion != _lastRuleVersion )
		{
			Plan( observer, ruleVersion, destination );
			entering.AddRange( destination );
			return destination.Count;
		}

		var changed = false;
		foreach ( var level in _levels )
		{
			var oldCenter = GetLevelCenter( _lastObserver, level );
			var newCenter = GetLevelCenter( observer, level );
			var delta = newCenter - oldCenter;
			if ( delta == Vector3Int.Zero )
			{
				if ( level.Lod > 0 )
				{
					ProcessShellBoundary( level, newCenter, observer, ruleVersion, destination, entering, leaving );
					changed = true;
				}
				continue;
			}
			changed = true;
			if ( System.Math.Abs( delta.x ) >= level.Dimension || System.Math.Abs( delta.y ) >= level.Dimension || System.Math.Abs( delta.z ) >= level.Dimension )
			{
				var previous = new HashSet<VoxelVisualBlockKey>( destination );
				Plan( observer, ruleVersion, destination );
				BuildDelta( previous, destination, entering, leaving );
				return destination.Count;
			}

			ProcessEnteringSlabs( level, oldCenter, newCenter, observer, ruleVersion, destination, entering, leaving );
			if ( level.Lod > 0 ) ProcessShellBoundary( level, newCenter, observer, ruleVersion, destination, entering, leaving );
		}

		if ( changed )
		{
			BuildTransitions( destination );
			_lastObserver = observer;
		}
		return destination.Count;
	}

	private void ProcessEnteringSlabs( VoxelClipboxLodLevel level, Vector3Int oldCenter, Vector3Int newCenter, Vector3Int observer,
		int ruleVersion, HashSet<VoxelVisualBlockKey> destination, List<VoxelVisualBlockKey> entering, List<VoxelVisualBlockKey> leaving )
	{
		var half = level.Dimension / 2;
		var oldMin = oldCenter - new Vector3Int( half, half, half );
		var oldMax = oldMin + new Vector3Int( level.Dimension - 1, level.Dimension - 1, level.Dimension - 1 );
		var newMin = newCenter - new Vector3Int( half, half, half );
		var newMax = newMin + new Vector3Int( level.Dimension - 1, level.Dimension - 1, level.Dimension - 1 );
		var delta = newCenter - oldCenter;

		if ( delta.x != 0 )
		{
			var minX = delta.x > 0 ? oldMax.x + 1 : newMin.x;
			var maxX = delta.x > 0 ? newMax.x : oldMin.x - 1;
			for ( var z = newMin.z; z <= newMax.z; z++ )
			for ( var y = newMin.y; y <= newMax.y; y++ )
			for ( var x = minX; x <= maxX; x++ ) ReassignCell( level, new Vector3Int( x, y, z ), oldCenter, observer, ruleVersion, destination, entering, leaving );
		}
		if ( delta.y != 0 )
		{
			var minY = delta.y > 0 ? oldMax.y + 1 : newMin.y;
			var maxY = delta.y > 0 ? newMax.y : oldMin.y - 1;
			for ( var z = newMin.z; z <= newMax.z; z++ )
			for ( var y = minY; y <= maxY; y++ )
			for ( var x = newMin.x; x <= newMax.x; x++ ) ReassignCell( level, new Vector3Int( x, y, z ), oldCenter, observer, ruleVersion, destination, entering, leaving );
		}
		if ( delta.z != 0 )
		{
			var minZ = delta.z > 0 ? oldMax.z + 1 : newMin.z;
			var maxZ = delta.z > 0 ? newMax.z : oldMin.z - 1;
			for ( var z = minZ; z <= maxZ; z++ )
			for ( var y = newMin.y; y <= newMax.y; y++ )
			for ( var x = newMin.x; x <= newMax.x; x++ ) ReassignCell( level, new Vector3Int( x, y, z ), oldCenter, observer, ruleVersion, destination, entering, leaving );
		}
	}

	private void ProcessShellBoundary( VoxelClipboxLodLevel level, Vector3Int center, Vector3Int observer, int ruleVersion,
		HashSet<VoxelVisualBlockKey> destination, List<VoxelVisualBlockKey> entering, List<VoxelVisualBlockKey> leaving )
	{
		var half = level.Dimension / 2;
		var min = center - new Vector3Int( half, half, half );
		var max = min + new Vector3Int( level.Dimension - 1, level.Dimension - 1, level.Dimension - 1 );
		for ( var z = min.z; z <= max.z; z++ )
		for ( var y = min.y; y <= max.y; y++ )
		for ( var x = min.x; x <= max.x; x++ )
		{
			ReassignCell( level, new Vector3Int( x, y, z ), center, observer, ruleVersion, destination, entering, leaving );
		}
	}

	private void ReassignCell( VoxelClipboxLodLevel level, Vector3Int coordinate, Vector3Int oldCenter, Vector3Int observer, int ruleVersion,
		HashSet<VoxelVisualBlockKey> destination, List<VoxelVisualBlockKey> entering, List<VoxelVisualBlockKey> leaving )
	{
		var newCenter = GetLevelCenter( observer, level );
		// A toroidal slot entering on one side reuses the slot that exited on
		// the opposite side. Pair by one full logical dimension, not by the
		// center offset (which would incorrectly evict an interior block).
		var oldCoordinate = coordinate;
		var half = level.Dimension / 2;
		var oldMin = oldCenter - new Vector3Int( half, half, half );
		var oldMax = oldMin + new Vector3Int( level.Dimension - 1, level.Dimension - 1, level.Dimension - 1 );
		if ( coordinate.x > oldMax.x ) oldCoordinate.x -= level.Dimension;
		else if ( coordinate.x < oldMin.x ) oldCoordinate.x += level.Dimension;
		if ( coordinate.y > oldMax.y ) oldCoordinate.y -= level.Dimension;
		else if ( coordinate.y < oldMin.y ) oldCoordinate.y += level.Dimension;
		if ( coordinate.z > oldMax.z ) oldCoordinate.z -= level.Dimension;
		else if ( coordinate.z < oldMin.z ) oldCoordinate.z += level.Dimension;
		var oldKey = new VoxelVisualBlockKey( oldCoordinate, level.Lod, ruleVersion );
		if ( destination.Remove( oldKey ) ) leaving.Add( oldKey );
		if ( level.Lod > 0 && IsCoveredByFinerLevel( coordinate, observer, level ) ) return;
		var newKey = new VoxelVisualBlockKey( coordinate, level.Lod, ruleVersion );
		if ( destination.Add( newKey ) ) entering.Add( newKey );
	}

	private static void BuildDelta( HashSet<VoxelVisualBlockKey> previous, HashSet<VoxelVisualBlockKey> destination, List<VoxelVisualBlockKey> entering, List<VoxelVisualBlockKey> leaving )
	{
		foreach ( var key in previous ) if ( !destination.Contains( key ) ) leaving.Add( key );
		foreach ( var key in destination ) if ( !previous.Contains( key ) ) entering.Add( key );
	}

	private static Vector3Int GetLevelCenter( Vector3Int observer, VoxelClipboxLodLevel level ) => new(
		FloorDiv( observer.x, level.SampleSpacing ),
		FloorDiv( observer.y, level.SampleSpacing ),
		FloorDiv( observer.z, level.SampleSpacing ) );

	private void BuildTransitions( HashSet<VoxelVisualBlockKey> desired )
	{
		_lookup.Clear();
		foreach ( var key in desired ) _lookup[(key.Lod, key.Coordinate)] = key;
		foreach ( var fine in desired )
		{
			if ( fine.Lod >= _levels.Count - 1 ) continue;
			var spacing = 1 << fine.Lod;
			foreach ( var (face, direction) in Directions() )
			{
				var coarseSpacing = spacing * 2;
				// Keys are block coordinates, while the LOD spacing is expressed in
				// canonical samples. Cross the complete fine block before mapping the
				// face into the coarse block grid; adding one sample would select an
				// interior neighbor for every face.
				var worldNeighbor = new Vector3Int(
					(fine.Coordinate.x * LogicalBlockSize + direction.x * LogicalBlockSize) * spacing,
					(fine.Coordinate.y * LogicalBlockSize + direction.y * LogicalBlockSize) * spacing,
					(fine.Coordinate.z * LogicalBlockSize + direction.z * LogicalBlockSize) * spacing );
				var coarseCoordinate = new Vector3Int(
					FloorDiv( worldNeighbor.x, LogicalBlockSize * coarseSpacing ),
					FloorDiv( worldNeighbor.y, LogicalBlockSize * coarseSpacing ),
					FloorDiv( worldNeighbor.z, LogicalBlockSize * coarseSpacing ) );
				if ( !_lookup.TryGetValue( (fine.Lod + 1, coarseCoordinate), out var coarse ) ) continue;
				_transitions.Add( new VoxelLodTransitionDescriptor( fine, coarse, face ) );
			}
		}
	}

	private static bool IsCoveredByFinerLevel( Vector3Int coordinate, Vector3Int observer, VoxelClipboxLodLevel level )
	{
		var scale = level.SampleSpacing;
		var worldCenter = new Vector3(
			(coordinate.x + 0.5f) * LogicalBlockSize * scale,
			(coordinate.y + 0.5f) * LogicalBlockSize * scale,
			(coordinate.z + 0.5f) * LogicalBlockSize * scale );
		var observerCenter = new Vector3( observer.x * LogicalBlockSize, observer.y * LogicalBlockSize, observer.z * LogicalBlockSize );
		var finerExtent = (level.Radius + 0.5f) * LogicalBlockSize * 0.5f;
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
