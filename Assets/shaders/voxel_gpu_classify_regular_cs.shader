MODES
{
	Default();
}
CS
{
	#include "system.fxc"
	// Shared regular-cell classification for production and conformance.
	// A masked coarse boundary layer is owned by its Transvoxel companion.
	struct BlockRequest { float4 SampleOrigin; float4 SampleScale; int CoordinateX; int CoordinateY; int CoordinateZ; int Lod; uint RuleVersion; uint Generation; uint ResidentSlot; uint TransitionFaceMask; };
	StructuredBuffer<BlockRequest> BlockRequests < Attribute( "BlockRequests" ); >;
	StructuredBuffer<float> DensitySamples < Attribute( "DensitySamples" ); >; StructuredBuffer<uint> RegularLookup < Attribute( "RegularLookup" ); >;
	RWStructuredBuffer<uint3> Cells < Attribute( "Cells" ); >; RWStructuredBuffer<uint> EdgeFlags < Attribute( "EdgeFlags" ); >; RWStructuredBuffer<uint> Statistics < Attribute( "Statistics" ); >;
	AppendStructuredBuffer<uint> DrawIndexCounter < Attribute( "DrawIndexCounter" ); >;
	int ChunkSize < Attribute( "ChunkSize" ); >; int SampleSize < Attribute( "SampleSize" ); >; int HaloSize < Attribute( "HaloSize" ); >; int CellCount < Attribute( "CellCount" ); >; int EdgeSlotCount < Attribute( "EdgeSlotCount" ); >; int BatchSize < Attribute( "BatchSize" ); >; int PublicationPass < Attribute( "PublicationPass" ); >;
	int RegularGeometryCountsOffset < Attribute( "RegularGeometryCountsOffset" ); >; int RegularVertexDataOffset < Attribute( "RegularVertexDataOffset" ); >;
	static const uint3 Corners[8]={uint3(0,0,0),uint3(1,0,0),uint3(0,1,0),uint3(1,1,0),uint3(0,0,1),uint3(1,0,1),uint3(0,1,1),uint3(1,1,1)};
	uint3 Decode3D(uint index,uint size){uint p=size*size,z=index/p,r=index-z*p,y=r/size;return uint3(r-y*size,y,z);} uint HaloIndex(int3 p){int3 h=p+1;return h.x+HaloSize*(h.y+HaloSize*h.z);} float Distance(uint block,int3 p){float v=DensitySamples[block*HaloSize*HaloSize*HaloSize+HaloIndex(p)];return abs(v)<.000001f?(v<0?-.000001f:.000001f):v;} uint SampleIndex(uint3 p){return p.x+SampleSize*(p.y+SampleSize*p.z);}
	uint EdgeSlot(uint3 cell,uint data){uint code=data&0xff,a=(code>>4)&0xf,b=code&0xf;uint3 first=cell+Corners[a],second=cell+Corners[b],d=uint3(abs(int3(first)-int3(second)));uint axis=d.x!=0?0:d.y!=0?1:2;return SampleIndex(min(first,second))*3+axis;}
	[numthreads(64,1,1)] void MainCs(uint3 id:SV_DispatchThreadID)
	{
		if(PublicationPass!=0){if(id.x>=(uint)CellCount*(uint)BatchSize)return;uint count=Cells[id.x].y;for(uint i=0;i<count;i++)DrawIndexCounter.Append(0);return;}
		if(id.x>=(uint)CellCount*(uint)BatchSize)return;uint block=id.x/(uint)CellCount,local=id.x-block*(uint)CellCount;uint3 cell=Decode3D(local,ChunkSize);BlockRequest request=BlockRequests[block];bool masked=((request.TransitionFaceMask&1u)!=0&&cell.x==0)||((request.TransitionFaceMask&2u)!=0&&cell.x==ChunkSize-1)||((request.TransitionFaceMask&4u)!=0&&cell.y==0)||((request.TransitionFaceMask&8u)!=0&&cell.y==ChunkSize-1)||((request.TransitionFaceMask&16u)!=0&&cell.z==0)||((request.TransitionFaceMask&32u)!=0&&cell.z==ChunkSize-1);if(masked){Cells[id.x].x=0;Cells[id.x].y=0;return;}uint code=0;[unroll]for(uint c=0;c<8;c++)if(Distance(block,int3(cell+Corners[c]))<0)code|=1u<<c;
		Cells[id.x].x=code;Statistics[8]=CellCount;if(code==0||code==255)return;uint cls=RegularLookup[code],counts=RegularLookup[RegularGeometryCountsOffset+cls],vertices=counts>>4;Cells[id.x].y=(counts&0xf)*3;InterlockedAdd(Statistics[7],1);
		for(uint v=0;v<vertices;v++)InterlockedOr(EdgeFlags[block*(uint)EdgeSlotCount+EdgeSlot(cell,RegularLookup[RegularVertexDataOffset+code*12+v])],1);
	}
}
