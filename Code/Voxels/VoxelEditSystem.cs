using System.Runtime.InteropServices;

public enum VoxelEditShape : uint
{
	Sphere,
	Capsule,
	OrientedBox
}

public enum VoxelCsgOperation : uint
{
	Add,
	Subtract,
	SmoothAdd,
	SmoothSubtract,
	MaterialPaint
}

public struct VoxelEditOp
{
	public ulong EditId;
	public uint WorldRevision;
	public VoxelEditShape Shape;
	public VoxelCsgOperation Operation;
	public Vector3 Position;
	public Rotation Rotation;
	public Vector3 Size;
	public float Smoothness;
	public ushort MaterialId;
}

[StructLayout( LayoutKind.Sequential, Pack = 4, Size = 80 )]
internal struct VoxelGpuEditOp
{
	public Vector4 PositionAndSmoothness;
	public Vector4 Rotation;
	public Vector4 SizeAndMaterial;
	public uint Shape;
	public uint Operation;
	public uint WorldRevision;
	public uint Reserved0;
	public Vector4 BoundsMin;
}

internal sealed class VoxelEditJournal
{
	public const int MaximumGpuOperations = 1024;
	private readonly object _gate = new();
	private readonly List<VoxelEditOp> _operations = new();
	private ulong _nextEditId = 1;
	private uint _worldRevision;

	public int Count { get { lock ( _gate ) return _operations.Count; } }
	public uint WorldRevision { get { lock ( _gate ) return _worldRevision; } }

	public VoxelEditOp Append( VoxelEditOp operation )
	{
		lock ( _gate )
		{
			if ( _operations.Count >= MaximumGpuOperations )
				throw new System.InvalidOperationException( $"The active voxel edit journal reached its Phase Five safety limit of {MaximumGpuOperations} operations; baked edit bricks must compact it before more edits can be accepted." );

			Validate( operation );
			operation.EditId = _nextEditId++;
			operation.WorldRevision = checked( ++_worldRevision );
			operation.Rotation = operation.Rotation.Normal;
			operation.Size = new Vector3( System.MathF.Abs( operation.Size.x ), System.MathF.Abs( operation.Size.y ), System.MathF.Abs( operation.Size.z ) );
			operation.Smoothness = System.MathF.Max( 0.0f, operation.Smoothness );
			_operations.Add( operation );
			return operation;
		}
	}

	public float EvaluateDistance( Vector3 canonicalSample, float proceduralDistance )
		=> EvaluateDistance( CreateOperationSnapshot(), canonicalSample, proceduralDistance );

	public static float EvaluateDistance( IReadOnlyList<VoxelEditOp> operations, Vector3 canonicalSample, float proceduralDistance )
	{
		var distance = proceduralDistance;
		foreach ( var operation in operations )
		{
			if ( operation.Operation == VoxelCsgOperation.MaterialPaint || !Contains( operation, canonicalSample ) ) continue;
			distance = ApplyDistance( distance, ShapeDistance( operation, canonicalSample ), operation.Operation, operation.Smoothness );
		}
		return distance;
	}

	public VoxelMaterial EvaluateMaterial( Vector3 canonicalSample, float distance, VoxelMaterial proceduralMaterial )
		=> EvaluateMaterial( CreateOperationSnapshot(), canonicalSample, distance, proceduralMaterial );

	public static VoxelMaterial EvaluateMaterial( IReadOnlyList<VoxelEditOp> operations, Vector3 canonicalSample, float distance, VoxelMaterial proceduralMaterial )
	{
		var material = distance < 0.0f ? proceduralMaterial : VoxelMaterial.Air;
		foreach ( var operation in operations )
		{
			if ( operation.Operation != VoxelCsgOperation.MaterialPaint || !Contains( operation, canonicalSample ) || ShapeDistance( operation, canonicalSample ) > 0.0f ) continue;
			material = (VoxelMaterial)System.Math.Clamp( operation.MaterialId, (ushort)0, (ushort)byte.MaxValue );
		}
		return distance < 0.0f ? material : VoxelMaterial.Air;
	}

	public VoxelGpuEditOp[] CreateGpuSnapshot()
		=> CreateGpuSnapshot( out _ );

	public VoxelGpuEditOp[] CreateGpuSnapshot( out int operationCount )
	{
		var operations = CreateOperationSnapshot();
		operationCount = operations.Length;
		var result = new VoxelGpuEditOp[System.Math.Max( 1, operations.Length )];
		for ( var index = 0; index < operations.Length; index++ )
		{
			var operation = operations[index];
			var bounds = GetBounds( operation );
			var boundsRadius = (bounds.Maxs - operation.Position).Length;
			result[index] = new VoxelGpuEditOp
			{
				PositionAndSmoothness = new Vector4( operation.Position, operation.Smoothness ),
				Rotation = new Vector4( operation.Rotation.x, operation.Rotation.y, operation.Rotation.z, operation.Rotation.w ),
				SizeAndMaterial = new Vector4( operation.Size, operation.MaterialId ),
				Shape = (uint)operation.Shape,
				Operation = (uint)operation.Operation,
				WorldRevision = operation.WorldRevision,
				BoundsMin = new Vector4( bounds.Mins, boundsRadius )
			};
		}
		return result;
	}

	public VoxelEditOp[] CreateOperationSnapshot()
	{
		lock ( _gate ) return _operations.ToArray();
	}

	public static BBox GetBounds( VoxelEditOp operation )
	{
		var radius = operation.Shape switch
		{
			VoxelEditShape.Sphere => operation.Size.x,
			VoxelEditShape.Capsule => operation.Size.x + operation.Size.y,
			_ => operation.Size.Length
		};
		radius += operation.Smoothness;
		return new BBox( operation.Position - Vector3.One * radius, operation.Position + Vector3.One * radius );
	}

