MODES
{
	Default();
}
// Versioned packing of one compact 32-byte count result per request.
CS
{
	#include "system.fxc"
	struct BlockRequest
	{
		float4 SampleOrigin;
		float4 ReservedOrigin;
		int CoordinateX;
		int CoordinateY;
		int CoordinateZ;
		int Lod;
		uint RuleVersion;
		uint Generation;
		uint ResidentSlot;
		uint Reserved;
	};
	struct CountResult
	{
		uint VertexCount;
		uint IndexCount;
		uint Generation;
		uint RequestIndex;
		uint Overflow;
		uint ActiveCells;
		uint Reserved0;
		uint Reserved1;
	};
	StructuredBuffer<BlockRequest> BlockRequests < Attribute( "BlockRequests" ); >;
	StructuredBuffer<uint> BlockCounts < Attribute( "BlockCounts" ); >;
	RWStructuredBuffer<CountResult> CountResults < Attribute( "CountResults" ); >;
	int BatchSize < Attribute( "BatchSize" ); >;
	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if ( id.x >= (uint)BatchSize ) return;
		CountResult result;
		result.VertexCount = BlockCounts[id.x * 2];
		result.IndexCount = BlockCounts[id.x * 2 + 1];
		result.Generation = BlockRequests[id.x].Generation;
		result.RequestIndex = id.x;
		result.Overflow = 0;
		result.ActiveCells = result.IndexCount / 3;
		result.Reserved0 = 0;
		result.Reserved1 = 0;
		CountResults[id.x] = result;
	}
}
