MODES
{
	Default();
}
CS
{
	#include "system.fxc"
	struct TransitionCell
	{
		float4 Origin; float4 U; float4 V; float4 W;
		float4 Density0; float4 Density1; float4 Density2; float4 Density3;
		uint CaseCode; uint VertexCount; uint IndexCount; uint VertexOffset; uint IndexOffset;
	};
	struct VoxelGpuVertex { float3 Position; float3 Normal; float3 Tangent; float2 TexCoord; };
	StructuredBuffer<TransitionCell> Cells < Attribute( "Cells" ); >;
	StructuredBuffer<uint> CellClass < Attribute( "CellClass" ); >;
	StructuredBuffer<uint> CellGeometry < Attribute( "CellGeometry" ); >;
	StructuredBuffer<uint> CellTriangles < Attribute( "CellTriangles" ); >;
	StructuredBuffer<uint> VertexData < Attribute( "VertexData" ); >;
	RWStructuredBuffer<VoxelGpuVertex> OutputVertices < Attribute( "OutputVertices" ); >;
	RWStructuredBuffer<uint> OutputIndices < Attribute( "OutputIndices" ); >;
	float VoxelSize < Attribute( "VoxelSize" ); >;
	uint CellCount < Attribute( "CellCount" ); >;
	float Density( TransitionCell cell, uint corner )
	{
		if ( corner < 4u ) return cell.Density0[corner];
		if ( corner < 8u ) return cell.Density1[corner - 4u];
		if ( corner < 12u ) return cell.Density2[corner - 8u];
		return cell.Density3.x;
	}
	float3 Position( TransitionCell cell, uint corner )
	{
		if ( corner < 9u )
		{
			uint u = corner % 3u, v = corner / 3u;
			return cell.Origin.xyz + cell.U.xyz * (float)u * 2.0f + cell.V.xyz * (float)v * 2.0f;
		}
		uint u = (corner == 10u || corner == 12u) ? 2u : 0u;
		uint v = (corner == 11u || corner == 12u) ? 2u : 0u;
		return cell.Origin.xyz + cell.U.xyz * (float)u * 2.0f + cell.V.xyz * (float)v * 2.0f + cell.W.xyz;
	}
	float3 Safe( float3 value, float3 fallback )
	{
		float lengthSquared = dot( value, value );
		return lengthSquared > 1e-12f ? value * rsqrt( lengthSquared ) : fallback;
	}
	[numthreads(64,1,1)] void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if ( id.x >= CellCount ) return;
		TransitionCell cell = Cells[id.x];
		uint transitionClass = CellClass[cell.CaseCode] & 0x7Fu;
		uint geometry = CellGeometry[transitionClass];
		uint vertexCount = geometry >> 4;
		uint triangleCount = geometry & 0xFu;
		if ( vertexCount != cell.VertexCount || triangleCount * 3u != cell.IndexCount ) return;
		for ( uint vertex = 0u; vertex < 12u; vertex++ )
		{
			if ( vertex >= vertexCount ) break;
			uint edge = VertexData[cell.CaseCode * 12u + vertex];
			uint first = edge & 0x0Fu, second = (edge >> 4u) & 0x0Fu;
			float firstDensity = Density( cell, first ), secondDensity = Density( cell, second );
			float denominator = firstDensity - secondDensity;
			float t = abs( denominator ) > 0.000001f ? firstDensity / denominator : 0.5f;
			t = clamp( t, 0.0f, 1.0f );
			float3 position = lerp( Position( cell, first ), Position( cell, second ), t );
			float3 normal = Safe( -cell.W.xyz, float3( 0, 0, 1 ) );
			VoxelGpuVertex output;
			output.Position = position;
			output.Normal = normal;
			output.Tangent = Safe( cross( float3( 0, 0, 1 ), normal ), float3( 1, 0, 0 ) );
			output.TexCoord = position.xy / 128.0f;
			OutputVertices[cell.VertexOffset + vertex] = output;
		}
		for ( uint index = 0u; index < 36u; index++ )
		{
			if ( index >= triangleCount * 3u ) break;
			OutputIndices[cell.IndexOffset + index] = cell.VertexOffset + CellTriangles[transitionClass * 36u + index];
		}
	}
}
