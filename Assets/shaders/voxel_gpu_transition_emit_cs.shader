MODES
{
	Default();
}
CS
{
	#include "system.fxc"
	StructuredBuffer<uint> Cases < Attribute( "Cases" ); >;
	StructuredBuffer<uint> CellClass < Attribute( "CellClass" ); >;
	StructuredBuffer<uint> CellGeometry < Attribute( "CellGeometry" ); >;
	StructuredBuffer<uint> CellTriangles < Attribute( "CellTriangles" ); >;
	StructuredBuffer<uint> VertexData < Attribute( "VertexData" ); >;
	RWStructuredBuffer<uint> Output < Attribute( "Output" ); >;
	static const uint VertexDataStride = 12u;

	[numthreads(64,1,1)]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if ( id.x >= 512 ) return;
		uint caseCode = Cases[id.x];
		uint transitionClass = CellClass[caseCode] & 0x7Fu;
		uint geometry = CellGeometry[transitionClass];
		uint vertexCount = geometry >> 4;
		uint triangleCount = geometry & 0xFu;
		uint outputBase = id.x * 50u;
		Output[outputBase] = vertexCount;
		Output[outputBase + 1u] = triangleCount * 3u;
		for ( uint vertex = 0; vertex < 12u; vertex++ )
			if ( vertex < vertexCount ) Output[outputBase + 2u + vertex] = VertexData[id.x * VertexDataStride + vertex];
			else Output[outputBase + 2u + vertex] = 0u;
		for ( uint index = 0; index < 36u; index++ )
			Output[outputBase + 14u + index] = index < triangleCount * 3u ? CellTriangles[transitionClass * 36u + index] : 0u;
	}
}
