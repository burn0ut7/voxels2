internal sealed class VoxelGpuTerrainRenderer : SceneCustomObject, System.IDisposable
{
	public const string ShaderName = "shaders/voxel_gpu_terrain.shader";
	private readonly int _commandsPerSubmission;
	private readonly CameraComponent _camera;
	private readonly VoxelGpuMeshPool _pool;
	private readonly VoxelGpuResidentTable _residents;
	private readonly VoxelGpuTerrainDiagnosticCounters _diagnostics;
	private readonly float _cullingPaddingWorld;
	private readonly GpuBuffer<GpuBuffer.IndirectDrawIndexedArguments> _drawArguments;
	private readonly VoxelGpuResidentTable.ResidentEntry[] _residentSnapshot;
	private readonly GpuBuffer.IndirectDrawIndexedArguments[] _argumentData;
	private readonly GpuBuffer.IndirectDrawIndexedArguments[] _uploadedArgumentData;
	private readonly Sandbox.Rendering.CommandList[] _depthCommandLists;
	private readonly Sandbox.Rendering.CommandList[] _opaqueCommandLists;
	private readonly RenderAttributes _attributes = new();
	private readonly Material _material;
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
	private bool _hasUploadedArguments;
	private int _uploadedArgumentCount;
	private bool _renderingEnabled = true;
	private bool _disposed;
	private int _publishedRenderableRegularCommandCount;
	private int _publishedRenderableTransitionCommandCount;
	private int _visibleRegularCommandCount;
	private int _visibleTransitionCommandCount;
	private double _lastCullingMilliseconds;
	private double _lastArgumentBuildMilliseconds;
	private long _lastArgumentUploadBytes;

	public bool UsesProductionLighting => true;
	public bool UsesDepthPrepass => true;
	public int DepthPrepassCommandListCount => _attachedDepthCommandListCount;
	public int OpaqueCommandListCount => _attachedOpaqueCommandListCount;
	public long CullingRebuildCount => _cullingRebuildCount;
	public long ArgumentUploadCount => _argumentUploadCount;
	public bool IsTerrainRenderingEnabled => _renderingEnabled;
	public int CommandsPerSubmission => _commandsPerSubmission;
	public int PublishedRenderableRegularCommandCount => _publishedRenderableRegularCommandCount;
	public int PublishedRenderableTransitionCommandCount => _publishedRenderableTransitionCommandCount;
	public int VisibleRegularCommandCount => _visibleRegularCommandCount;
	public int VisibleTransitionCommandCount => _visibleTransitionCommandCount;
	public double LastCullingMilliseconds => _lastCullingMilliseconds;
	public double LastArgumentBuildMilliseconds => _lastArgumentBuildMilliseconds;
	public long LastArgumentUploadBytes => _lastArgumentUploadBytes;

	public VoxelGpuTerrainRenderer( SceneWorld world, CameraComponent camera, VoxelGpuMeshPool pool, VoxelGpuResidentTable residents, VoxelGpuTerrainDiagnosticCounters diagnostics, int commandsPerSubmission, float cullingPaddingWorld )
		: base( world )
	{
		_camera = camera ?? throw new System.ArgumentNullException( nameof( camera ) );
		_pool = pool ?? throw new System.ArgumentNullException( nameof( pool ) );
		_residents = residents ?? throw new System.ArgumentNullException( nameof( residents ) );
		_diagnostics = diagnostics ?? throw new System.ArgumentNullException( nameof( diagnostics ) );
		if ( commandsPerSubmission < 1 ) throw new System.ArgumentOutOfRangeException( nameof( commandsPerSubmission ) );
		_commandsPerSubmission = commandsPerSubmission;
		_cullingPaddingWorld = System.MathF.Max( 0.0f, cullingPaddingWorld );
		_residentSnapshot = new VoxelGpuResidentTable.ResidentEntry[residents.Capacity];
		_argumentData = new GpuBuffer.IndirectDrawIndexedArguments[residents.Capacity];
		_uploadedArgumentData = new GpuBuffer.IndirectDrawIndexedArguments[residents.Capacity];
		_material = Material.FromShader( ShaderName );
		_drawArguments = new GpuBuffer<GpuBuffer.IndirectDrawIndexedArguments>( residents.Capacity,
			GpuBuffer.UsageFlags.Structured | GpuBuffer.UsageFlags.IndirectDrawArguments, "Voxel GPU Draw Commands" );
		var commandListCapacity = (residents.Capacity + _commandsPerSubmission - 1) / _commandsPerSubmission;
		_depthCommandLists = new Sandbox.Rendering.CommandList[commandListCapacity];
		_opaqueCommandLists = new Sandbox.Rendering.CommandList[commandListCapacity];
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
		if ( residentsChanged || cameraChanged ) Rebuild();
	}

	private void Rebuild()
	{
		var rebuildStart = System.Diagnostics.Stopwatch.GetTimestamp();
		_cullingRebuildCount++;
		var publishedCount = _residents.CopyPublishedEntries( _residentSnapshot );
		var frustum = _camera.GetFrustum();
		var visibleCommandCount = 0;
		var transitionVisibleCommandCount = 0;
		var publishedRenderableRegularCount = 0;
		var publishedRenderableTransitionCount = 0;
		for ( var index = 0; index < publishedCount; index++ )
		{
			var entry = _residentSnapshot[index];
			if ( entry.Descriptor.IndexCount == 0 ) continue;
			if ( entry.Key.IsTransition ) publishedRenderableTransitionCount++;
			else publishedRenderableRegularCount++;
			var boundsMin = new Vector3( entry.Descriptor.BoundsMin.x, entry.Descriptor.BoundsMin.y, entry.Descriptor.BoundsMin.z );
			var boundsMax = new Vector3( entry.Descriptor.BoundsMax.x, entry.Descriptor.BoundsMax.y, entry.Descriptor.BoundsMax.z );
			var padding = Vector3.One * (_cullingPaddingWorld * 0.25f);
			var bounds = new BBox( boundsMin - padding, boundsMax + padding );
			if ( !frustum.IsInside( bounds, true ) ) continue;
			if ( entry.Key.IsTransition ) transitionVisibleCommandCount++;
			_argumentData[visibleCommandCount++] = new GpuBuffer.IndirectDrawIndexedArguments
			{
				IndexCount = entry.Descriptor.IndexCount,
				InstanceCount = 1,
				FirstIndex = entry.Descriptor.IndexOffset,
				BaseVertex = (int)entry.Descriptor.VertexOffset,
				FirstInstance = 0
			};
		}
		_lastCullingMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( rebuildStart ).TotalMilliseconds;

		var visibleUploadCount = visibleCommandCount == 0 ? 0 :
			System.Math.Min( _argumentData.Length, ((visibleCommandCount + _commandsPerSubmission - 1) / _commandsPerSubmission) * _commandsPerSubmission );
		var uploadCount = System.Math.Max( visibleUploadCount, _attachedDepthCommandListCount * _commandsPerSubmission );
		if ( uploadCount > visibleCommandCount ) System.Array.Clear( _argumentData, visibleCommandCount, uploadCount - visibleCommandCount );
		_lastArgumentBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( rebuildStart ).TotalMilliseconds - _lastCullingMilliseconds;
		_publishedRenderableRegularCommandCount = publishedRenderableRegularCount;
		_publishedRenderableTransitionCommandCount = publishedRenderableTransitionCount;
		_visibleRegularCommandCount = visibleCommandCount - transitionVisibleCommandCount;
		_visibleTransitionCommandCount = transitionVisibleCommandCount;
		_diagnostics.VisibleDrawCommands = visibleCommandCount;
		_diagnostics.TransitionVisibleDrawCommands = transitionVisibleCommandCount;
		if ( ArgumentsChanged( uploadCount ) )
		{
			_argumentUploadCount++;
			if ( uploadCount > 0 ) System.Array.Copy( _argumentData, _uploadedArgumentData, uploadCount );
			_uploadedArgumentCount = uploadCount;
			_hasUploadedArguments = true;
			_lastArgumentUploadBytes = (long)uploadCount * 20;
			if ( uploadCount > 0 )
			{
				var argumentUploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
				_drawArguments.SetData( new System.Span<GpuBuffer.IndirectDrawIndexedArguments>( _argumentData, 0, uploadCount ) );
				var argumentUploadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( argumentUploadStart ).TotalMilliseconds;
				if ( argumentUploadMilliseconds >= 8.0 && System.Threading.Interlocked.Increment( ref _slowUploadLogCount ) <= 32 )
					Log.Info( $"Voxel GPU indirect argument upload: {argumentUploadMilliseconds:F2}ms, commands={visibleCommandCount:N0}, uploaded={uploadCount:N0}." );
			}
		}
		UpdateCommandLists( visibleUploadCount / _commandsPerSubmission );
	}

	private bool ArgumentsChanged( int uploadCount )
	{
		if ( !_hasUploadedArguments || uploadCount != _uploadedArgumentCount ) return true;
		for ( var index = 0; index < uploadCount; index++ )
		{
			var current = _argumentData[index];
			var previous = _uploadedArgumentData[index];
			if ( current.IndexCount != previous.IndexCount || current.InstanceCount != previous.InstanceCount ||
				current.FirstIndex != previous.FirstIndex || current.BaseVertex != previous.BaseVertex || current.FirstInstance != previous.FirstInstance ) return true;
		}
		return false;
	}

	private void UpdateCommandLists( int requiredCount )
	{
		// Removing and reattaching command lists can rebuild the custom render layer for a frame.
		// Keep a bounded high-water mark and zero unused indirect commands instead.
		for ( var index = _attachedDepthCommandListCount; index < requiredCount; index++ )
		{
			_depthCommandLists[index] ??= new Sandbox.Rendering.CommandList( $"Voxel GPU Terrain Depth Multi Draw {index}" );
			_opaqueCommandLists[index] ??= new Sandbox.Rendering.CommandList( $"Voxel GPU Terrain Opaque Multi Draw {index}" );
			var offset = index * _commandsPerSubmission;
			var commandCount = (uint)System.Math.Min( _commandsPerSubmission, _residents.Capacity - offset );
			BuildCommandList( _depthCommandLists[index], offset, commandCount );
			BuildCommandList( _opaqueCommandLists[index], offset, commandCount );
			if ( _renderingEnabled )
			{
				_camera.AddCommandList( _depthCommandLists[index], Sandbox.Rendering.Stage.AfterDepthPrepass );
				_camera.AddCommandList( _opaqueCommandLists[index], Sandbox.Rendering.Stage.AfterOpaque );
			}
		}
		if ( requiredCount > _attachedDepthCommandListCount ) _attachedDepthCommandListCount = requiredCount;
		if ( requiredCount > _attachedOpaqueCommandListCount ) _attachedOpaqueCommandListCount = requiredCount;
	}

	private void BuildCommandList( Sandbox.Rendering.CommandList commandList, int offset, uint commandCount )
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
		Delete();
	}
}
