MODES
{
	Default();
}
CS
{
	#include "system.fxc"
	RWStructuredBuffer<uint3> Cells < Attribute( "Cells" ); >;
	RWStructuredBuffer<uint> EdgeFlags < Attribute( "EdgeFlags" ); >;
	RWStructuredBuffer<uint> EdgeVertexIds < Attribute( "EdgeVertexIds" ); >;
	RWStructuredBuffer<uint> Statistics < Attribute( "Statistics" ); >;
	int CellCount < Attribute( "CellCount" ); >;
	int EdgeSlotCount < Attribute( "EdgeSlotCount" ); >;
	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint index = id.x;
		if ( index < (uint)CellCount ) Cells[index] = uint3( 0, 0, 0 );
		if ( index < (uint)EdgeSlotCount ) { EdgeFlags[index] = 0; EdgeVertexIds[index] = 0; }
		if ( index < 10 ) Statistics[index] = 0;
	}
}
