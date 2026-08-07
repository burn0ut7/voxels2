HEADER
{
	DevShader = true;
	Description = "Indexed GPU Lewiner MC33 SDF mesher";
}

MODES
{
	Default();
}

COMMON
{
	#include "system.fxc"
}

CS
{
	struct PrototypeVertex { float3 Position; float3 Normal; float3 Tangent; float2 TexCoord; };
	static const uint3 Corners[8] = {
		uint3(0,0,0),uint3(1,0,0),uint3(1,1,0),uint3(0,1,0),
		uint3(0,0,1),uint3(1,0,1),uint3(1,1,1),uint3(0,1,1) };
	static const uint2 EdgeCorners[12] = {
		uint2(0,1),uint2(1,2),uint2(3,2),uint2(0,3),uint2(4,5),uint2(5,6),
		uint2(7,6),uint2(4,7),uint2(0,4),uint2(1,5),uint2(2,6),uint2(3,7) };
	static const uint TableCases=0;
	static const uint Tiling1=1; static const uint Tiling2=2; static const uint Tiling3_1=3; static const uint Tiling3_2=4;
	static const uint Tiling4_1=5; static const uint Tiling4_2=6; static const uint Tiling5=7; static const uint Tiling6_1_1=8; static const uint Tiling6_1_2=9;
	static const uint Tiling6_2=10; static const uint Tiling7_1=11; static const uint Tiling7_2=12; static const uint Tiling7_3=13; static const uint Tiling7_4_1=14;
	static const uint Tiling7_4_2=15; static const uint Tiling8=16; static const uint Tiling9=17; static const uint Tiling10_1_1=18; static const uint Tiling10_1_1_=19;
	static const uint Tiling10_1_2=20; static const uint Tiling10_2=21; static const uint Tiling10_2_=22; static const uint Tiling11=23;
	static const uint Tiling12_1_1=24; static const uint Tiling12_1_1_=25; static const uint Tiling12_1_2=26; static const uint Tiling12_2=27; static const uint Tiling12_2_=28;
	static const uint Tiling13_1=29; static const uint Tiling13_1_=30; static const uint Tiling13_2=31; static const uint Tiling13_2_=32; static const uint Tiling13_3=33;
	static const uint Tiling13_3_=34; static const uint Tiling13_4=35; static const uint Tiling13_5_1=36; static const uint Tiling13_5_2=37; static const uint Tiling14=38;
	static const uint Test3=39; static const uint Test4=40; static const uint Test6=41; static const uint Test7=42; static const uint Test10=43; static const uint Test12=44; static const uint Test13=45; static const uint Subconfig13=46;

	StructuredBuffer<float> SdfSamples < Attribute("SdfSamples"); >;
	StructuredBuffer<uint> LutValues < Attribute("LutValues"); >;
	StructuredBuffer<uint> LutMetadata < Attribute("LutMetadata"); >;
	RWStructuredBuffer<PrototypeVertex> OutputVertices < Attribute("OutputVertices"); >;
	RWStructuredBuffer<uint> OutputIndices < Attribute("OutputIndices"); >;
	RWStructuredBuffer<uint> Statistics < Attribute("Statistics"); >;
	RWStructuredBuffer<uint> EdgeFlags < Attribute("EdgeFlags"); >;
	RWStructuredBuffer<uint> EdgeVertexIds < Attribute("EdgeVertexIds"); >;
	RWStructuredBuffer<uint4> CellSelections < Attribute("CellSelections"); >;
	RWStructuredBuffer<uint> CellIndexOffsets < Attribute("CellIndexOffsets"); >;
	RWStructuredBuffer<uint> CellCenterIds < Attribute("CellCenterIds"); >;
	float3 ChunkWorldOrigin < Attribute("ChunkWorldOrigin"); >;
	int ChunkSize < Attribute("ChunkSize"); >;
	int SampleSize < Attribute("SampleSize"); >;
	int HaloSize < Attribute("HaloSize"); >;
	float VoxelSize < Attribute("VoxelSize"); >;
	int MaxVertices < Attribute("MaxVertices"); >;
	int MaxIndices < Attribute("MaxIndices"); >;
	int CellCount < Attribute("CellCount"); >;
	int EdgeSlotCount < Attribute("EdgeSlotCount"); >;
	int Phase < Attribute("Phase"); >;
	groupshared uint ScanTotals[256];
	groupshared uint ScanBases[256];
	groupshared uint ScanEdgeTotal;
	groupshared uint ScanCenterTotal;
	groupshared uint ScanIndexTotal;

	uint SampleIndex(uint3 p) { return p.x + SampleSize*(p.y + SampleSize*p.z); }
	uint CellIndex(uint3 p) { return p.x + ChunkSize*(p.y + ChunkSize*p.z); }
	uint HaloIndex(int3 p) { int3 h=p+1; return h.x + HaloSize*(h.y + HaloSize*h.z); }
	float RawDistance(int3 p) { return SdfSamples[HaloIndex(p)]; }
	float Distance(int3 p) { float v=RawDistance(p); return abs(v)<0.000001f ? 0.000001f : v; }
	int SignedLut(uint table,uint i,uint j,uint k) {
		uint m=table*4, d1=LutMetadata[m+2], d2=LutMetadata[m+3];
		uint v=LutValues[LutMetadata[m] + (i*d1+j)*d2+k]; return v>127 ? int(v)-256 : int(v);
	}
	int L1(uint t,uint i) { return SignedLut(t,i,0,0); }
	int L2(uint t,uint i,uint j) { return SignedLut(t,i,j,0); }
	int L3(uint t,uint i,uint j,uint k) { return SignedLut(t,i,j,k); }
	float3 Gradient(int3 p) { return float3(RawDistance(p+int3(1,0,0))-RawDistance(p-int3(1,0,0)),RawDistance(p+int3(0,1,0))-RawDistance(p-int3(0,1,0)),RawDistance(p+int3(0,0,1))-RawDistance(p-int3(0,0,1))); }
	float3 SafeNormal(float3 v,float3 f) { float d=dot(v,v); return d>1e-12f?v*rsqrt(d):f; }
	float3 Tangent(float3 n) { return SafeNormal(abs(n.z)<.999f?cross(float3(0,0,1),n):float3(1,0,0),float3(1,0,0)); }

	bool FaceTest(float v[8],int face) {
		int f=abs(face); float A=0,B=0,C=0,D=0;
		if(f==1){A=v[0];B=v[4];C=v[5];D=v[1];} else if(f==2){A=v[1];B=v[5];C=v[6];D=v[2];}
		else if(f==3){A=v[2];B=v[6];C=v[7];D=v[3];} else if(f==4){A=v[3];B=v[7];C=v[4];D=v[0];}
		else if(f==5){A=v[0];B=v[3];C=v[2];D=v[1];} else {A=v[4];B=v[7];C=v[6];D=v[5];}
		float q=A*C-B*D; return abs(q)<1e-7f ? face>=0 : face*A*q>=0;
	}

	bool InteriorTest(float v[8],int mcCase,uint config,uint subconfig,int s) {
		float t=0,A=0,B=0,C=0,D=0; int edge=-1;
		if(mcCase==4||mcCase==10) {
			float a=(v[4]-v[0])*(v[6]-v[2])-(v[7]-v[3])*(v[5]-v[1]);
			float b=v[2]*(v[4]-v[0])+v[0]*(v[6]-v[2])-v[1]*(v[7]-v[3])-v[3]*(v[5]-v[1]);
			t=-b/(2*a+1e-7f); if(t<0||t>1) return s>0;
			A=v[0]+(v[4]-v[0])*t; B=v[3]+(v[7]-v[3])*t; C=v[2]+(v[6]-v[2])*t; D=v[1]+(v[5]-v[1])*t;
		} else {
			if(mcCase==6) edge=L2(Test6,config,2); else if(mcCase==7) edge=L2(Test7,config,4);
			else if(mcCase==12) edge=L2(Test12,config,3); else edge=L3(Tiling13_5_1,config,subconfig,0);
			uint2 ec=EdgeCorners[edge]; t=v[ec.x]/(v[ec.x]-v[ec.y]+1e-7f); A=0;
			if(edge==0){B=lerp(v[3],v[2],t);C=lerp(v[7],v[6],t);D=lerp(v[4],v[5],t);}
			else if(edge==1){B=lerp(v[0],v[3],t);C=lerp(v[4],v[7],t);D=lerp(v[5],v[6],t);}
			else if(edge==2){B=lerp(v[1],v[0],t);C=lerp(v[5],v[4],t);D=lerp(v[6],v[7],t);}
			else if(edge==3){B=lerp(v[2],v[1],t);C=lerp(v[6],v[5],t);D=lerp(v[7],v[4],t);}
			else if(edge==4){B=lerp(v[7],v[6],t);C=lerp(v[3],v[2],t);D=lerp(v[0],v[1],t);}
			else if(edge==5){B=lerp(v[4],v[7],t);C=lerp(v[0],v[3],t);D=lerp(v[1],v[2],t);}
			else if(edge==6){B=lerp(v[5],v[4],t);C=lerp(v[1],v[0],t);D=lerp(v[2],v[3],t);}
			else if(edge==7){B=lerp(v[6],v[5],t);C=lerp(v[2],v[1],t);D=lerp(v[3],v[0],t);}
			else if(edge==8){B=lerp(v[3],v[7],t);C=lerp(v[2],v[6],t);D=lerp(v[1],v[5],t);}
			else if(edge==9){B=lerp(v[0],v[4],t);C=lerp(v[3],v[7],t);D=lerp(v[2],v[6],t);}
			else if(edge==10){B=lerp(v[1],v[5],t);C=lerp(v[0],v[4],t);D=lerp(v[3],v[7],t);}
			else {B=lerp(v[2],v[6],t);C=lerp(v[1],v[5],t);D=lerp(v[0],v[4],t);}
		}
		int mask=(A>=0?1:0)|(B>=0?2:0)|(C>=0?4:0)|(D>=0?8:0); float q=A*C-B*D;
		if(mask==5&&q>=1e-7f) return s<0; if(mask==10&&q<1e-7f) return s<0;
		return (mask==7||mask==11||mask==13||mask==14||mask==15)?s<0:s>0;
	}

	uint4 Selection(uint3 cell,float v[8]) {
		uint mask=0; [unroll] for(uint i=0;i<8;i++) if(v[i]>0) mask|=1u<<i;
		int c=L2(TableCases,mask,0), cfg=L2(TableCases,mask,1); if(c==0) return uint4(0,0,0,0);
		uint table=0,row=cfg,sub=0,count=0,center=0; int sc=0;
		if(c==1){table=Tiling1;count=1;} else if(c==2){table=Tiling2;count=2;}
		else if(c==3){bool q=FaceTest(v,L1(Test3,cfg));table=q?Tiling3_2:Tiling3_1;count=q?4:2;}
		else if(c==4){bool q=InteriorTest(v,c,cfg,0,L1(Test4,cfg));table=q?Tiling4_1:Tiling4_2;count=q?2:6;}
		else if(c==5){table=Tiling5;count=3;}
		else if(c==6){if(FaceTest(v,L2(Test6,cfg,0))){table=Tiling6_2;count=5;}else if(InteriorTest(v,c,cfg,0,L2(Test6,cfg,1))){table=Tiling6_1_1;count=3;}else{table=Tiling6_1_2;count=9;center=1;}}
		else if(c==7){sc=(FaceTest(v,L2(Test7,cfg,0))?1:0)+(FaceTest(v,L2(Test7,cfg,1))?2:0)+(FaceTest(v,L2(Test7,cfg,2))?4:0); if(sc==0){table=Tiling7_1;count=3;}else if(sc==1||sc==2||sc==4){table=Tiling7_2;sub=sc==1?0:(sc==2?1:2);count=5;}else if(sc==3||sc==5||sc==6){table=Tiling7_3;sub=sc==3?0:(sc==5?1:2);count=9;center=1;}else{bool q=InteriorTest(v,c,cfg,0,L2(Test7,cfg,3));table=q?Tiling7_4_1:Tiling7_4_2;count=q?5:9;}}
		else if(c==8){table=Tiling8;count=2;} else if(c==9){table=Tiling9;count=4;}
		else if(c==10){bool a=FaceTest(v,L2(Test10,cfg,0)),b=FaceTest(v,L2(Test10,cfg,1));if(a&&b){bool q=InteriorTest(v,c,cfg,0,-L2(Test10,cfg,2));table=q?Tiling10_1_1_:Tiling10_1_2;row=q?cfg:5-cfg;count=q?4:8;}else if(a){table=Tiling10_2;count=8;center=1;}else if(b){table=Tiling10_2_;count=8;center=1;}else{bool q=InteriorTest(v,c,cfg,0,L2(Test10,cfg,2));table=q?Tiling10_1_1:Tiling10_1_2;count=q?4:8;}}
		else if(c==11){table=Tiling11;count=4;}
		else if(c==12){bool a=FaceTest(v,L2(Test12,cfg,0)),b=FaceTest(v,L2(Test12,cfg,1));if(a&&b){bool q=InteriorTest(v,c,cfg,0,-L2(Test12,cfg,2));table=q?Tiling12_1_1_:Tiling12_1_2;row=q?cfg:23-cfg;count=q?4:8;}else if(a){table=Tiling12_2;count=8;center=1;}else if(b){table=Tiling12_2_;count=8;center=1;}else{bool q=InteriorTest(v,c,cfg,0,L2(Test12,cfg,2));table=q?Tiling12_1_1:Tiling12_1_2;count=q?4:8;}}
		else if(c==13){[unroll]for(uint f=0;f<6;f++)if(FaceTest(v,L2(Test13,cfg,f)))sc+=1u<<f;sc=L1(Subconfig13,sc);if(sc==0||sc==45){table=sc==0?Tiling13_1:Tiling13_1_;count=4;}else if((sc>=1&&sc<=6)||(sc>=39&&sc<=44)){table=sc<=6?Tiling13_2:Tiling13_2_;sub=sc<=6?sc-1:sc-39;count=6;}else if((sc>=7&&sc<=18)||(sc>=27&&sc<=38)){table=sc<=18?Tiling13_3:Tiling13_3_;sub=sc<=18?sc-7:sc-27;count=10;center=1;}else if(sc>=19&&sc<=22){table=Tiling13_4;sub=sc-19;count=12;center=1;}else{uint ss=sc-23;bool q=InteriorTest(v,c,cfg,ss,L2(Test13,cfg,6));table=q?Tiling13_5_1:Tiling13_5_2;sub=ss;count=q?6:10;}}
		else {table=Tiling14;count=4;}
		return uint4(table,row,sub,count|(center<<16));
	}

	uint EdgeSlot(uint3 base,uint axis){return (SampleIndex(base)*3)+axis;}
	uint CellEdgeSlot(uint3 c,uint e){uint3 b=c;uint a=0;if(e==1){b.x++;a=1;}else if(e==2){b.y++;}else if(e==3)a=1;else if(e==4)b.z++;else if(e==5){b.x++;b.z++;a=1;}else if(e==6){b.y++;b.z++;}else if(e==7){b.z++;a=1;}else if(e==8)a=2;else if(e==9){b.x++;a=2;}else if(e==10){b.x++;b.y++;a=2;}else if(e==11){b.y++;a=2;}return EdgeSlot(b,a);}
	int TilingEdge(uint4 s,uint n){return SignedLut(s.x,s.y,s.z,n);}
	float3 EdgePosition(uint3 c,uint e,float v[8]){uint2 ec=EdgeCorners[e];float t=v[ec.x]/(v[ec.x]-v[ec.y]+1e-7f);return (float3(c)+lerp((float3)Corners[ec.x],(float3)Corners[ec.y],saturate(t)))*VoxelSize+ChunkWorldOrigin;}

	[numthreads(8,8,4)] void MainCs(uint3 p:SV_DispatchThreadID) {
		if(Phase==1){
			uint lane=p.x+8*(p.y+8*p.z);
			uint edgeBlock=((uint)EdgeSlotCount+255)/256,edgeStart=lane*edgeBlock,edgeEnd=min(edgeStart+edgeBlock,(uint)EdgeSlotCount),local=0;
			for(uint i=edgeStart;i<edgeEnd;i++)local+=EdgeFlags[i]!=0?1:0;
			ScanTotals[lane]=local;GroupMemoryBarrierWithGroupSync();
			if(lane==0){uint prefix=0;for(uint i=0;i<256;i++){ScanBases[i]=prefix;prefix+=ScanTotals[i];}ScanEdgeTotal=prefix;}
			GroupMemoryBarrierWithGroupSync();local=ScanBases[lane];
			for(uint i=edgeStart;i<edgeEnd;i++){EdgeVertexIds[i]=local;if(EdgeFlags[i]!=0)local++;}
			GroupMemoryBarrierWithGroupSync();
			uint cellBlock=((uint)CellCount+255)/256,cellStart=lane*cellBlock,cellEnd=min(cellStart+cellBlock,(uint)CellCount);local=0;
			for(uint i=cellStart;i<cellEnd;i++)local+=(CellSelections[i].w&0x10000)!=0?1:0;
			ScanTotals[lane]=local;GroupMemoryBarrierWithGroupSync();
			if(lane==0){uint prefix=ScanEdgeTotal;for(uint i=0;i<256;i++){ScanBases[i]=prefix;prefix+=ScanTotals[i];}ScanCenterTotal=prefix;}
			GroupMemoryBarrierWithGroupSync();local=ScanBases[lane];
			for(uint i=cellStart;i<cellEnd;i++){CellCenterIds[i]=local;if((CellSelections[i].w&0x10000)!=0)local++;}
			GroupMemoryBarrierWithGroupSync();local=0;
			for(uint i=cellStart;i<cellEnd;i++)local+=(CellSelections[i].w&0xffff)*3;
			ScanTotals[lane]=local;GroupMemoryBarrierWithGroupSync();
			if(lane==0){uint prefix=0;for(uint i=0;i<256;i++){ScanBases[i]=prefix;prefix+=ScanTotals[i];}ScanIndexTotal=prefix;}
			GroupMemoryBarrierWithGroupSync();local=ScanBases[lane];
			for(uint i=cellStart;i<cellEnd;i++){CellIndexOffsets[i]=local;local+=(CellSelections[i].w&0xffff)*3;}
			GroupMemoryBarrierWithGroupSync();
			if(lane==0){Statistics[5]=ScanCenterTotal;Statistics[6]=ScanIndexTotal;Statistics[4]=(ScanCenterTotal>(uint)MaxVertices||ScanIndexTotal>(uint)MaxIndices)?1:0;}return;
		}
		if(any(p>=(uint)SampleSize))return;
		if(Phase==0){for(uint a=0;a<3;a++){bool valid=(a==0?p.x<(uint)ChunkSize:(a==1?p.y<(uint)ChunkSize:p.z<(uint)ChunkSize));uint slot=EdgeSlot(p,a);float va=Distance((int3)p),vb=Distance((int3)p+(a==0?int3(1,0,0):(a==1?int3(0,1,0):int3(0,0,1))));EdgeFlags[slot]=valid&&((va>0)!=(vb>0));}if(any(p>=(uint)ChunkSize))return;float v[8];[unroll]for(uint i=0;i<8;i++)v[i]=Distance((int3)(p+Corners[i]));uint4 s=Selection(p,v);CellSelections[CellIndex(p)]=s;uint old;InterlockedAdd(Statistics[0],1,old);if((s.w&0xffff)>0)InterlockedAdd(Statistics[1],1,old);return;}
		if(Statistics[4]!=0)return;
		if(Phase==2){for(uint a=0;a<3;a++){uint slot=EdgeSlot(p,a);if(EdgeFlags[slot]==0)continue;int3 q=(int3)p, r=q+(a==0?int3(1,0,0):(a==1?int3(0,1,0):int3(0,0,1)));float va=Distance(q),vb=Distance(r),t=saturate(va/(va-vb+1e-7f));float3 pos=(lerp((float3)q,(float3)r,t)*VoxelSize)+ChunkWorldOrigin;float3 n=SafeNormal(lerp(Gradient(q),Gradient(r),t),float3(0,0,1));PrototypeVertex o;o.Position=pos;o.Normal=n;o.Tangent=Tangent(n);o.TexCoord=pos.xy/128;OutputVertices[EdgeVertexIds[slot]]=o;}return;}
		if(any(p>=(uint)ChunkSize))return;uint ci=CellIndex(p);uint4 s=CellSelections[ci];uint tc=s.w&0xffff;if(tc==0)return;float v[8];[unroll]for(uint i=0;i<8;i++)v[i]=Distance((int3)(p+Corners[i]));if((s.w&0x10000)!=0){float3 cp=0,cn=0;uint count=0;[unroll]for(uint e=0;e<12;e++){uint slot=CellEdgeSlot(p,e);if(EdgeFlags[slot]!=0){PrototypeVertex ev=OutputVertices[EdgeVertexIds[slot]];cp+=ev.Position;cn+=ev.Normal;count++;}}PrototypeVertex cv;cv.Position=cp/max(count,1);cv.Normal=SafeNormal(cn,float3(0,0,1));cv.Tangent=Tangent(cv.Normal);cv.TexCoord=cv.Position.xy/128;OutputVertices[CellCenterIds[ci]]=cv;}
		uint off=CellIndexOffsets[ci];for(uint t=0;t<tc;t++){uint ids[3];float3 ps[3];[unroll]for(uint k=0;k<3;k++){int e=TilingEdge(s,t*3+k);ids[k]=e==12?CellCenterIds[ci]:EdgeVertexIds[CellEdgeSlot(p,e)];ps[k]=OutputVertices[ids[k]].Position;}float3 fn=cross(ps[1]-ps[0],ps[2]-ps[0]);float3 en=OutputVertices[ids[0]].Normal+OutputVertices[ids[1]].Normal+OutputVertices[ids[2]].Normal;if(dot(fn,en)<0){uint tmp=ids[1];ids[1]=ids[2];ids[2]=tmp;fn=-fn;}float a2=dot(fn,fn);if(a2<1e-12f){uint old;InterlockedAdd(Statistics[3],1,old);}float l0=dot(ps[1]-ps[0],ps[1]-ps[0]),l1=dot(ps[2]-ps[1],ps[2]-ps[1]),l2=dot(ps[0]-ps[2],ps[0]-ps[2]);float quality=2*sqrt(3*a2)/max(l0+l1+l2,1e-12f);if(quality<0.08f){uint old;InterlockedAdd(Statistics[7],1,old);}OutputIndices[off+t*3]=ids[0];OutputIndices[off+t*3+1]=ids[1];OutputIndices[off+t*3+2]=ids[2];}
		uint old;InterlockedAdd(Statistics[2],tc,old);
	}
}
