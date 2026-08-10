MODES
{
	Default();
}
// Transvoxel shared fine-face density ownership.
CS
{
	#include "system.fxc"
	#include "voxel_terrain_field.fxc"
	#include "voxel_transvoxel_orientation.fxc"
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
		uint CoarseFaceMask;
	};
	struct CountResult { uint VertexCount; uint IndexCount; uint Generation; uint RequestIndex; uint Overflow; uint ActiveCells; uint Reserved0; uint Reserved1; };
	StructuredBuffer<TransitionRequest> Requests < Attribute( "Requests" ); >;
	StructuredBuffer<uint> Lookup < Attribute( "Lookup" ); >;
	RWStructuredBuffer<float> Samples < Attribute( "Samples" ); >;
	RWStructuredBuffer<CountResult> CountResults < Attribute( "CountResults" ); >;
	int ChunkSize < Attribute( "ChunkSize" ); >;
	int SampleCount < Attribute( "SampleCount" ); >;
	int TransitionCellCount < Attribute( "TransitionCellCount" ); >;
	int BatchSize < Attribute( "BatchSize" ); >;
	int GeometryOffset < Attribute( "GeometryOffset" ); >;
	int TriangleOffset < Attribute( "TriangleOffset" ); >;
	int VertexOffset < Attribute( "VertexOffset" ); >;
	int TransitionCellsPerAxis < Attribute( "TransitionCellsPerAxis" ); >;
	float VoxelSize < Attribute( "VoxelSize" ); >;
	float SdfClampDistance < Attribute( "SdfClampDistance" ); >;
	float SimplexFrequency < Attribute( "SimplexFrequency" ); >;
	float SimplexAmplitude < Attribute( "SimplexAmplitude" ); >;
	float SimplexBaseHeight < Attribute( "SimplexBaseHeight" ); >;
	int SimplexSeed < Attribute( "SimplexSeed" ); >;
	static const uint CaseOrder[9] = { 0, 1, 2, 5, 8, 7, 6, 3, 4 };
	float3 FaceNormal( uint face ) { if(face==0)return float3(-1,0,0);if(face==1)return float3(1,0,0);if(face==2)return float3(0,-1,0);if(face==3)return float3(0,1,0);if(face==4)return float3(0,0,-1);return float3(0,0,1); }
	float3 FineSample( TransitionRequest request, uint sample, uint cellIndex ) { return request.FineOrigin.xyz + TransitionFacePosition(sample,request.Face,cellIndex,(uint)TransitionCellsPerAxis,ChunkSize) * (float)request.FineStep; }
	float3 CoarseSample( TransitionRequest request, uint sample, uint cellIndex )
	{
		float3 p=FineSample(request,sample,cellIndex);
		float3 boundary=request.CoarseOrigin.xyz;
		float extent=(float)ChunkSize*(float)request.CoarseStep;
		if(request.Face==0)boundary.x+=extent;else if(request.Face==1){}else if(request.Face==2)boundary.y+=extent;else if(request.Face==3){}else if(request.Face==4)boundary.z+=extent;
		boundary += FaceNormal(request.Face)*(float)request.CoarseStep;
		if(request.Face==0||request.Face==1)p.x=boundary.x;else if(request.Face==2||request.Face==3)p.y=boundary.y;else p.z=boundary.z;
		return p;
	}
	float3 WorldSample( TransitionRequest request, uint sample, uint cellIndex ) { return sample<9?FineSample(request,sample,cellIndex):CoarseSample(request,sample,cellIndex); }
	[numthreads(64,1,1)] void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if(id.x>=(uint)BatchSize)return;
		TransitionRequest request=Requests[id.x];uint vertexCount=0,indexCount=0,activeCells=0;
		[loop]for(uint cell=0;cell<(uint)TransitionCellCount;cell++)
		{
			uint baseIndex=(id.x*(uint)TransitionCellCount+cell)*(uint)SampleCount;
			[unroll]for(uint sample=0;sample<9;sample++)Samples[baseIndex+sample]=EvaluateEditedTerrainDensity(FineSample(request,sample,cell),SdfClampDistance,SimplexFrequency,SimplexAmplitude,SimplexBaseHeight,SimplexSeed);
			Samples[baseIndex+9]=Samples[baseIndex];Samples[baseIndex+10]=Samples[baseIndex+2];Samples[baseIndex+11]=Samples[baseIndex+6];Samples[baseIndex+12]=Samples[baseIndex+8];
			uint code=0;[unroll]for(uint bit=0;bit<9;bit++)if(Samples[baseIndex+CaseOrder[bit]]<0)code|=1u<<bit;
			uint cls=Lookup[code]&0x7f;uint counts=Lookup[GeometryOffset+cls];vertexCount+=counts>>4;indexCount+=(counts&15)*3;if((counts&15)!=0)activeCells++;
		}
		CountResult result;result.VertexCount=vertexCount;result.IndexCount=indexCount;result.Generation=request.Generation;result.RequestIndex=request.RequestIndex;result.Overflow=0;result.ActiveCells=activeCells;result.Reserved0=0;result.Reserved1=0;CountResults[id.x]=result;
	}
}
