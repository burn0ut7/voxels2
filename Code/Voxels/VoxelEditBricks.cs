using System.IO;
using System.Runtime.InteropServices;

internal readonly record struct VoxelEditBakeReport(
	bool Baked,
	string Failure,
	int BakedOperations,
	int RemainingOperations,
	int BakedBrickCount,
	long SampleEvaluations,
	uint BakedThroughRevision );

internal readonly record struct VoxelEditPersistenceReport(
	bool Succeeded,
	bool Found,
	bool RuleVersionMismatch,
	int ActiveOperations,
	int BakedBricks,
	string Failure );

[StructLayout( LayoutKind.Sequential, Pack = 4, Size = 32 )]
internal struct VoxelGpuEditBrick
{
	public Vector4 OriginAndSize;
	public uint SampleOffset;
	public uint WorldRevision;
	public uint RuleVersion;
	public uint Reserved;
}

internal sealed class VoxelEditBrick
{
	public const int Size = VoxelEditBrickStore.BrickSize;
	public const int SampleSize = Size + 1;
	public const int SampleCount = SampleSize * SampleSize * SampleSize;

	public Vector3Int Coordinate { get; }
	public uint WorldRevision { get; }
	public uint RuleVersion { get; }
	public float[] Distances { get; }
	public byte[] Materials { get; }

	public VoxelEditBrick( Vector3Int coordinate, uint worldRevision, uint ruleVersion, float[] distances, byte[] materials )
	{
		if ( distances is null || distances.Length != SampleCount ) throw new System.ArgumentException( "A baked edit brick must contain its complete sample cube.", nameof( distances ) );
		if ( materials is null || materials.Length != SampleCount ) throw new System.ArgumentException( "A baked edit brick must contain its complete material cube.", nameof( materials ) );
		Coordinate = coordinate;
		WorldRevision = worldRevision;
		RuleVersion = ruleVersion;
		Distances = distances;
		Materials = materials;
	}

	public bool TrySample( Vector3 canonicalSample, out float distance, out VoxelMaterial material )
	{
		var origin = new Vector3( Coordinate.x * Size, Coordinate.y * Size, Coordinate.z * Size );
		var local = canonicalSample - origin;
		if ( local.x < 0.0f || local.y < 0.0f || local.z < 0.0f || local.x > Size || local.y > Size || local.z > Size )
		{
			distance = 0.0f;
			material = VoxelMaterial.Air;
			return false;
		}

		var x0 = System.Math.Clamp( (int)System.MathF.Floor( local.x ), 0, Size );
		var y0 = System.Math.Clamp( (int)System.MathF.Floor( local.y ), 0, Size );
		var z0 = System.Math.Clamp( (int)System.MathF.Floor( local.z ), 0, Size );
		var x1 = System.Math.Min( Size, x0 + 1 );
		var y1 = System.Math.Min( Size, y0 + 1 );
		var z1 = System.Math.Min( Size, z0 + 1 );
		var tx = x1 == x0 ? 0.0f : local.x - x0;
		var ty = y1 == y0 ? 0.0f : local.y - y0;
		var tz = z1 == z0 ? 0.0f : local.z - z0;

		var c000 = Distances[GetIndex( x0, y0, z0 )];
		var c100 = Distances[GetIndex( x1, y0, z0 )];
		var c010 = Distances[GetIndex( x0, y1, z0 )];
		var c110 = Distances[GetIndex( x1, y1, z0 )];
		var c001 = Distances[GetIndex( x0, y0, z1 )];
		var c101 = Distances[GetIndex( x1, y0, z1 )];
		var c011 = Distances[GetIndex( x0, y1, z1 )];
		var c111 = Distances[GetIndex( x1, y1, z1 )];
		var lower = Lerp( Lerp( c000, c100, tx ), Lerp( c010, c110, tx ), ty );
		var upper = Lerp( Lerp( c001, c101, tx ), Lerp( c011, c111, tx ), ty );
		distance = Lerp( lower, upper, tz );
		var materialX = (int)System.MathF.Round( local.x );
		var materialY = (int)System.MathF.Round( local.y );
		var materialZ = (int)System.MathF.Round( local.z );
		material = (VoxelMaterial)Materials[GetIndex( System.Math.Clamp( materialX, 0, Size ), System.Math.Clamp( materialY, 0, Size ), System.Math.Clamp( materialZ, 0, Size ) )];
		return true;
	}

	private static float Lerp( float first, float second, float fraction ) => first + (second - first) * fraction;
	private static int GetIndex( int x, int y, int z ) => x + SampleSize * (y + SampleSize * z);
}

internal sealed class VoxelEditBrickStore
{
	public const int BrickSize = 16;
	public const int SampleSize = BrickSize + 1;
	public const int SampleCount = SampleSize * SampleSize * SampleSize;
	public const int MaximumBakedBricks = 1024;
	public const int BakedBrickLookupCapacity = 2048;
	private const int FileMagic = 0x56454442;
	private const int FileVersion = 1;
	private readonly Dictionary<Vector3Int, VoxelEditBrick> _bricks = new();

