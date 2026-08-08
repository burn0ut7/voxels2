MODES
{
	Default();
}
CS
{
	#include "system.fxc"
	RWStructuredBuffer<float> DensitySamples < Attribute( "DensitySamples" ); >;
	int HaloSize < Attribute( "HaloSize" ); >;
	float SdfClampDistance < Attribute( "SdfClampDistance" ); >;
	float3 SampleOrigin < Attribute( "SampleOrigin" ); >;
	uint3 Decode3D( uint index, uint size )
	{
		uint plane = size * size, z = index / plane, remainder = index - z * plane, y = remainder / size;
		return uint3( remainder - y * size, y, z );
	}
	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint count = HaloSize * HaloSize * HaloSize;
		if ( id.x >= count ) return;
		float3 localSample = float3( Decode3D( id.x, HaloSize ) ) - 1.0f;
		DensitySamples[id.x] = clamp( SampleOrigin.z + localSample.z, -SdfClampDistance, SdfClampDistance );
	}
}
