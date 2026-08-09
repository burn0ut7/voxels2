MODES
{
	Default();
}
// GPU LOD crack plan: force a clean live rebuild after orientation ownership fix.
CS
{
	#include "system.fxc"
	#include "voxel_transvoxel_orientation.fxc"
	StructuredBuffer<float> Samples < Attribute( "Samples" ); >;
	StructuredBuffer<uint> Lookup < Attribute( "Lookup" ); >;
	RWStructuredBuffer<float4> OutputVertices < Attribute( "OutputVertices" ); >;
	RWStructuredBuffer<uint> OutputIndices < Attribute( "OutputIndices" ); >;
	RWStructuredBuffer<uint> Statistics < Attribute( "Statistics" ); >;
	int VariantCount < Attribute( "VariantCount" ); >;
	int GeometryOffset < Attribute( "GeometryOffset" ); >;
	int TriangleOffset < Attribute( "TriangleOffset" ); >;
	int VertexOffset < Attribute( "VertexOffset" ); >;

	static const uint CaseOrder[9] = { 0, 1, 2, 5, 8, 7, 6, 3, 4 };


	[numthreads(64,1,1)]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if ( id.x >= (uint)VariantCount ) return;
		uint variant = id.x;
		uint caseCode = variant % 512;
		uint face = (variant / 512) % 6;
		bool reverseWinding = (variant / 3072) != 0;
		uint sampleBase = caseCode * 13;
		uint computedCase = 0;
		[unroll] for ( uint bit = 0; bit < 9; bit++ ) if ( Samples[sampleBase + CaseOrder[bit]] < 0) computedCase |= 1u << bit;
		if ( computedCase != caseCode ) InterlockedAdd( Statistics[0], 1 );

		uint cellClass = Lookup[caseCode];
		uint geometryCounts = Lookup[(uint)GeometryOffset + (cellClass & 0x7f)];
		uint vertexCount = geometryCounts >> 4;
		uint triangleCount = geometryCounts & 0xf;
		uint vertexBase = variant * 12;
		[unroll] for ( uint vertex = 0; vertex < 12; vertex++ )
		{
			if ( vertex >= vertexCount ) break;
			uint edge = Lookup[(uint)VertexOffset + caseCode * 12 + vertex] & 0xff;
			uint first = (edge >> 4) & 0xf;
			uint second = edge & 0xf;
			float firstSample = Samples[sampleBase + first];
			float secondSample = Samples[sampleBase + second];
			float denominator = firstSample - secondSample;
			float t = abs(denominator) > 0.000001f ? firstSample / denominator : 0.5f;
			t = clamp(t, 0, 1);
			float3 position = lerp(TransitionProofSamplePosition(first, face), TransitionProofSamplePosition(second, face), t);
			OutputVertices[vertexBase + vertex] = float4(position, 1);
		}

		uint triangleBase = (uint)TriangleOffset + (cellClass & 0x7f) * 36;
		uint indexBase = variant * 36;
		bool flipTriangles = ((cellClass & 0x80) != 0) != reverseWinding;
		[unroll] for ( uint triangle = 0; triangle < 12; triangle++ )
		{
			if ( triangle >= triangleCount ) break;
			uint source = triangleBase + triangle * 3;
			uint first = Lookup[source];
			uint second = Lookup[source + 1];
			uint third = Lookup[source + 2];
			if ( flipTriangles )
			{
				OutputIndices[indexBase + triangle * 3] = vertexBase + first;
				OutputIndices[indexBase + triangle * 3 + 1] = vertexBase + second;
				OutputIndices[indexBase + triangle * 3 + 2] = vertexBase + third;
			}
			else
			{
				OutputIndices[indexBase + triangle * 3] = vertexBase + third;
				OutputIndices[indexBase + triangle * 3 + 1] = vertexBase + second;
				OutputIndices[indexBase + triangle * 3 + 2] = vertexBase + first;
			}
		}
		InterlockedAdd( Statistics[1], vertexCount );
		InterlockedAdd( Statistics[2], triangleCount );
	}
}
