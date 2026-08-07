internal sealed class VoxelMeshData
{
	public List<Vertex> Vertices { get; }
	public List<int> Indices { get; }

	public VoxelMeshData( int estimatedVertexCount, int estimatedIndexCount )
	{
		Vertices = new List<Vertex>( estimatedVertexCount );
		Indices = new List<int>( estimatedIndexCount );
	}
}
