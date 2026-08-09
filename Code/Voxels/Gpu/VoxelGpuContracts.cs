using System.Runtime.InteropServices;

internal readonly record struct VoxelVisualBlockKey( Vector3Int Coordinate, int Lod, int RuleVersion, uint EditRevision = 0 );

[StructLayout( LayoutKind.Sequential, Pack = 4, Size = 64 )]
internal struct VoxelGpuBlockRequest
{
	public Vector4 SampleOrigin;
	public Vector4 ReservedOrigin;
	public int CoordinateX;
	public int CoordinateY;
	public int CoordinateZ;
	public int Lod;
	public uint RuleVersion;
	public uint Generation;
	public uint ResidentSlot;
	public uint Reserved;
}

[StructLayout( LayoutKind.Sequential, Pack = 4, Size = 32 )]
internal struct VoxelGpuCountResult
{
	public uint VertexCount;
	public uint IndexCount;
	public uint Generation;
	public uint RequestIndex;
	public uint Overflow;
	public uint ActiveCells;
	public uint Reserved0;
	public uint Reserved1;
}

[StructLayout( LayoutKind.Sequential, Pack = 4, Size = 64 )]
internal struct VoxelGpuAllocationDescriptor
{
	public uint VertexOffset;
	public uint VertexCapacity;
	public uint IndexOffset;
	public uint IndexCapacity;
	public uint Generation;
	public uint ResidentSlot;
	public uint RequestIndex;
	public uint Flags;
	public Vector4 DrawOrigin;
	public Vector4 Reserved;
}

[StructLayout( LayoutKind.Sequential, Pack = 4, Size = 64 )]
internal struct VoxelGpuResidentDescriptor
{
	public Vector4 DrawOrigin;
	public Vector4 BoundsMin;
	public Vector4 BoundsMax;
	public uint Generation;
	public uint VertexOffset;
	public uint IndexOffset;
	public uint IndexCount;
}

internal readonly record struct VoxelGpuPoolRange( int Offset, int Count )
{
	public bool IsEmpty => Count == 0;
	public int End => checked( Offset + Count );
}

internal readonly record struct VoxelGpuAllocationHandle(
	VoxelGpuPoolRange Vertices,
	VoxelGpuPoolRange Indices,
	uint Generation )
{
	public bool IsEmpty => Vertices.IsEmpty && Indices.IsEmpty;
}

internal static class VoxelGpuContractValidation
{
	public static void AssertLayouts()
	{
		AssertGpuStride<VoxelGpuBlockRequest>( 64 );
		AssertGpuStride<VoxelGpuCountResult>( 32 );
		AssertGpuStride<VoxelGpuAllocationDescriptor>( 64 );
		AssertGpuStride<VoxelGpuResidentDescriptor>( 64 );
	}

	private static void AssertGpuStride<T>( int expected ) where T : unmanaged
	{
		using var probe = new GpuBuffer<T>( 1, GpuBuffer.UsageFlags.Structured, $"{typeof( T ).Name} ABI Probe" );
		var actual = probe.ElementSize;
		if ( actual != expected || actual % 16 != 0 )
		{
			throw new System.InvalidOperationException( $"GPU contract {typeof( T ).Name} is {actual} bytes; expected {expected}." );
		}
	}
}
