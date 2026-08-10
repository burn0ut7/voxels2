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

internal sealed class VoxelCollisionMeshData
{
	public List<Vector3> Vertices { get; }
	internal List<Vector3> Normals { get; }
	public List<int> Indices { get; }

	public VoxelCollisionMeshData( int estimatedVertexCount, int estimatedIndexCount )
	{
		Vertices = new List<Vector3>( estimatedVertexCount );
		Normals = new List<Vector3>( estimatedVertexCount );
		Indices = new List<int>( estimatedIndexCount );
	}

	public static VoxelCollisionMeshData FromVisual( VoxelMeshData visual )
	{
		var result = new VoxelCollisionMeshData( visual.Vertices.Count, visual.Indices.Count );
		foreach ( var vertex in visual.Vertices )
		{
			result.Vertices.Add( vertex.Position );
			result.Normals.Add( vertex.Normal );
		}
		result.Indices.AddRange( visual.Indices );
		return result;
	}
}
