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

	float HashPosition( float3 position )
	{
		position = frac( position * 0.1031f );
		position += dot( position, position.yzx + 33.33f );
		return frac( (position.x + position.y) * position.z );
	}

	float SmoothNoise( float3 position )
	{
		float3 cell = floor( position );
		float3 blend = frac( position );
		blend = blend * blend * (3.0f - 2.0f * blend);

		float lower00 = lerp( HashPosition( cell + float3( 0.0f, 0.0f, 0.0f ) ), HashPosition( cell + float3( 1.0f, 0.0f, 0.0f ) ), blend.x );
		float lower10 = lerp( HashPosition( cell + float3( 0.0f, 1.0f, 0.0f ) ), HashPosition( cell + float3( 1.0f, 1.0f, 0.0f ) ), blend.x );
		float upper00 = lerp( HashPosition( cell + float3( 0.0f, 0.0f, 1.0f ) ), HashPosition( cell + float3( 1.0f, 0.0f, 1.0f ) ), blend.x );
		float upper10 = lerp( HashPosition( cell + float3( 0.0f, 1.0f, 1.0f ) ), HashPosition( cell + float3( 1.0f, 1.0f, 1.0f ) ), blend.x );

		float lower = lerp( lower00, lower10, blend.y );
		float upper = lerp( upper00, upper10, blend.y );
		return lerp( lower, upper, blend.z );
	}
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material material = Material::Init( i );
		float3 worldPosition = material.WorldPosition;
		float broadVariation = SmoothNoise( worldPosition / 128.0f );
		float fineVariation = SmoothNoise( worldPosition / 32.0f );
		float colorBlend = saturate( broadVariation * 0.7f + fineVariation * 0.3f );
		float3 darkGrass = float3( 0.12f, 0.22f, 0.13f );
		float3 lightGrass = float3( 0.34f, 0.43f, 0.31f );
		material.Albedo = SrgbGammaToLinear( lerp( darkGrass, lightGrass, colorBlend ) );
		material.Roughness = 0.95f;
		material.Metalness = 0.0f;

		return ShadingModelStandard::Shade( material );
	}
}
