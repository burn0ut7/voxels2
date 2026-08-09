HEADER
{
	Description = "Production GPU voxel terrain material";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth();
}

COMMON
{
	#include "common/shared.hlsl"
	struct ResidentDescriptor
	{
		float4 DrawOrigin;
		float4 BoundsMin;
		float4 BoundsMax;
		uint Generation;
		uint VertexOffset;
		uint IndexOffset;
		uint IndexCount;
	};
	StructuredBuffer<ResidentDescriptor> TerrainResidents < Attribute( "TerrainResidents" ); >;
}

struct VertexInput
{
	float3 LocalPosition : POSITION < Semantic( None ); >;
	float3 Normal : NORMAL < Semantic( None ); >;
	float3 Tangent : TANGENT < Semantic( None ); >;
	float4 Color : COLOR0 < Semantic( None ); >;
	float2 TextureCoords : TEXCOORD0 < Semantic( None ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	PixelInput MainVs( const VertexInput input )
	{
		PixelInput output;
		float3 worldPosition = input.LocalPosition;
		output.vPositionWs = worldPosition - g_vHighPrecisionLightingOffsetWs.xyz;
		output.vPositionPs = Position3WsToPs( worldPosition );
		output.vNormalWs = input.Normal;
		output.vVertexColor = float4( 1.0f, 1.0f, 1.0f, 1.0f );
		output.vTextureCoords = float4( input.TextureCoords, input.TextureCoords );
		output.vTangentUWs = input.Tangent;
		output.vTangentVWs = normalize( cross( input.Normal, input.Tangent ) );
		return output;
	}
}

PS
{
	#include "common/pixel.hlsl"

	float4 MainPs( PixelInput input ) : SV_Target0
	{
		Material material = Material::Init( input );
		float3 worldPosition = material.WorldPosition;
		float broadVariation = frac( sin( dot( floor( worldPosition / 128.0f ), float3( 12.9898f, 78.233f, 37.719f ) ) ) * 43758.5453f );
		float fineVariation = frac( sin( dot( floor( worldPosition / 32.0f ), float3( 39.3468f, 11.135f, 83.155f ) ) ) * 24634.6345f );
		float colorBlend = saturate( broadVariation * 0.7f + fineVariation * 0.3f );
		float3 darkTerrain = float3( 0.12f, 0.22f, 0.13f );
		float3 lightTerrain = float3( 0.34f, 0.43f, 0.31f );
		material.Albedo = SrgbGammaToLinear( lerp( darkTerrain, lightTerrain, colorBlend ) );
		material.Roughness = 0.95f;
		material.Metalness = 0.0f;
		return ShadingModelStandard::Shade( input, material );
	}
}
