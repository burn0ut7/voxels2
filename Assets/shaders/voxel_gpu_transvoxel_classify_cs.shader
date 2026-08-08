MODES
{
	Default();
}
CS
{
	#include "system.fxc"
	StructuredBuffer<float> DensitySamples < Attribute( "DensitySamples" ); >;
	StructuredBuffer<uint> RegularLookup < Attribute( "RegularLookup" ); >;
	RWStructuredBuffer<uint3> Cells < Attribute( "Cells" ); >;
	RWStructuredBuffer<uint> EdgeFlags < Attribute( "EdgeFlags" ); >;
	RWStructuredBuffer<uint> Statistics < Attribute( "Statistics" ); >;
	int ChunkSize < Attribute( "ChunkSize" ); >;
	int SampleSize < Attribute( "SampleSize" ); >;
	int HaloSize < Attribute( "HaloSize" ); >;
	int CellCount < Attribute( "CellCount" ); >;
	int RegularGeometryCountsOffset < Attribute( "RegularGeometryCountsOffset" ); >;
	int RegularVertexDataOffset < Attribute( "RegularVertexDataOffset" ); >;
	static const uint3 Corners[8] = { uint3(0,0,0),uint3(1,0,0),uint3(0,1,0),uint3(1,1,0),uint3(0,0,1),uint3(1,0,1),uint3(0,1,1),uint3(1,1,1) };
	uint3 Decode3D( uint index, uint size ) { uint p=size*size,z=index/p,r=index-z*p,y=r/size; return uint3(r-y*size,y,z); }
	uint HaloIndex( int3 p ) { int3 h=p+1; return h.x+HaloSize*(h.y+HaloSize*h.z); }
	float Distance( int3 p ) { float v=DensitySamples[HaloIndex(p)]; return abs(v)<0.000001f?0.000001f:v; }
	uint SampleIndex( uint3 p ) { return p.x+SampleSize*(p.y+SampleSize*p.z); }
	uint EdgeSlot( uint3 cell, uint data )
	{
		uint code=data&0xff, a=(code>>4)&0xf, b=code&0xf; uint3 first=cell+Corners[a],second=cell+Corners[b];
		uint3 d=uint3(abs(int3(first)-int3(second))); uint axis=d.x!=0?0:d.y!=0?1:2;
		return SampleIndex(min(first,second))*3+axis;
	}
	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if ( id.x >= (uint)CellCount ) return; uint3 cell=Decode3D(id.x,ChunkSize); uint code=0;
		[unroll] for(uint c=0;c<8;c++) if(Distance(int3(cell+Corners[c]))<0) code|=1u<<c;
		Cells[id.x].x=code; Statistics[8]=CellCount; if(code==0||code==255)return;
		uint cls=RegularLookup[code], counts=RegularLookup[RegularGeometryCountsOffset+cls], vertices=counts>>4;
		Cells[id.x].y=(counts&0xf)*3; InterlockedAdd(Statistics[7],1);
		for(uint v=0;v<vertices;v++) InterlockedOr(EdgeFlags[EdgeSlot(cell,RegularLookup[RegularVertexDataOffset+code*12+v])],1);
	}
}
