MODES
{
	Default();
}
// Versioned persistent terrain Pass A density and material rules for fixed-LOD batches.
CS
{
	#include "system.fxc"
	struct BlockRequest
	{
		float4 SampleOrigin;
		float4 SampleScale;
		int CoordinateX;
		int CoordinateY;
		int CoordinateZ;
		int Lod;
		uint RuleVersion;
		uint Generation;
		uint ResidentSlot;
		uint TransitionFaceMask;
	};
	StructuredBuffer<BlockRequest> BlockRequests < Attribute( "BlockRequests" ); >;
	RWStructuredBuffer<float> DensitySamples < Attribute( "DensitySamples" ); >;
	int HaloSize < Attribute( "HaloSize" ); >;
	int HaloSampleCount < Attribute( "HaloSampleCount" ); >;
	int BatchSize < Attribute( "BatchSize" ); >;
	float SdfClampDistance < Attribute( "SdfClampDistance" ); >;
	float SimplexFrequency < Attribute( "SimplexFrequency" ); >;
	float SimplexAmplitude < Attribute( "SimplexAmplitude" ); >;
	float SimplexBaseHeight < Attribute( "SimplexBaseHeight" ); >;
	int SimplexSeed < Attribute( "SimplexSeed" ); >;
	uint3 Decode3D( uint index, uint size )
	{
		uint plane = size * size, z = index / plane, remainder = index - z * plane, y = remainder / size;
		return uint3( remainder - y * size, y, z );
	}
	int Hash( int x, int y, int seed )
	{
		int value = seed + x * 374761393 + y * 668265263;
		value = (value ^ (value >> 13)) * 1274126177;
		return value ^ (value >> 16);
	}
	float2 Gradient( int index )
	{
		if ( index == 0 ) return float2( 1.0f, 0.0f );
		if ( index == 1 ) return float2( -1.0f, 0.0f );
		if ( index == 2 ) return float2( 0.0f, 1.0f );
		if ( index == 3 ) return float2( 0.0f, -1.0f );
		if ( index == 4 ) return float2( 0.70710677f, 0.70710677f );
		if ( index == 5 ) return float2( -0.70710677f, 0.70710677f );
		if ( index == 6 ) return float2( 0.70710677f, -0.70710677f );
		return float2( -0.70710677f, -0.70710677f );
	}
	float SimplexNoise( float2 point )
	{
		const float skewFactor = 0.3660254038f;
		const float unskewFactor = 0.2113248654f;
		float skewed = (point.x + point.y) * skewFactor;
		int cellX = (int)floor( point.x + skewed );
		int cellY = (int)floor( point.y + skewed );
		float cellOffset = (cellX + cellY) * unskewFactor;
		float2 offset = point - (float2( cellX, cellY ) - cellOffset);
		int secondCornerX = offset.x > offset.y ? 1 : 0;
		int secondCornerY = offset.x > offset.y ? 0 : 1;
		float2 second = offset - float2( secondCornerX, secondCornerY ) + unskewFactor;
		float2 third = offset - 1.0f + 2.0f * unskewFactor;
		float value = 0.0f;
		float radius = 0.5f - dot( offset, offset );
		if ( radius > 0.0f ) value += radius * radius * radius * radius * dot( Gradient( Hash( cellX, cellY, SimplexSeed ) & 7 ), offset );
		radius = 0.5f - dot( second, second );
		if ( radius > 0.0f ) value += radius * radius * radius * radius * dot( Gradient( Hash( cellX + secondCornerX, cellY + secondCornerY, SimplexSeed ) & 7 ), second );
		radius = 0.5f - dot( third, third );
		if ( radius > 0.0f ) value += radius * radius * radius * radius * dot( Gradient( Hash( cellX + 1, cellY + 1, SimplexSeed ) & 7 ), third );
		return 70.0f * value;
	}
	float TerrainDensity( float3 p )
	{
		float surfaceHeight = SimplexBaseHeight + SimplexNoise( p.xy * SimplexFrequency ) * SimplexAmplitude;
		return p.z - surfaceHeight;
	}
	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint count = (uint)HaloSampleCount * (uint)BatchSize;
		if ( id.x >= count ) return;
		uint block = id.x / (uint)HaloSampleCount;
		uint localIndex = id.x - block * (uint)HaloSampleCount;
		float3 localSample = float3( Decode3D( localIndex, HaloSize ) ) - 1.0f;
		float3 sample = BlockRequests[block].SampleOrigin.xyz + localSample * BlockRequests[block].SampleScale.xyz;
		DensitySamples[id.x] = clamp( TerrainDensity( sample ), -SdfClampDistance, SdfClampDistance );
	}
}
