MODES
{
	Default();
}
CS
{
	#include "system.fxc"
	struct VoxelGpuVertex { float3 Position; float3 Normal; float3 Tangent; float2 TexCoord; };
	StructuredBuffer<float> DensitySamples < Attribute( "DensitySamples" ); >;
	StructuredBuffer<uint> EdgeFlags < Attribute( "EdgeFlags" ); >;
	StructuredBuffer<uint> EdgeVertexIds < Attribute( "EdgeVertexIds" ); >;
	RWStructuredBuffer<VoxelGpuVertex> OutputVertices < Attribute( "OutputVertices" ); >;
	int SampleSize < Attribute( "SampleSize" ); >; int HaloSize < Attribute( "HaloSize" ); >; int EdgeSlotCount < Attribute( "EdgeSlotCount" ); >;
	float VoxelSize < Attribute( "VoxelSize" ); >; float3 DrawOrigin < Attribute( "DrawOrigin" ); >;
	uint3 Decode3D(uint i,uint s){uint p=s*s,z=i/p,r=i-z*p,y=r/s;return uint3(r-y*s,y,z);} uint HaloIndex(int3 p){int3 h=p+1;return h.x+HaloSize*(h.y+HaloSize*h.z);}
	float Raw(int3 p){return DensitySamples[HaloIndex(p)];} float Distance(int3 p){float v=Raw(p);return abs(v)<.000001f?.000001f:v;}
	float3 Gradient(int3 p){return float3(Raw(p+int3(1,0,0))-Raw(p-int3(1,0,0)),Raw(p+int3(0,1,0))-Raw(p-int3(0,1,0)),Raw(p+int3(0,0,1))-Raw(p-int3(0,0,1)));}
	float3 Safe(float3 v,float3 f){float d=dot(v,v);return d>1e-12f?v*rsqrt(d):f;}
	[numthreads(64,1,1)] void MainCs(uint3 id:SV_DispatchThreadID)
	{
		uint slot=id.x;if(slot>=(uint)EdgeSlotCount||EdgeFlags[slot]==0)return;uint sample=slot/3,axis=slot-sample*3;uint3 a=Decode3D(sample,SampleSize),b=a;
		if(axis==0)b.x++;else if(axis==1)b.y++;else b.z++;float da=Distance(int3(a)),db=Distance(int3(b)),den=da-db,t=abs(den)>.000001f?da/den:.5f;t=clamp(t,0,1);
		float3 local=lerp(float3(a),float3(b),t)*VoxelSize,n=Safe(lerp(Gradient(int3(a)),Gradient(int3(b)),t),float3(0,0,1));
		VoxelGpuVertex o;o.Position=DrawOrigin+local;o.Normal=n;o.Tangent=abs(n.z)<.999f?Safe(cross(float3(0,0,1),n),float3(1,0,0)):float3(1,0,0);o.TexCoord=local.xy/128;OutputVertices[EdgeVertexIds[slot]]=o;
	}
}
