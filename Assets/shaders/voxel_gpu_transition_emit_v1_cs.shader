MODES
{
	Default();
}
// Transvoxel all-adjacent-faces secondary ownership.
CS
{
	#include "system.fxc"
	#include "voxel_terrain_field.fxc"
	#include "voxel_transvoxel_orientation.fxc"
	#include "voxel_transvoxel_deformation.fxc"
	struct TransitionRequest { float4 FineOrigin; float4 CoarseOrigin; uint FineStep; uint CoarseStep; uint Face; uint Generation; uint RequestIndex; uint ResidentSlot; uint TransitionSlot; uint CoarseFaceMask; };
	struct AllocationDescriptor { uint VertexOffset; uint VertexCapacity; uint IndexOffset; uint IndexCapacity; uint Generation; uint ResidentSlot; uint RequestIndex; uint Flags; float4 DrawOrigin; float4 DrawScale; };
	struct VoxelGpuVertex { float3 Position; float3 Normal; float3 Tangent; float2 TexCoord; };
	StructuredBuffer<TransitionRequest> Requests < Attribute( "Requests" ); >;
	StructuredBuffer<float> Samples < Attribute( "Samples" ); >;
	StructuredBuffer<uint> Lookup < Attribute( "Lookup" ); >;
	StructuredBuffer<AllocationDescriptor> Allocations < Attribute( "Allocations" ); >;
	RWStructuredBuffer<VoxelGpuVertex> OutputVertices < Attribute( "OutputVertices" ); >;
	RWStructuredBuffer<uint> OutputIndices < Attribute( "OutputIndices" ); >;
	int ChunkSize < Attribute( "ChunkSize" ); >; int SampleCount < Attribute( "SampleCount" ); >; int TransitionCellCount < Attribute( "TransitionCellCount" ); >; int TransitionCellsPerAxis < Attribute( "TransitionCellsPerAxis" ); >; int BatchSize < Attribute( "BatchSize" ); >; int GeometryOffset < Attribute( "GeometryOffset" ); >; int TriangleOffset < Attribute( "TriangleOffset" ); >; int VertexOffset < Attribute( "VertexOffset" ); >; float VoxelSize < Attribute( "VoxelSize" ); >; float SdfClampDistance < Attribute( "SdfClampDistance" ); >; float SimplexFrequency < Attribute( "SimplexFrequency" ); >; float SimplexAmplitude < Attribute( "SimplexAmplitude" ); >; float SimplexBaseHeight < Attribute( "SimplexBaseHeight" ); >; int SimplexSeed < Attribute( "SimplexSeed" ); >;
	static const uint CaseOrder[9]={0,1,2,5,8,7,6,3,4};
	float Density(float3 p,uint editOperationCount){return EvaluateEditedTerrainDensity(p,editOperationCount,SdfClampDistance,SimplexFrequency,SimplexAmplitude,SimplexBaseHeight,SimplexSeed);} float3 FaceNormal(uint face){if(face==0)return float3(-1,0,0);if(face==1)return float3(1,0,0);if(face==2)return float3(0,-1,0);if(face==3)return float3(0,1,0);if(face==4)return float3(0,0,-1);return float3(0,0,1);}
	float3 FineSample(TransitionRequest request,uint sample,uint cellIndex){return request.FineOrigin.xyz+TransitionFacePosition(sample,request.Face,cellIndex,(uint)TransitionCellsPerAxis,ChunkSize)*(float)request.FineStep;}
	float3 CoarseSample(TransitionRequest request,uint sample,uint cellIndex){float3 p=FineSample(request,sample,cellIndex);float3 boundary=request.CoarseOrigin.xyz;float extent=(float)ChunkSize*(float)request.CoarseStep;if(request.Face==0)boundary.x+=extent;else if(request.Face==2)boundary.y+=extent;else if(request.Face==4)boundary.z+=extent;if(request.Face==0||request.Face==1)p.x=boundary.x;else if(request.Face==2||request.Face==3)p.y=boundary.y;else p.z=boundary.z;return TransitionSecondaryCanonicalPosition(p,request.CoarseOrigin.xyz,request.CoarseStep,request.CoarseFaceMask,ChunkSize);}
	float3 WorldSample(TransitionRequest request,uint sample,uint cellIndex){return sample<9?FineSample(request,sample,cellIndex):CoarseSample(request,sample,cellIndex);}
	float3 Normal(TransitionRequest request,float3 p,uint sampleA,uint sampleB,uint cellIndex,float t){float e=(float)request.FineStep;float3 a=WorldSample(request,sampleA,cellIndex),b=WorldSample(request,sampleB,cellIndex),pa=lerp(a,b,t);uint editOperationCount=(uint)request.FineOrigin.w;float3 g=float3(Density(pa+float3(e,0,0),editOperationCount)-Density(pa-float3(e,0,0),editOperationCount),Density(pa+float3(0,e,0),editOperationCount)-Density(pa-float3(0,e,0),editOperationCount),Density(pa+float3(0,0,e),editOperationCount)-Density(pa-float3(0,0,e),editOperationCount));float d=dot(g,g);if(d<1e-12)return FaceNormal(request.Face);return g*rsqrt(d);}
	[numthreads(64,1,1)] void MainCs(uint3 id:SV_DispatchThreadID)
	{
		if(id.x>=(uint)BatchSize)return;TransitionRequest request=Requests[id.x];AllocationDescriptor allocation=Allocations[id.x];if((allocation.Flags&2)==0)return;uint vertexCursor=0,indexCursor=0;
		[loop]for(uint cell=0;cell<(uint)TransitionCellCount;cell++)
		{
			uint baseSample=(id.x*(uint)TransitionCellCount+cell)*(uint)SampleCount,code=0;[unroll]for(uint bit=0;bit<9;bit++)if(Samples[baseSample+CaseOrder[bit]]<0)code|=1u<<bit;
			uint cellClass=Lookup[code],cls=cellClass&0x7f,counts=Lookup[GeometryOffset+cls],vertexCount=counts>>4,indexCount=(counts&15)*3;
			[unroll]for(uint v=0;v<12;v++){if(v>=vertexCount)break;uint edge=Lookup[VertexOffset+code*12+v],a=(edge>>4)&15,b=edge&15;float da=Samples[baseSample+a],db=Samples[baseSample+b],den=da-db,t=abs(den)>.000001?da/den:.5;t=clamp(t,0,1);float3 world=lerp(WorldSample(request,a,cell),WorldSample(request,b,cell),t),p=(world-request.FineOrigin.xyz)*VoxelSize;float3 n=Normal(request,world,a,b,cell,t);VoxelGpuVertex output;output.Position=p+allocation.DrawOrigin.xyz;output.Normal=n;output.Tangent=abs(n.z)<.999?normalize(cross(float3(0,0,1),n)):float3(1,0,0);output.TexCoord=p.xy/128;OutputVertices[allocation.VertexOffset+vertexCursor+v]=output;}
			uint triBase=TriangleOffset+cls*36;bool flip=(cellClass&0x80)!=0;[unroll]for(uint i=0;i<36;i+=3){if(i>=indexCount)break;uint a=Lookup[triBase+i],b=Lookup[triBase+i+1],c=Lookup[triBase+i+2];if(!flip){uint tmp=a;a=c;c=tmp;}uint target=allocation.IndexOffset+indexCursor+i;OutputIndices[target]=a+vertexCursor;OutputIndices[target+1]=b+vertexCursor;OutputIndices[target+2]=c+vertexCursor;}
			vertexCursor+=vertexCount;indexCursor+=indexCount;
		}
	}
}