	public int Count => _bricks.Count;
	public long SampleCountTotal => (long)_bricks.Count * SampleCount;

	public bool TrySample( Vector3 canonicalSample, out float distance, out VoxelMaterial material )
	{
		distance = 0.0f;
		material = VoxelMaterial.Air;
		var coordinate = GetBrickCoordinate( canonicalSample );
		return _bricks.TryGetValue( coordinate, out var brick ) && brick.TrySample( canonicalSample, out distance, out material );
	}

	public VoxelEditBakeReport Bake(
		IReadOnlyList<VoxelEditOp> operations,
		int operationCount,
		System.Func<Vector3, float> proceduralDistance,
		uint ruleVersion )
	{
		if ( operations is null || operationCount < 1 || operationCount > operations.Count )
			throw new System.ArgumentOutOfRangeException( nameof( operationCount ) );

		var brickCoordinates = new HashSet<Vector3Int>();
		for ( var operationIndex = 0; operationIndex < operationCount; operationIndex++ )
		{
			foreach ( var coordinate in EnumerateBrickCoordinates( VoxelEditJournal.GetBounds( operations[operationIndex] ) ) )
				brickCoordinates.Add( coordinate );
		}
		if ( brickCoordinates.Count > MaximumBakedBricks )
			return new VoxelEditBakeReport( false, $"Bake would require {brickCoordinates.Count:N0} bricks, exceeding the bounded capacity of {MaximumBakedBricks:N0}.", 0, operations.Count, Count, 0, 0 );

		var orderedCoordinates = brickCoordinates.ToList();
		orderedCoordinates.Sort( CompareCoordinates );
		var replacement = new Dictionary<Vector3Int, VoxelEditBrick>( _bricks );
		long evaluations = 0;
		var bakedThroughRevision = 0u;
		foreach ( var coordinate in orderedCoordinates )
		{
			var distances = new float[SampleCount];
			var materials = new byte[SampleCount];
			var brickOrigin = new Vector3( coordinate.x * BrickSize, coordinate.y * BrickSize, coordinate.z * BrickSize );
			var brickMaximum = brickOrigin + Vector3.One * BrickSize;
			var relevantOperations = new List<VoxelEditOp>();
			for ( var operationIndex = 0; operationIndex < operationCount; operationIndex++ )
			{
				var operationBounds = VoxelEditJournal.GetBounds( operations[operationIndex] );
				if ( operationBounds.Maxs.x < brickOrigin.x || operationBounds.Mins.x > brickMaximum.x ||
					operationBounds.Maxs.y < brickOrigin.y || operationBounds.Mins.y > brickMaximum.y ||
					operationBounds.Maxs.z < brickOrigin.z || operationBounds.Mins.z > brickMaximum.z ) continue;
				relevantOperations.Add( operations[operationIndex] );
			}
			for ( var z = 0; z <= BrickSize; z++ )
			for ( var y = 0; y <= BrickSize; y++ )
			for ( var x = 0; x <= BrickSize; x++ )
			{
				var canonicalSample = new Vector3( coordinate.x * BrickSize + x, coordinate.y * BrickSize + y, coordinate.z * BrickSize + z );
				var distance = proceduralDistance( canonicalSample );
				var material = distance < 0.0f ? VoxelMaterial.Terrain : VoxelMaterial.Air;
				if ( _bricks.TryGetValue( coordinate, out var priorBrick ) && priorBrick.TrySample( canonicalSample, out var priorDistance, out var priorMaterial ) )
				{
					distance = priorDistance;
					material = priorMaterial;
				}
				foreach ( var operation in relevantOperations )
				{
					distance = VoxelEditJournal.ApplyDistance( operation, canonicalSample, distance );
					var sourceMaterial = material == VoxelMaterial.Air ? VoxelMaterial.Terrain : material;
					material = VoxelEditJournal.ApplyMaterial( operation, canonicalSample, distance, sourceMaterial );
				}
				var index = GetIndex( x, y, z );
				distances[index] = distance;
				materials[index] = (byte)material;
				evaluations++;
			}
			var revision = operations[operationCount - 1].WorldRevision;
			bakedThroughRevision = System.Math.Max( bakedThroughRevision, revision );
			replacement[coordinate] = new VoxelEditBrick( coordinate, revision, ruleVersion, distances, materials );
		}

		_bricks.Clear();
		foreach ( var pair in replacement ) _bricks.Add( pair.Key, pair.Value );
		return new VoxelEditBakeReport( true, string.Empty, operationCount, operations.Count - operationCount, Count, evaluations, bakedThroughRevision );
	}

