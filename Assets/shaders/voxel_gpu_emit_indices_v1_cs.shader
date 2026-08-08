MODES
{
	Default();
}
// Versioned capacity-safe Pass B writes block-local indices into assigned persistent ranges.
CS
{
	#include "system.fxc"
	struct VoxelGpuVertex { float3 Position; float3 Normal; float3 Tangent; float2 TexCoord; };
	struct AllocationDescriptor { uint VertexOffset; uint VertexCapacity; uint IndexOffset; uint IndexCapacity; uint Generation; uint ResidentSlot; uint RequestIndex; uint Flags; float4 DrawOrigin; float4 Reserved; };
	StructuredBuffer<uint> RegularLookup < Attribute( "RegularLookup" ); >; StructuredBuffer<uint3> Cells < Attribute( "Cells" ); >;
	StructuredBuffer<uint> EdgeVertexIds < Attribute( "EdgeVertexIds" ); >; StructuredBuffer<VoxelGpuVertex> OutputVertices < Attribute( "OutputVertices" ); >;
	StructuredBuffer<uint> EdgeGroupSums < Attribute( "EdgeGroupSums" ); >; StructuredBuffer<uint> CellGroupSums < Attribute( "CellGroupSums" ); >; StructuredBuffer<AllocationDescriptor> Allocations < Attribute( "Allocations" ); >;
	RWStructuredBuffer<uint> OutputIndices < Attribute( "OutputIndices" ); >;
	int ChunkSize < Attribute( "ChunkSize" ); >; int SampleSize < Attribute( "SampleSize" ); >; int CellCount < Attribute( "CellCount" ); >; int EdgeSlotCount < Attribute( "EdgeSlotCount" ); >; int EdgeGroupCount < Attribute( "EdgeGroupCount" ); >; int CellGroupCount < Attribute( "CellGroupCount" ); >; int BatchSize < Attribute( "BatchSize" ); >;
	int RegularGeometryCountsOffset < Attribute( "RegularGeometryCountsOffset" ); >; int RegularTriangleIndicesOffset < Attribute( "RegularTriangleIndicesOffset" ); >; int RegularVertexDataOffset < Attribute( "RegularVertexDataOffset" ); >;
	static const uint3 Corners[8]={uint3(0,0,0),uint3(1,0,0),uint3(0,1,0),uint3(1,1,0),uint3(0,0,1),uint3(1,0,1),uint3(0,1,1),uint3(1,1,1)};
	uint3 Decode3D(uint i,uint s){uint p=s*s,z=i/p,r=i-z*p,y=r/s;return uint3(r-y*s,y,z);} uint SampleIndex(uint3 p){return p.x+SampleSize*(p.y+SampleSize*p.z);}
	uint EdgeSlot(uint3 cell,uint data){uint code=data&0xff,a=(code>>4)&0xf,b=data&0xf;uint3 x=cell+Corners[a],y=cell+Corners[b],d=uint3(abs(int3(x)-int3(y)));uint axis=d.x!=0?0:d.y!=0?1:2;return SampleIndex(min(x,y))*3+axis;}
	[numthreads(64,1,1)] void MainCs(uint3 id:SV_DispatchThreadID)
	{
		if(id.x>=(uint)CellCount*(uint)BatchSize)return;uint block=id.x/(uint)CellCount,local=id.x-block*(uint)CellCount,code=Cells[id.x].x;if(code==0||code==255)return;AllocationDescriptor allocation=Allocations[block];
		uint3 cell=Decode3D(local,ChunkSize);uint cls=RegularLookup[code],counts=RegularLookup[RegularGeometryCountsOffset+cls],vc=counts>>4,tc=counts&0xf,verts[12];
		for(uint v=0;v<vc;v++){uint edge=EdgeSlot(cell,RegularLookup[RegularVertexDataOffset+code*12+v]);verts[v]=EdgeGroupSums[block*(uint)EdgeGroupCount+edge/256]+EdgeVertexIds[block*(uint)EdgeSlotCount+edge];if(verts[v]>=allocation.VertexCapacity)return;}
		uint output=CellGroupSums[block*(uint)CellGroupCount+local/256]+Cells[id.x].z;if(output+tc*3>allocation.IndexCapacity)return;uint triBase=RegularTriangleIndicesOffset+cls*15;
		for(uint t=0;t<tc;t++){uint table=triBase+t*3,a=verts[RegularLookup[table]],b=verts[RegularLookup[table+1]],c=verts[RegularLookup[table+2]],target=allocation.IndexOffset+output+t*3;float3 pa=OutputVertices[allocation.VertexOffset+a].Position,pb=OutputVertices[allocation.VertexOffset+b].Position,pc=OutputVertices[allocation.VertexOffset+c].Position,fn=cross(pb-pa,pc-pa),vn=OutputVertices[allocation.VertexOffset+a].Normal+OutputVertices[allocation.VertexOffset+b].Normal+OutputVertices[allocation.VertexOffset+c].Normal;OutputIndices[target]=a;if(dot(fn,vn)>=0){OutputIndices[target+1]=b;OutputIndices[target+2]=c;}else{OutputIndices[target+1]=c;OutputIndices[target+2]=b;}}
	}
}
