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
		float4 ReservedOrigin;
		int CoordinateX;
		int CoordinateY;
		int CoordinateZ;
		int Lod;
		uint RuleVersion;
		uint Generation;
		uint ResidentSlot;
		uint Reserved;
	};
	StructuredBuffer<BlockRequest> BlockRequests < Attribute( "BlockRequests" ); >;
	RWStructuredBuffer<float> DensitySamples < Attribute( "DensitySamples" ); >;
	int HaloSize < Attribute( "HaloSize" ); >;
	int HaloSampleCount < Attribute( "HaloSampleCount" ); >;
	int BatchSize < Attribute( "BatchSize" ); >;
	float SdfClampDistance < Attribute( "SdfClampDistance" ); >;
	uint3 Decode3D( uint index, uint size )
	{
		uint plane = size * size, z = index / plane, remainder = index - z * plane, y = remainder / size;
		return uint3( remainder - y * size, y, z );
	}
	float TerrainRuleStressV1( float3 p )
	{
		float rolling = sin( p.x * 0.0031f ) * 2.5f + cos( p.y * 0.0027f ) * 2.0f;
		// Keep the Phase 2B stress surface safely inside the project's single vertical
		// chunk (-32..0 voxel coordinates), instead of straddling its upper boundary.
		return p.z + 16.0f - rolling;
	}
	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint count = (uint)HaloSampleCount * (uint)BatchSize;
		if ( id.x >= count ) return;
		uint block = id.x / (uint)HaloSampleCount;
		uint localIndex = id.x - block * (uint)HaloSampleCount;
		float3 localSample = float3( Decode3D( localIndex, HaloSize ) ) - 1.0f;
		float3 sample = BlockRequests[block].SampleOrigin.xyz + localSample;
		float density = sample.z;
		if ( BlockRequests[block].RuleVersion != 0 ) density = TerrainRuleStressV1( sample );
		DensitySamples[id.x] = clamp( density, -SdfClampDistance, SdfClampDistance );
	}
}
