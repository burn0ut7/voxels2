MODES
{
	Default();
}
// Phase 2 batched density.
CS
{
	#include "system.fxc"
	struct BlockInput { float4 SampleOrigin; float4 DrawOrigin; };
	StructuredBuffer<BlockInput> Blocks < Attribute( "Blocks" ); >;
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
	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint count = (uint)HaloSampleCount * (uint)BatchSize;
		if ( id.x >= count ) return;
		uint block = id.x / (uint)HaloSampleCount, localIndex = id.x - block * (uint)HaloSampleCount;
		float3 localSample = float3( Decode3D( localIndex, HaloSize ) ) - 1.0f;
		DensitySamples[id.x] = clamp( Blocks[block].SampleOrigin.z + localSample.z, -SdfClampDistance, SdfClampDistance );
	}
}
