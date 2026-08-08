MODES
{
	Default();
}
// Shared regular-cell scan kernel for proof and persistent production batches.
CS
{
	#include "system.fxc"
	StructuredBuffer<uint> EdgeFlags < Attribute( "EdgeFlags" ); >; RWStructuredBuffer<uint> EdgeVertexIds < Attribute( "EdgeVertexIds" ); >; RWStructuredBuffer<uint> EdgeGroupSums < Attribute( "EdgeGroupSums" ); >;
	RWStructuredBuffer<uint3> Cells < Attribute( "Cells" ); >; RWStructuredBuffer<uint> CellGroupSums < Attribute( "CellGroupSums" ); >; RWStructuredBuffer<uint> BlockCounts < Attribute( "BlockCounts" ); >;
	int EdgeSlotCount < Attribute( "EdgeSlotCount" ); >; int EdgeGroupCount < Attribute( "EdgeGroupCount" ); >; int CellCount < Attribute( "CellCount" ); >; int CellGroupCount < Attribute( "CellGroupCount" ); >; int BatchSize < Attribute( "BatchSize" ); >; int ScanPass < Attribute( "ScanPass" ); >;
	groupshared uint Scan[256];
	[numthreads(256,1,1)] void MainCs(uint3 group:SV_GroupID,uint lane:SV_GroupIndex)
	{
		if(ScanPass==2){uint block=group.x;if(block>=(uint)BatchSize||lane!=0)return;uint edgeBase=block*(uint)EdgeGroupCount,cellBase=block*(uint)CellGroupCount,vertices=0,indices=0;for(uint edgeGroup=0;edgeGroup<(uint)EdgeGroupCount;edgeGroup++){uint count=EdgeGroupSums[edgeBase+edgeGroup];EdgeGroupSums[edgeBase+edgeGroup]=vertices;vertices+=count;}for(uint cellGroup=0;cellGroup<(uint)CellGroupCount;cellGroup++){uint count=CellGroupSums[cellBase+cellGroup];CellGroupSums[cellBase+cellGroup]=indices;indices+=count;}BlockCounts[block*2]=vertices;BlockCounts[block*2+1]=indices;return;}
		if(ScanPass==1){uint block=group.x/(uint)CellGroupCount,g=group.x-block*(uint)CellGroupCount;if(block>=(uint)BatchSize)return;uint local=g*256+lane,index=block*(uint)CellCount+local;Scan[lane]=local<(uint)CellCount?Cells[index].y:0;GroupMemoryBarrierWithGroupSync();for(uint up=1;up<256;up<<=1){uint i=(lane+1)*up*2-1;if(i<256)Scan[i]+=Scan[i-up];GroupMemoryBarrierWithGroupSync();}uint total=Scan[255];if(lane==0)Scan[255]=0;GroupMemoryBarrierWithGroupSync();for(uint down=128;down>0;down>>=1){uint i=(lane+1)*down*2-1;if(i<256){uint value=Scan[i-down];Scan[i-down]=Scan[i];Scan[i]+=value;}GroupMemoryBarrierWithGroupSync();}if(local<(uint)CellCount)Cells[index].z=Scan[lane];if(lane==0)CellGroupSums[block*(uint)CellGroupCount+g]=total;return;}
		uint block=group.x/(uint)EdgeGroupCount,g=group.x-block*(uint)EdgeGroupCount;if(block>=(uint)BatchSize)return;uint local=g*256+lane;Scan[lane]=local<(uint)EdgeSlotCount?EdgeFlags[block*(uint)EdgeSlotCount+local]:0;GroupMemoryBarrierWithGroupSync();for(uint up=1;up<256;up<<=1){uint i=(lane+1)*up*2-1;if(i<256)Scan[i]+=Scan[i-up];GroupMemoryBarrierWithGroupSync();}uint total=Scan[255];if(lane==0)Scan[255]=0;GroupMemoryBarrierWithGroupSync();for(uint down=128;down>0;down>>=1){uint i=(lane+1)*down*2-1;if(i<256){uint value=Scan[i-down];Scan[i-down]=Scan[i];Scan[i]+=value;}GroupMemoryBarrierWithGroupSync();}if(local<(uint)EdgeSlotCount)EdgeVertexIds[block*(uint)EdgeSlotCount+local]=Scan[lane];if(lane==0)EdgeGroupSums[block*(uint)EdgeGroupCount+g]=total;
	}
}