	private static bool Contains( VoxelEditOp operation, Vector3 point ) => GetBounds( operation ).Contains( point );

	private static float ShapeDistance( VoxelEditOp operation, Vector3 point )
	{
		var local = operation.Rotation.Inverse * (point - operation.Position);
		return operation.Shape switch
		{
			VoxelEditShape.Sphere => local.Length - operation.Size.x,
			VoxelEditShape.Capsule => CapsuleDistance( local, operation.Size.x, operation.Size.y ),
			VoxelEditShape.OrientedBox => BoxDistance( local, operation.Size ),
			_ => float.MaxValue
		};
	}

	private static float CapsuleDistance( Vector3 point, float radius, float halfHeight )
	{
		point.z -= System.Math.Clamp( point.z, -halfHeight, halfHeight );
		return point.Length - radius;
	}

	private static float BoxDistance( Vector3 point, Vector3 halfExtents )
	{
		var delta = new Vector3( System.MathF.Abs( point.x ), System.MathF.Abs( point.y ), System.MathF.Abs( point.z ) ) - halfExtents;
		var outside = new Vector3( System.MathF.Max( delta.x, 0.0f ), System.MathF.Max( delta.y, 0.0f ), System.MathF.Max( delta.z, 0.0f ) );
		return outside.Length + System.MathF.Min( System.MathF.Max( delta.x, System.MathF.Max( delta.y, delta.z ) ), 0.0f );
	}

	private static float ApplyDistance( float terrain, float shape, VoxelCsgOperation operation, float smoothness ) => operation switch
	{
		VoxelCsgOperation.Add => System.MathF.Min( terrain, shape ),
		VoxelCsgOperation.Subtract => System.MathF.Max( terrain, -shape ),
		VoxelCsgOperation.SmoothAdd => SmoothMinimum( terrain, shape, smoothness ),
		VoxelCsgOperation.SmoothSubtract => SmoothMaximum( terrain, -shape, smoothness ),
		_ => terrain
	};

	private static float SmoothMinimum( float first, float second, float smoothing )
	{
		if ( smoothing <= 0.0001f ) return System.MathF.Min( first, second );
		var blend = System.Math.Clamp( 0.5f + 0.5f * (second - first) / smoothing, 0.0f, 1.0f );
		return second + (first - second) * blend - smoothing * blend * (1.0f - blend);
	}

	private static float SmoothMaximum( float first, float second, float smoothing ) => -SmoothMinimum( -first, -second, smoothing );

	private static void Validate( VoxelEditOp operation )
	{
		if ( !System.Enum.IsDefined( operation.Shape ) ) throw new System.ArgumentOutOfRangeException( nameof( operation.Shape ) );
		if ( !System.Enum.IsDefined( operation.Operation ) ) throw new System.ArgumentOutOfRangeException( nameof( operation.Operation ) );
		if ( operation.Size.x <= 0.0f || operation.Size.y < 0.0f || operation.Size.z < 0.0f ) throw new System.ArgumentOutOfRangeException( nameof( operation.Size ) );
	}
}

internal static class VoxelEditInvalidation
{
	public static HashSet<Vector3Int> GetCpuChunks( VoxelEditOp operation, int chunkSize )
	{
		var bounds = VoxelEditJournal.GetBounds( operation );
		return EnumerateBlocks( bounds, chunkSize, 0, new Vector3Int( 0, 0, -chunkSize ) );
	}

	public static HashSet<VoxelVisualBlockKey> GetVisualBlocks( VoxelEditOp operation, IEnumerable<VoxelVisualBlockKey> candidates, int chunkSize )
	{
		var result = new HashSet<VoxelVisualBlockKey>();
		foreach ( var key in candidates )
			if ( OverlapsVisualBlock( operation, key, chunkSize ) ) result.Add( key );
		return result;
	}

	public static bool OverlapsVisualBlock( VoxelEditOp operation, VoxelVisualBlockKey key, int chunkSize )
	{
		var step = 1 << key.Lod;
		var origin = VoxelGpuCanonicalCoordinates.CanonicalBlockOriginSamples( key.Coordinate, key.Lod, chunkSize );
		var halo = step * 2.0f;
		var bounds = new BBox( origin - Vector3.One * halo, origin + Vector3.One * (chunkSize * step + halo) );
		return bounds.Overlaps( VoxelEditJournal.GetBounds( operation ) );
	}

	private static HashSet<Vector3Int> EnumerateBlocks( BBox bounds, int blockSize, int lod, Vector3Int originOffset )
	{
		var step = checked( blockSize * (1 << lod) );
		var minimum = bounds.Mins - originOffset;
		var maximum = bounds.Maxs - originOffset;
		var min = new Vector3Int( FloorDiv( (int)System.MathF.Floor( minimum.x ), step ), FloorDiv( (int)System.MathF.Floor( minimum.y ), step ), FloorDiv( (int)System.MathF.Floor( minimum.z ), step ) );
		var max = new Vector3Int( FloorDiv( (int)System.MathF.Ceiling( maximum.x ), step ), FloorDiv( (int)System.MathF.Ceiling( maximum.y ), step ), FloorDiv( (int)System.MathF.Ceiling( maximum.z ), step ) );
		var result = new HashSet<Vector3Int>();
		for ( var z = min.z; z <= max.z; z++ )
		for ( var y = min.y; y <= max.y; y++ )
		for ( var x = min.x; x <= max.x; x++ ) result.Add( new Vector3Int( x, y, z ) );
		return result;
	}

	private static int FloorDiv( int value, int divisor )
	{
		var quotient = value / divisor;
		return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
	}
}
