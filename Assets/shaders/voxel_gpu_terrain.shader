HEADER
{
	Description = "Persistent fixed-LOD GPU voxel terrain";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
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
	RenderState( BlendEnable, false );
	RenderState( DepthEnable, true );
	RenderState( DepthWriteEnable, true );
	RenderState( CullMode, BACK );

	float4 MainPs( PixelInput input ) : SV_Target0
	{
		float3 normal = normalize( input.vNormalWs );
		float3 sunDirection = normalize( float3( -0.35f, -0.45f, 0.82f ) );
		float directLight = saturate( dot( normal, sunDirection ) );
		float skyLight = saturate( normal.z * 0.5f + 0.5f );
		float lighting = 0.22f + directLight * 0.63f + skyLight * 0.15f;
		float3 grass = SrgbGammaToLinear( float3( 0.13f, 0.46f, 0.055f ) );
		return float4( grass * lighting, 1.0f );
	}
}
