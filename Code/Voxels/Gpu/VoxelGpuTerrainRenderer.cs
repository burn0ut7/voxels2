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
	private readonly VoxelGpuResidentTable.ResidentEntry[] _residentSnapshot;
	private readonly VoxelGpuResidentDescriptor[] _descriptorData;
	private readonly GpuBuffer.IndirectDrawIndexedArguments[] _argumentData;
	private readonly GpuBuffer.IndirectDrawIndexedArguments[] _uploadedArgumentData;
	private readonly Sandbox.Rendering.CommandList[] _depthCommandLists;
	private readonly Sandbox.Rendering.CommandList[] _opaqueCommandLists;
	private readonly RenderAttributes _attributes = new();
	private readonly Material _material;
	private readonly long _cullingRefreshIntervalTicks = System.Diagnostics.Stopwatch.Frequency / 30;
	private readonly float _cullingPositionThresholdSquared;
	private readonly float _cullingDirectionThresholdSquared;
	private int _attachedDepthCommandListCount;
	private int _attachedOpaqueCommandListCount;
	private long _cullingRebuildCount;
	private long _argumentUploadCount;
	private int _slowUploadLogCount;
	private int _dirty = 1;
	private bool _hasCameraCullingState;
	private Vector3 _lastCameraPosition;
	private Vector3 _lastCameraForward;
	private Vector3 _lastCameraUp;
	private long _nextCullingRefreshTimestamp;
	private bool _hasUploadedArguments;
	private int _uploadedArgumentCount;
	private bool _renderingEnabled = true;
	private bool _disposed;

	public bool UsesProductionLighting => true;
	public bool UsesDepthPrepass => true;
	public int DepthPrepassCommandListCount => _attachedDepthCommandListCount;
	public int OpaqueCommandListCount => _attachedOpaqueCommandListCount;
	public long CullingRebuildCount => _cullingRebuildCount;
	public long ArgumentUploadCount => _argumentUploadCount;
	public bool IsTerrainRenderingEnabled => _renderingEnabled;

	public VoxelGpuTerrainRenderer( SceneWorld world, CameraComponent camera, VoxelGpuMeshPool pool, VoxelGpuResidentTable residents, VoxelGpuTerrainDiagnosticCounters diagnostics, float cullingPaddingWorld )
		: base( world )
	{
		_camera = camera ?? throw new System.ArgumentNullException( nameof( camera ) );
		_pool = pool ?? throw new System.ArgumentNullException( nameof( pool ) );
		_residents = residents ?? throw new System.ArgumentNullException( nameof( residents ) );
		_diagnostics = diagnostics ?? throw new System.ArgumentNullException( nameof( diagnostics ) );
		_cullingPaddingWorld = System.MathF.Max( 0.0f, cullingPaddingWorld );
		var cullingPositionThreshold = _cullingPaddingWorld > 0.0f ? System.MathF.Max( 16.0f, _cullingPaddingWorld * 0.25f ) : 0.0f;
		_cullingPositionThresholdSquared = cullingPositionThreshold * cullingPositionThreshold;
		_cullingDirectionThresholdSquared = _cullingPaddingWorld > 0.0f ? 0.02f : 0.000001f;
		_residentSnapshot = new VoxelGpuResidentTable.ResidentEntry[residents.Capacity];
		_descriptorData = new VoxelGpuResidentDescriptor[residents.Capacity];
		_argumentData = new GpuBuffer.IndirectDrawIndexedArguments[residents.Capacity];
		_uploadedArgumentData = new GpuBuffer.IndirectDrawIndexedArguments[residents.Capacity];
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
		_drawArguments.SetData( _argumentData, 0 );
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

	public void SetRenderingEnabled( bool enabled )
	{
		if ( _disposed || _renderingEnabled == enabled ) return;
		_renderingEnabled = enabled;
		for ( var index = 0; index < _attachedDepthCommandListCount; index++ )
		{
			if ( enabled ) _camera.AddCommandList( _depthCommandLists[index], Sandbox.Rendering.Stage.AfterDepthPrepass );
			else _camera.RemoveCommandList( _depthCommandLists[index] );
		}
		for ( var index = 0; index < _attachedOpaqueCommandListCount; index++ )
		{
			if ( enabled ) _camera.AddCommandList( _opaqueCommandLists[index], Sandbox.Rendering.Stage.AfterOpaque );
			else _camera.RemoveCommandList( _opaqueCommandLists[index] );
		}
	}

	public override void RenderSceneObject()
	{
		if ( _disposed ) return;
		var cameraRotation = _camera.WorldRotation;
		var cameraPosition = _camera.WorldPosition;
		var cameraForward = cameraRotation.Forward;
		var cameraUp = cameraRotation.Up;
		var cameraChanged = !_hasCameraCullingState ||
			(cameraPosition - _lastCameraPosition).LengthSquared > _cullingPositionThresholdSquared ||
			(cameraForward - _lastCameraForward).LengthSquared > _cullingDirectionThresholdSquared ||
			(cameraUp - _lastCameraUp).LengthSquared > _cullingDirectionThresholdSquared;
		var now = System.Diagnostics.Stopwatch.GetTimestamp();
		var cullingRefreshDue = now >= _nextCullingRefreshTimestamp;
		var cullingChanged = cameraChanged && cullingRefreshDue;
		if ( cullingChanged )
		{
			_hasCameraCullingState = true;
			_lastCameraPosition = cameraPosition;
			_lastCameraForward = cameraForward;
			_lastCameraUp = cameraUp;
			_nextCullingRefreshTimestamp = now + _cullingRefreshIntervalTicks;
		}
		var residentsChanged = System.Threading.Interlocked.Exchange( ref _dirty, 0 ) != 0;
		if ( residentsChanged || cullingChanged ) Rebuild( residentsChanged );
	}

	private void Rebuild( bool uploadResidents )
	{
		_cullingRebuildCount++;
		_residents.CopyEntries( _residentSnapshot );
		var frustum = _camera.GetFrustum();
		var visibleCommandCount = 0;
		for ( var slot = 0; slot < _residentSnapshot.Length; slot++ )
		{
			var entry = _residentSnapshot[slot];
			if ( uploadResidents ) _descriptorData[slot] = entry.Published ? entry.Descriptor : default;
			if ( !entry.Published ) continue;
			if ( entry.Descriptor.IndexCount == 0 ) continue;
			var boundsMin = new Vector3( entry.Descriptor.BoundsMin.x, entry.Descriptor.BoundsMin.y, entry.Descriptor.BoundsMin.z );
			var boundsMax = new Vector3( entry.Descriptor.BoundsMax.x, entry.Descriptor.BoundsMax.y, entry.Descriptor.BoundsMax.z );
			var padding = Vector3.One * (_cullingPaddingWorld * 0.25f);
			var bounds = new BBox( boundsMin - padding, boundsMax + padding );
			if ( !frustum.IsInside( bounds, true ) ) continue;
			_argumentData[visibleCommandCount++] = new GpuBuffer.IndirectDrawIndexedArguments
			{
				IndexCount = entry.Descriptor.IndexCount,
				InstanceCount = 1,
				FirstIndex = entry.Descriptor.IndexOffset,
				BaseVertex = (int)entry.Descriptor.VertexOffset,
				FirstInstance = 0
			};
		}

		System.Array.Clear( _argumentData, visibleCommandCount, _argumentData.Length - visibleCommandCount );
		if ( uploadResidents )
		{
			var residentUploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
			_residentBuffer.SetData( _descriptorData );
			var residentUploadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( residentUploadStart ).TotalMilliseconds;
			if ( residentUploadMilliseconds >= 8.0 && System.Threading.Interlocked.Increment( ref _slowUploadLogCount ) <= 32 )
				Log.Info( $"Voxel GPU resident buffer upload: {residentUploadMilliseconds:F2}ms, capacity={_descriptorData.Length:N0}." );
		}
		_diagnostics.VisibleDrawCommands = visibleCommandCount;
		if ( ArgumentsChanged( visibleCommandCount ) )
		{
			_argumentUploadCount++;
			System.Array.Copy( _argumentData, _uploadedArgumentData, _argumentData.Length );
			_uploadedArgumentCount = visibleCommandCount;
			_hasUploadedArguments = true;
			var argumentUploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
			_drawArguments.SetData( _argumentData, 0 );
			var argumentUploadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( argumentUploadStart ).TotalMilliseconds;
			if ( argumentUploadMilliseconds >= 8.0 && System.Threading.Interlocked.Increment( ref _slowUploadLogCount ) <= 32 )
				Log.Info( $"Voxel GPU indirect argument upload: {argumentUploadMilliseconds:F2}ms, commands={visibleCommandCount:N0}, capacity={_argumentData.Length:N0}." );
		}
	}

	private bool ArgumentsChanged( int visibleCommandCount )
	{
		if ( !_hasUploadedArguments || visibleCommandCount != _uploadedArgumentCount ) return true;
		for ( var index = 0; index < _argumentData.Length; index++ )
		{
			var current = _argumentData[index];
			var previous = _uploadedArgumentData[index];
			if ( current.IndexCount != previous.IndexCount || current.InstanceCount != previous.InstanceCount ||
				current.FirstIndex != previous.FirstIndex || current.BaseVertex != previous.BaseVertex || current.FirstInstance != previous.FirstInstance ) return true;
		}
		return false;
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
