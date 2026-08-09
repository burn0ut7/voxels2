MODES
{
	Default();
}
// GPU LOD crack plan: shared canonical terrain field live-reload marker.
// Versioned persistent terrain Pass A density and material rules for fixed-LOD batches.
CS
{
	#include "system.fxc"
	#include "voxel_terrain_field.fxc"
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
	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint count = (uint)HaloSampleCount * (uint)BatchSize;
		if ( id.x >= count ) return;
		uint block = id.x / (uint)HaloSampleCount;
		uint localIndex = id.x - block * (uint)HaloSampleCount;
		float3 localSample = float3( Decode3D( localIndex, HaloSize ) ) - 1.0f;
		float3 sample = BlockRequests[block].SampleOrigin.xyz + localSample * BlockRequests[block].SampleScale.xyz;
		DensitySamples[id.x] = EvaluateTerrainDensity( sample, SdfClampDistance, SimplexFrequency, SimplexAmplitude, SimplexBaseHeight, SimplexSeed );
	}
}
