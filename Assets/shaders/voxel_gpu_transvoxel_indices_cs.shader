MODES
{
	Default();
}
CS
{
	#include "system.fxc"
	struct VoxelGpuVertex { float3 Position; float3 Normal; float3 Tangent; float2 TexCoord; };
	StructuredBuffer<uint> RegularLookup < Attribute( "RegularLookup" ); >; StructuredBuffer<uint3> Cells < Attribute( "Cells" ); >;
	StructuredBuffer<uint> EdgeVertexIds < Attribute( "EdgeVertexIds" ); >; StructuredBuffer<VoxelGpuVertex> OutputVertices < Attribute( "OutputVertices" ); >;
	RWStructuredBuffer<uint> OutputIndices < Attribute( "OutputIndices" ); >;
	int ChunkSize < Attribute( "ChunkSize" ); >; int SampleSize < Attribute( "SampleSize" ); >; int CellCount < Attribute( "CellCount" ); >;
	int RegularGeometryCountsOffset < Attribute( "RegularGeometryCountsOffset" ); >; int RegularTriangleIndicesOffset < Attribute( "RegularTriangleIndicesOffset" ); >; int RegularVertexDataOffset < Attribute( "RegularVertexDataOffset" ); >;
	static const uint3 Corners[8]={uint3(0,0,0),uint3(1,0,0),uint3(0,1,0),uint3(1,1,0),uint3(0,0,1),uint3(1,0,1),uint3(0,1,1),uint3(1,1,1)};
	uint3 Decode3D(uint i,uint s){uint p=s*s,z=i/p,r=i-z*p,y=r/s;return uint3(r-y*s,y,z);} uint SampleIndex(uint3 p){return p.x+SampleSize*(p.y+SampleSize*p.z);}
	uint EdgeSlot(uint3 cell,uint data){uint code=data&0xff,a=(code>>4)&0xf,b=code&0xf;uint3 x=cell+Corners[a],y=cell+Corners[b],d=uint3(abs(int3(x)-int3(y)));uint axis=d.x!=0?0:d.y!=0?1:2;return SampleIndex(min(x,y))*3+axis;}
	[numthreads(64,1,1)] void MainCs(uint3 id:SV_DispatchThreadID)
	{
		if(id.x>=(uint)CellCount)return;uint code=Cells[id.x].x;if(code==0||code==255)return;uint3 cell=Decode3D(id.x,ChunkSize);uint cls=RegularLookup[code],counts=RegularLookup[RegularGeometryCountsOffset+cls],vc=counts>>4,tc=counts&0xf,verts[12];
		for(uint v=0;v<vc;v++)verts[v]=EdgeVertexIds[EdgeSlot(cell,RegularLookup[RegularVertexDataOffset+code*12+v])];uint output=Cells[id.x].z,triBase=RegularTriangleIndicesOffset+cls*15;
		for(uint t=0;t<tc;t++){uint table=triBase+t*3,a=verts[RegularLookup[table]],b=verts[RegularLookup[table+1]],c=verts[RegularLookup[table+2]],target=output+t*3;float3 pa=OutputVertices[a].Position,pb=OutputVertices[b].Position,pc=OutputVertices[c].Position,fn=cross(pb-pa,pc-pa),vn=OutputVertices[a].Normal+OutputVertices[b].Normal+OutputVertices[c].Normal;OutputIndices[target]=a;if(dot(fn,vn)>=0){OutputIndices[target+1]=b;OutputIndices[target+2]=c;}else{OutputIndices[target+1]=c;OutputIndices[target+2]=b;}}
	}
}
