MODES
{
	Default();
}
// GPU LOD crack plan: stable near-zero terrain field with one height evaluation per column.
// Versioned persistent terrain Pass A density rules with revisioned sparse edits and vertical column reuse.
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
		uint columnSampleCount = (uint)HaloSize * (uint)HaloSize;
		uint count = columnSampleCount * (uint)BatchSize;
		if ( id.x >= count ) return;
		uint block = id.x / columnSampleCount;
		uint localColumn = id.x - block * columnSampleCount;
		uint localY = localColumn / (uint)HaloSize;
		uint localX = localColumn - localY * (uint)HaloSize;
		float2 localSampleXY = float2( localX, localY ) - 1.0f;
		float2 sampleXY = BlockRequests[block].SampleOrigin.xy + localSampleXY * BlockRequests[block].SampleScale.xy;
		float surfaceHeight = SimplexBaseHeight + VoxelTerrainSimplexNoise( sampleXY * SimplexFrequency, SimplexSeed ) * SimplexAmplitude;
		[loop] for ( uint localZ = 0; localZ < (uint)HaloSize; localZ++ )
		{
			float sampleZ = BlockRequests[block].SampleOrigin.z + ((float)localZ - 1.0f) * BlockRequests[block].SampleScale.z;
			float3 sample = float3( sampleXY, sampleZ );
			uint localIndex = localX + localY * (uint)HaloSize + localZ * (uint)HaloSize * (uint)HaloSize;
			uint outputIndex = block * (uint)HaloSampleCount + localIndex;
			DensitySamples[outputIndex] = EvaluateEditedTerrainDensityFromSurfaceHeight( sample, (uint)BlockRequests[block].SampleScale.w, surfaceHeight, SdfClampDistance );
		}
	}
}
