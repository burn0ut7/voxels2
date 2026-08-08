MODES
{
	Default();
}
// Phase 2 batched clear.
CS
{
	#include "system.fxc"
	RWStructuredBuffer<uint3> Cells < Attribute( "Cells" ); >;
	RWStructuredBuffer<uint> EdgeFlags < Attribute( "EdgeFlags" ); >;
	RWStructuredBuffer<uint> EdgeVertexIds < Attribute( "EdgeVertexIds" ); >;
	RWStructuredBuffer<uint> BlockCounts < Attribute( "BlockCounts" ); >;
	int CellCount < Attribute( "CellCount" ); >;
	int EdgeSlotCount < Attribute( "EdgeSlotCount" ); >;
	int DescriptorOffset < Attribute( "DescriptorOffset" ); >; int TotalsOffset < Attribute( "TotalsOffset" ); >; int AllocationPass < Attribute( "AllocationPass" ); >; int BatchSize < Attribute( "BatchSize" ); >;
	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint index = id.x;
		if(AllocationPass!=0){if(index>=(uint)BatchSize)return;uint vertices=0,indices=0;for(uint previous=0;previous<index;previous++){vertices+=BlockCounts[previous*2];indices+=BlockCounts[previous*2+1];}uint descriptor=(uint)DescriptorOffset+index*4;BlockCounts[descriptor]=vertices;BlockCounts[descriptor+1]=indices;BlockCounts[descriptor+2]=BlockCounts[index*2];BlockCounts[descriptor+3]=BlockCounts[index*2+1];if(index==(uint)BatchSize-1){BlockCounts[TotalsOffset]=vertices+BlockCounts[index*2];BlockCounts[TotalsOffset+1]=indices+BlockCounts[index*2+1];}return;}
		if ( index < (uint)CellCount * (uint)BatchSize ) Cells[index] = uint3( 0, 0, 0 );
		if ( index < (uint)EdgeSlotCount * (uint)BatchSize ) { EdgeFlags[index] = 0; EdgeVertexIds[index] = 0; }
	}
}
