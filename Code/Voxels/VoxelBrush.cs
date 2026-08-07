public sealed class VoxelBrush : Component
{
	private const float BrushRepeatInterval = 0.05f;
	private float _brushRepeatCooldown;
	private float _applyOnStartCountdown;
	private bool _applyOnStartPending;
	private bool _applyOnStartLastFrame;
	private int _applyOnStartRemaining;
	private int _applyOnStartIndex;

	[Property, Group( "Brush" )]
	public Vector3 LocalCenter { get; set; }

	[Property, Group( "Brush" ), Range( 1.0f, 1024.0f )]
	public float Radius { get; set; } = 128.0f;

	[Property, Group( "Brush" ), Range( -512.0f, 512.0f )]
	public float SdfDisplacement { get; set; } = 64.0f;

	[Property, Group( "Brush" ), Range( 1.0f, 100000.0f )]
	public float TraceDistance { get; set; } = 10000.0f;

	[Property, Group( "Debug" )]
	public bool ApplyOnStart { get; set; }

	[Property, Group( "Debug" ), Range( 0.0f, 10.0f )]
	public float ApplyOnStartDelay { get; set; }

	[Property, Group( "Debug" ), Range( 1, 64 )]
	public int ApplyOnStartCount { get; set; } = 1;

	[Property, Group( "Debug" ), Range( 0.05f, 1.0f )]
	public float ApplyOnStartInterval { get; set; } = BrushRepeatInterval;

	[Property, Group( "Debug" )]
	public Vector3 ApplyOnStartStep { get; set; }

	protected override void OnStart()
	{
		if ( ApplyOnStart )
		{
			BeginDebugBrushSequence();
		}
		_applyOnStartLastFrame = ApplyOnStart;
	}

	protected override void OnUpdate()
	{
		if ( ApplyOnStart && !_applyOnStartLastFrame )
		{
			BeginDebugBrushSequence();
		}
		_applyOnStartLastFrame = ApplyOnStart;

		if ( _applyOnStartPending )
		{
			_applyOnStartCountdown -= Time.Delta;
			if ( _applyOnStartCountdown <= 0.0f )
			{
				ApplyBrush( LocalCenter + ApplyOnStartStep * _applyOnStartIndex );
				_applyOnStartIndex++;
				_applyOnStartRemaining--;
				_applyOnStartPending = _applyOnStartRemaining > 0;
				_applyOnStartCountdown = System.MathF.Max( ApplyOnStartInterval, BrushRepeatInterval );
			}
		}

		var dig = Input.Down( "attack1" );
		var place = !dig && Input.Down( "attack2" );
		if ( !dig && !place )
		{
			_brushRepeatCooldown = 0.0f;
			return;
		}

		_brushRepeatCooldown -= Time.Delta;
		if ( _brushRepeatCooldown > 0.0f )
		{
			return;
		}

		ApplyAimedBrush( dig );
		_brushRepeatCooldown = BrushRepeatInterval;
	}

	private void BeginDebugBrushSequence()
	{
		_applyOnStartCountdown = ApplyOnStartDelay;
		_applyOnStartRemaining = System.Math.Clamp( ApplyOnStartCount, 1, 64 );
		_applyOnStartIndex = 0;
		_applyOnStartPending = true;
	}

	[Button]
	public void ApplyBrush()
	{
		ApplyBrush( LocalCenter );
	}

	private void ApplyBrush( Vector3 localCenter )
	{
		if ( !TryGetVoxelWorld( out var voxelWorld ) )
		{
			return;
		}

		var worldCenter = GameObject.WorldTransform.PointToWorld( localCenter );
		voxelWorld.DisplaceSdf( worldCenter, Radius, SdfDisplacement );
	}

	private void ApplyAimedBrush( bool dig )
	{
		if ( !TryGetVoxelWorld( out var voxelWorld ) )
		{
			return;
		}

		var camera = Scene.Camera;
		if ( camera is null )
		{
			return;
		}

		var traceStart = camera.WorldPosition;
		var traceEnd = traceStart + camera.WorldRotation.Forward * TraceDistance;
		var trace = Scene.Trace.Ray( traceStart, traceEnd )
			.WithTag( VoxelManager.ChunkTag )
			.Run();
		if ( !trace.Hit || trace.GameObject?.Parent != GameObject )
		{
			return;
		}

		var strength = System.MathF.Abs( SdfDisplacement );
		voxelWorld.DisplaceSdf( trace.HitPosition, Radius, dig ? strength : -strength );
	}

	private bool TryGetVoxelWorld( out VoxelManager voxelWorld )
	{
		voxelWorld = GameObject.Components.Get<VoxelManager>();
		if ( voxelWorld is not null )
		{
			return true;
		}

		Log.Warning( "Voxel brush owner must also have a VoxelManager component." );
		return false;
	}
}
