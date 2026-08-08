internal sealed class VoxelGpuTerrainRenderer : SceneCustomObject, System.IDisposable
{
	public const int MaximumCommandsPerSubmission = 16;
	private const string ShaderName = "shaders/voxel_gpu_terrain.shader";
	private readonly CameraComponent _camera;
	private readonly VoxelGpuMeshPool _pool;
	private readonly VoxelGpuResidentTable _residents;
	private readonly VoxelGpuTerrainDiagnosticCounters _diagnostics;
	private readonly GpuBuffer<VoxelGpuResidentDescriptor> _residentBuffer;
	private readonly GpuBuffer<GpuBuffer.IndirectDrawIndexedArguments> _drawArguments;
	private readonly Sandbox.Rendering.CommandList[] _commandLists;
	private readonly RenderAttributes _attributes = new();
	private readonly Material _material;
	private int _attachedCommandListCount;
	private bool _dirty = true;
	private bool _disposed;

	public VoxelGpuTerrainRenderer( SceneWorld world, CameraComponent camera, VoxelGpuMeshPool pool, VoxelGpuResidentTable residents, VoxelGpuTerrainDiagnosticCounters diagnostics )
		: base( world )
	{
		_camera = camera ?? throw new System.ArgumentNullException( nameof( camera ) );
		_pool = pool ?? throw new System.ArgumentNullException( nameof( pool ) );
		_residents = residents ?? throw new System.ArgumentNullException( nameof( residents ) );
		_diagnostics = diagnostics ?? throw new System.ArgumentNullException( nameof( diagnostics ) );
		_material = Material.FromShader( ShaderName );
		_residentBuffer = new GpuBuffer<VoxelGpuResidentDescriptor>( residents.Capacity, GpuBuffer.UsageFlags.Structured, "Voxel GPU Residents" );
		_drawArguments = new GpuBuffer<GpuBuffer.IndirectDrawIndexedArguments>( residents.Capacity,
			GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.IndirectDrawArguments, "Voxel GPU Draw Commands" );
		var commandListCapacity = (residents.Capacity + MaximumCommandsPerSubmission - 1) / MaximumCommandsPerSubmission;
		_commandLists = Enumerable.Range( 0, commandListCapacity )
			.Select( index => new Sandbox.Rendering.CommandList( $"Voxel GPU Terrain Multi Draw {index}" ) )
			.ToArray();
		Bounds = BBox.FromPositionAndSize( Vector3.Zero, Vector3.One * 1_000_000_000.0f );
	}

	public void MarkDirty() => _dirty = true;

	public override void RenderSceneObject()
	{
		if ( _disposed ) return;
		if ( _dirty ) Rebuild();
	}

	private void Rebuild()
	{
		_dirty = false;
		for ( var index = 0; index < _attachedCommandListCount; index++ )
		{
			_camera.RemoveCommandList( _commandLists[index] );
		}
		_attachedCommandListCount = 0;
		var descriptorData = new VoxelGpuResidentDescriptor[_residents.Capacity];
		var arguments = new List<GpuBuffer.IndirectDrawIndexedArguments>( _residents.Count );
		var frustum = _camera.GetFrustum();
		foreach ( var (slot, entry) in _residents.PublishedEntries() )
		{
			descriptorData[slot] = entry.Descriptor;
			if ( entry.Descriptor.IndexCount == 0 ) continue;
			var boundsMin = new Vector3( entry.Descriptor.BoundsMin.x, entry.Descriptor.BoundsMin.y, entry.Descriptor.BoundsMin.z );
			var boundsMax = new Vector3( entry.Descriptor.BoundsMax.x, entry.Descriptor.BoundsMax.y, entry.Descriptor.BoundsMax.z );
			var bounds = new BBox( boundsMin, boundsMax );
			if ( !frustum.IsInside( bounds, true ) ) continue;
			arguments.Add( new GpuBuffer.IndirectDrawIndexedArguments
			{
				IndexCount = entry.Descriptor.IndexCount,
				InstanceCount = 1,
				FirstIndex = entry.Descriptor.IndexOffset,
				BaseVertex = (int)entry.Descriptor.VertexOffset,
				FirstInstance = 0
			} );
		}

		_residentBuffer.SetData( descriptorData );
		_diagnostics.VisibleDrawCommands = arguments.Count;
		if ( arguments.Count == 0 )
		{
			return;
		}

		_drawArguments.SetData( arguments, 0 );
		_attributes.Set( "TerrainResidents", _residentBuffer );
		for ( var offset = 0; offset < arguments.Count; offset += MaximumCommandsPerSubmission )
		{
			var commandList = _commandLists[_attachedCommandListCount];
			commandList.Reset();
			commandList.ResourceBarrierTransition( _pool.Vertices, Sandbox.Rendering.ResourceState.VertexOrIndexBuffer );
			commandList.ResourceBarrierTransition( _pool.Indices, Sandbox.Rendering.ResourceState.VertexOrIndexBuffer );
			commandList.ResourceBarrierTransition( _drawArguments, Sandbox.Rendering.ResourceState.IndirectArgument );
			commandList.DrawIndexedInstancedIndirect(
				_pool.Vertices,
				_pool.Indices,
				_material,
				_drawArguments,
				(uint)offset,
				_attributes,
				Graphics.PrimitiveType.Triangles,
				(uint)System.Math.Min( MaximumCommandsPerSubmission, arguments.Count - offset ),
				0 );
			_camera.AddCommandList( commandList, Sandbox.Rendering.Stage.AfterOpaque );
			_attachedCommandListCount++;
		}
	}

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		for ( var index = 0; index < _attachedCommandListCount; index++ )
			_camera.RemoveCommandList( _commandLists[index] );
		_drawArguments.Dispose();
		_residentBuffer.Dispose();
		Delete();
	}
}
