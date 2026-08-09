internal sealed class VoxelGpuTerrainRenderer : SceneCustomObject, System.IDisposable
{
	public const int MaximumCommandsPerSubmission = 16;
	public const string ShaderName = "shaders/voxel_gpu_terrain.shader";
	private readonly CameraComponent _camera;
	private readonly VoxelGpuMeshPool _pool;
	private readonly VoxelGpuResidentTable _residents;
	private readonly VoxelGpuTerrainDiagnosticCounters _diagnostics;
	private readonly float _cullingPaddingWorld;
	private readonly GpuBuffer<VoxelGpuResidentDescriptor> _residentBuffer;
	private readonly GpuBuffer<GpuBuffer.IndirectDrawIndexedArguments> _drawArguments;
	private readonly Sandbox.Rendering.CommandList[] _depthCommandLists;
	private readonly Sandbox.Rendering.CommandList[] _opaqueCommandLists;
	private readonly RenderAttributes _attributes = new();
	private readonly Material _material;
	private int _attachedDepthCommandListCount;
	private int _attachedOpaqueCommandListCount;
	private int _dirty = 1;
	private bool _hasCameraCullingState;
	private Vector3 _lastCameraPosition;
	private Vector3 _lastCameraForward;
	private Vector3 _lastCameraUp;
	private bool _disposed;

	public bool UsesProductionLighting => true;
	public bool UsesDepthPrepass => true;
	public int DepthPrepassCommandListCount => _attachedDepthCommandListCount;
	public int OpaqueCommandListCount => _attachedOpaqueCommandListCount;

	public VoxelGpuTerrainRenderer( SceneWorld world, CameraComponent camera, VoxelGpuMeshPool pool, VoxelGpuResidentTable residents, VoxelGpuTerrainDiagnosticCounters diagnostics, float cullingPaddingWorld )
		: base( world )
	{
		_camera = camera ?? throw new System.ArgumentNullException( nameof( camera ) );
		_pool = pool ?? throw new System.ArgumentNullException( nameof( pool ) );
		_residents = residents ?? throw new System.ArgumentNullException( nameof( residents ) );
		_diagnostics = diagnostics ?? throw new System.ArgumentNullException( nameof( diagnostics ) );
		_cullingPaddingWorld = System.MathF.Max( 0.0f, cullingPaddingWorld );
		_material = Material.FromShader( ShaderName );
		_residentBuffer = new GpuBuffer<VoxelGpuResidentDescriptor>( residents.Capacity, GpuBuffer.UsageFlags.Structured, "Voxel GPU Residents" );
		_drawArguments = new GpuBuffer<GpuBuffer.IndirectDrawIndexedArguments>( residents.Capacity,
			GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.IndirectDrawArguments, "Voxel GPU Draw Commands" );
		var commandListCapacity = (residents.Capacity + MaximumCommandsPerSubmission - 1) / MaximumCommandsPerSubmission;
		_depthCommandLists = Enumerable.Range( 0, commandListCapacity )
			.Select( index => new Sandbox.Rendering.CommandList( $"Voxel GPU Terrain Depth Multi Draw {index}" ) )
			.ToArray();
		_opaqueCommandLists = Enumerable.Range( 0, commandListCapacity )
			.Select( index => new Sandbox.Rendering.CommandList( $"Voxel GPU Terrain Opaque Multi Draw {index}" ) )
			.ToArray();
		_attributes.Set( "TerrainResidents", _residentBuffer );
		_drawArguments.SetData( new GpuBuffer.IndirectDrawIndexedArguments[residents.Capacity], 0 );
		for ( var index = 0; index < commandListCapacity; index++ )
		{
			var offset = index * MaximumCommandsPerSubmission;
			var commandCount = (uint)System.Math.Min( MaximumCommandsPerSubmission, residents.Capacity - offset );
			BuildAndAttachCommandList( _depthCommandLists[index], offset, commandCount, Sandbox.Rendering.Stage.AfterDepthPrepass );
			BuildAndAttachCommandList( _opaqueCommandLists[index], offset, commandCount, Sandbox.Rendering.Stage.AfterOpaque );
		}
		_attachedDepthCommandListCount = commandListCapacity;
		_attachedOpaqueCommandListCount = commandListCapacity;
		Bounds = BBox.FromPositionAndSize( Vector3.Zero, Vector3.One * 1_000_000_000.0f );
	}

	public void MarkDirty() => System.Threading.Interlocked.Exchange( ref _dirty, 1 );

	public override void RenderSceneObject()
	{
		if ( _disposed ) return;
		var cameraRotation = _camera.WorldRotation;
		var cameraPosition = _camera.WorldPosition;
		var cameraForward = cameraRotation.Forward;
		var cameraUp = cameraRotation.Up;
		var cameraChanged = !_hasCameraCullingState ||
			(cameraPosition - _lastCameraPosition).LengthSquared > 0.0001f ||
			(cameraForward - _lastCameraForward).LengthSquared > 0.000001f ||
			(cameraUp - _lastCameraUp).LengthSquared > 0.000001f;
		if ( cameraChanged )
		{
			_hasCameraCullingState = true;
			_lastCameraPosition = cameraPosition;
			_lastCameraForward = cameraForward;
			_lastCameraUp = cameraUp;
		}
		var residentsChanged = System.Threading.Interlocked.Exchange( ref _dirty, 0 ) != 0;
		if ( residentsChanged || cameraChanged ) Rebuild( residentsChanged );
	}

	private void Rebuild( bool uploadResidents )
	{
		var descriptorData = new VoxelGpuResidentDescriptor[_residents.Capacity];
		var arguments = new List<GpuBuffer.IndirectDrawIndexedArguments>( _residents.Capacity );
		var frustum = _camera.GetFrustum();
		foreach ( var (slot, entry) in _residents.PublishedEntries() )
		{
			descriptorData[slot] = entry.Descriptor;
			if ( entry.Descriptor.IndexCount == 0 ) continue;
			var boundsMin = new Vector3( entry.Descriptor.BoundsMin.x, entry.Descriptor.BoundsMin.y, entry.Descriptor.BoundsMin.z );
			var boundsMax = new Vector3( entry.Descriptor.BoundsMax.x, entry.Descriptor.BoundsMax.y, entry.Descriptor.BoundsMax.z );
			var padding = Vector3.One * _cullingPaddingWorld;
			var bounds = new BBox( boundsMin - padding, boundsMax + padding );
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

		var visibleCommandCount = arguments.Count;
		while ( arguments.Count < _residents.Capacity ) arguments.Add( default );
		if ( uploadResidents ) _residentBuffer.SetData( descriptorData );
		_diagnostics.VisibleDrawCommands = visibleCommandCount;
		_drawArguments.SetData( arguments, 0 );
	}

	private void BuildAndAttachCommandList( Sandbox.Rendering.CommandList commandList, int offset, uint commandCount, Sandbox.Rendering.Stage stage )
	{
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
			commandCount,
			0 );
		_camera.AddCommandList( commandList, stage );
	}

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		for ( var index = 0; index < _attachedDepthCommandListCount; index++ ) _camera.RemoveCommandList( _depthCommandLists[index] );
		for ( var index = 0; index < _attachedOpaqueCommandListCount; index++ ) _camera.RemoveCommandList( _opaqueCommandLists[index] );
		_attachedDepthCommandListCount = 0;
		_attachedOpaqueCommandListCount = 0;
		_drawArguments.Dispose();
		_residentBuffer.Dispose();
		Delete();
	}
}
