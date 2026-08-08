MODES
{
	Default();
}
CS
{
	#include "system.fxc"
	StructuredBuffer<uint> EdgeFlags < Attribute( "EdgeFlags" ); >;
	RWStructuredBuffer<uint> EdgeVertexIds < Attribute( "EdgeVertexIds" ); >;
	RWStructuredBuffer<uint3> Cells < Attribute( "Cells" ); >;
	RWStructuredBuffer<uint> Statistics < Attribute( "Statistics" ); >;
	int CellCount < Attribute( "CellCount" ); >;
	int EdgeSlotCount < Attribute( "EdgeSlotCount" ); >;
	int MaxVertices < Attribute( "MaxVertices" ); >;
	int MaxIndices < Attribute( "MaxIndices" ); >;
	[numthreads( 1, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint vertices=0; for(uint e=0;e<(uint)EdgeSlotCount;e++) if(EdgeFlags[e]!=0) EdgeVertexIds[e]=vertices++;
		uint indices=0; for(uint c=0;c<(uint)CellCount;c++){ Cells[c].z=indices; indices+=Cells[c].y; }
		Statistics[0]=indices; Statistics[1]=indices>0?1:0; Statistics[2]=0; Statistics[3]=0; Statistics[4]=0;
		Statistics[5]=vertices; Statistics[6]=indices; Statistics[9]=(vertices>(uint)MaxVertices||indices>(uint)MaxIndices)?1:0;
	}
}