	public void Write( BinaryWriter writer )
	{
		writer.Write( FileMagic );
		writer.Write( FileVersion );
		writer.Write( _bricks.Count );
		foreach ( var pair in _bricks.OrderBy( pair => pair.Key.x ).ThenBy( pair => pair.Key.y ).ThenBy( pair => pair.Key.z ) )
		{
			var brick = pair.Value;
			writer.Write( brick.Coordinate.x );
			writer.Write( brick.Coordinate.y );
			writer.Write( brick.Coordinate.z );
			writer.Write( brick.WorldRevision );
			writer.Write( brick.RuleVersion );
			for ( var index = 0; index < VoxelEditBrick.SampleCount; index++ ) writer.Write( brick.Distances[index] );
			writer.Write( brick.Materials );
		}
	}

	public static VoxelEditBrickStore Read( BinaryReader reader, uint expectedRuleVersion, out bool ruleVersionMismatch )
	{
		ruleVersionMismatch = false;
		if ( reader.ReadInt32() != FileMagic || reader.ReadInt32() != FileVersion ) throw new InvalidDataException( "Voxel edit brick persistence format is not supported." );
		var count = reader.ReadInt32();
		if ( count < 0 || count > MaximumBakedBricks ) throw new InvalidDataException( "Voxel edit brick persistence exceeded its bounded brick capacity." );
		var store = new VoxelEditBrickStore();
		for ( var brickIndex = 0; brickIndex < count; brickIndex++ )
		{
			var coordinate = new Vector3Int( reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() );
			var worldRevision = reader.ReadUInt32();
			var ruleVersion = reader.ReadUInt32();
			var distances = new float[VoxelEditBrick.SampleCount];
			for ( var sampleIndex = 0; sampleIndex < distances.Length; sampleIndex++ ) distances[sampleIndex] = reader.ReadSingle();
			var materials = reader.ReadBytes( VoxelEditBrick.SampleCount );
			if ( materials.Length != VoxelEditBrick.SampleCount ) throw new EndOfStreamException( "Voxel edit brick material payload was truncated." );
			if ( ruleVersion != expectedRuleVersion ) ruleVersionMismatch = true;
			else store._bricks[coordinate] = new VoxelEditBrick( coordinate, worldRevision, ruleVersion, distances, materials );
		}
		return store;
	}

	public VoxelGpuEditBrick[] CreateGpuBricks( out float[] samples )
	{
		var ordered = _bricks.Values.OrderBy( brick => brick.Coordinate.x ).ThenBy( brick => brick.Coordinate.y ).ThenBy( brick => brick.Coordinate.z ).ToArray();
		var metadata = new VoxelGpuEditBrick[System.Math.Max( 1, ordered.Length )];
		samples = new float[System.Math.Max( 1, ordered.Length * VoxelEditBrick.SampleCount )];
		for ( var index = 0; index < ordered.Length; index++ )
		{
			var brick = ordered[index];
			var offset = index * VoxelEditBrick.SampleCount;
			System.Array.Copy( brick.Distances, 0, samples, offset, brick.Distances.Length );
			metadata[index] = new VoxelGpuEditBrick
			{
				OriginAndSize = new Vector4( brick.Coordinate.x * BrickSize, brick.Coordinate.y * BrickSize, brick.Coordinate.z * BrickSize, BrickSize ),
				SampleOffset = (uint)offset,
				WorldRevision = brick.WorldRevision,
				RuleVersion = brick.RuleVersion
			};
		}
		return metadata;
	}

	private static Vector3Int GetBrickCoordinate( Vector3 sample ) => new( FloorDiv( (int)System.MathF.Floor( sample.x ), BrickSize ), FloorDiv( (int)System.MathF.Floor( sample.y ), BrickSize ), FloorDiv( (int)System.MathF.Floor( sample.z ), BrickSize ) );

	private static IEnumerable<Vector3Int> EnumerateBrickCoordinates( BBox bounds )
	{
		var minimum = new Vector3Int( FloorDiv( (int)System.MathF.Floor( bounds.Mins.x ), BrickSize ), FloorDiv( (int)System.MathF.Floor( bounds.Mins.y ), BrickSize ), FloorDiv( (int)System.MathF.Floor( bounds.Mins.z ), BrickSize ) );
		var maximum = new Vector3Int( FloorDiv( (int)System.MathF.Ceiling( bounds.Maxs.x ), BrickSize ), FloorDiv( (int)System.MathF.Ceiling( bounds.Maxs.y ), BrickSize ), FloorDiv( (int)System.MathF.Ceiling( bounds.Maxs.z ), BrickSize ) );
		for ( var z = minimum.z; z <= maximum.z; z++ )
		for ( var y = minimum.y; y <= maximum.y; y++ )
		for ( var x = minimum.x; x <= maximum.x; x++ ) yield return new Vector3Int( x, y, z );
	}

	private static int GetIndex( int x, int y, int z ) => x + SampleSize * (y + SampleSize * z);
	private static int CompareCoordinates( Vector3Int left, Vector3Int right ) => left.z != right.z ? left.z.CompareTo( right.z ) : left.y != right.y ? left.y.CompareTo( right.y ) : left.x.CompareTo( right.x );
	private static int FloorDiv( int value, int divisor )
	{
		var quotient = value / divisor;
		return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
	}
}
