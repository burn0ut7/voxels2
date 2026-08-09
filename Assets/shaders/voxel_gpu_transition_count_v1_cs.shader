MODES
{
	Default();
}
CS
{
	#include "system.fxc"
	struct TransitionRequest
	{
		float4 FineOrigin;
		float4 CoarseOrigin;
		uint FineStep;
		uint CoarseStep;
		uint Face;
		uint Generation;
		uint RequestIndex;
		uint ResidentSlot;
		uint TransitionSlot;
		uint Reserved0;
	};
	struct CountResult { uint VertexCount; uint IndexCount; uint Generation; uint RequestIndex; uint Overflow; uint ActiveCells; uint Reserved0; uint Reserved1; };
	StructuredBuffer<TransitionRequest> Requests < Attribute( "Requests" ); >;
	StructuredBuffer<uint> Lookup < Attribute( "Lookup" ); >;
	RWStructuredBuffer<float> Samples < Attribute( "Samples" ); >;
	RWStructuredBuffer<CountResult> CountResults < Attribute( "CountResults" ); >;
	int ChunkSize < Attribute( "ChunkSize" ); >;
	int SampleCount < Attribute( "SampleCount" ); >;
	int BatchSize < Attribute( "BatchSize" ); >;
	int GeometryOffset < Attribute( "GeometryOffset" ); >;
	int TriangleOffset < Attribute( "TriangleOffset" ); >;
	int VertexOffset < Attribute( "VertexOffset" ); >;
	float VoxelSize < Attribute( "VoxelSize" ); >;
	float SdfClampDistance < Attribute( "SdfClampDistance" ); >;
	float SimplexFrequency < Attribute( "SimplexFrequency" ); >;
	float SimplexAmplitude < Attribute( "SimplexAmplitude" ); >;
	float SimplexBaseHeight < Attribute( "SimplexBaseHeight" ); >;
	int SimplexSeed < Attribute( "SimplexSeed" ); >;
	static const uint CaseOrder[9] = { 0, 1, 2, 5, 8, 7, 6, 3, 4 };
	static const int3 SampleCoordinates[9] = { int3(0,0,0), int3(1,0,0), int3(2,0,0), int3(0,1,0), int3(1,1,0), int3(2,1,0), int3(0,2,0), int3(1,2,0), int3(2,2,0) };
	int Hash( int x, int y, int seed ) { int value = seed + x * 374761393 + y * 668265263; value = (value ^ (value >> 13)) * 1274126177; return value ^ (value >> 16); }
	float2 Gradient( int index ) { if(index==0)return float2(1,0);if(index==1)return float2(-1,0);if(index==2)return float2(0,1);if(index==3)return float2(0,-1);if(index==4)return float2(.70710677,.70710677);if(index==5)return float2(-.70710677,.70710677);if(index==6)return float2(.70710677,-.70710677);return float2(-.70710677,-.70710677); }
	float SimplexNoise( float2 point ) { const float skew=.3660254038,unskew=.2113248654;float skewed=(point.x+point.y)*skew;int cellX=(int)floor(point.x+skewed),cellY=(int)floor(point.y+skewed);float cellOffset=(cellX+cellY)*unskew;float2 offset=point-(float2(cellX,cellY)-cellOffset);int sx=offset.x>offset.y?1:0,sy=offset.x>offset.y?0:1;float2 second=offset-float2(sx,sy)+unskew,third=offset-1+2*unskew,value=0;float radius=.5-dot(offset,offset);if(radius>0)value+=radius*radius*radius*radius*dot(Gradient(Hash(cellX,cellY,SimplexSeed)&7),offset);radius=.5-dot(second,second);if(radius>0)value+=radius*radius*radius*radius*dot(Gradient(Hash(cellX+sx,cellY+sy,SimplexSeed)&7),second);radius=.5-dot(third,third);if(radius>0)value+=radius*radius*radius*radius*dot(Gradient(Hash(cellX+1,cellY+1,SimplexSeed)&7),third);return 70*value; }
	float Density( float3 p ) { return clamp(p.z-(SimplexBaseHeight+SimplexNoise(p.xy*SimplexFrequency)*SimplexAmplitude),-SdfClampDistance,SdfClampDistance); }
	float3 FacePosition( uint sample, uint face ) { int3 c=sample<9?SampleCoordinates[sample]:sample==9?int3(0,0,0):sample==10?int3(2,0,0):sample==11?int3(0,2,0):int3(2,2,0);if(face==0)return float3(0,c.x,c.y);if(face==1)return float3(2,c.y,c.x);if(face==2)return float3(c.y,0,c.x);if(face==3)return float3(c.x,2,c.y);if(face==4)return float3(c.x,c.y,0);return float3(c.y,c.x,2); }
	float3 WorldSample( TransitionRequest request, uint sample ) { float3 facePosition=FacePosition(sample,request.Face)/2.0;float3 fine=request.FineOrigin.xyz+facePosition*(float)ChunkSize*(float)request.FineStep;return fine; }
	[numthreads(64,1,1)] void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if(id.x>=(uint)BatchSize)return;TransitionRequest request=Requests[id.x];uint baseIndex=id.x*(uint)SampleCount;[unroll]for(uint sample=0;sample<13;sample++)Samples[baseIndex+sample]=Density(WorldSample(request,sample));uint code=0;[unroll]for(uint bit=0;bit<9;bit++)if(Samples[baseIndex+CaseOrder[bit]]<0)code|=1u<<bit;uint cls=Lookup[code]&0x7f;uint counts=Lookup[GeometryOffset+cls];CountResult result;result.VertexCount=counts>>4;result.IndexCount=(counts&15)*3;result.Generation=request.Generation;result.RequestIndex=request.RequestIndex;result.Overflow=0;result.ActiveCells=code;result.Reserved0=0;result.Reserved1=0;CountResults[id.x]=result;
	}
}
