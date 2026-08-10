public sealed class VoxelBrush : Component
{
	private const float BrushRepeatInterval = 0.05f;
	private const string DigAction = "Attack1";
	private const string PlaceAction = "Attack2";
	private float _brushRepeatCooldown;
	private float _applyOnStartCountdown;
	private bool _applyOnStartPending;
	private bool _applyOnStartLastFrame;
	private int _applyOnStartRemaining;
	private int _applyOnStartIndex;
	private bool _aimMissLogged;

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

	[Property, Group( "Debug" )]
	public bool ApplyOnStartUsesAim { get; set; }

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
				if ( ApplyOnStartUsesAim ) ApplyAimedBrush( SdfDisplacement >= 0.0f, true );
				else ApplyBrush( LocalCenter + ApplyOnStartStep * _applyOnStartIndex );
				_applyOnStartIndex++;
				_applyOnStartRemaining--;
				_applyOnStartPending = _applyOnStartRemaining > 0;
				_applyOnStartCountdown = System.MathF.Max( ApplyOnStartInterval, BrushRepeatInterval );
			}
		}

		var dig = Input.Down( DigAction );
		var place = !dig && Input.Down( PlaceAction );
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
		_aimMissLogged = false;
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

	[Button( "Dig At Aim" )]
	public void DigAtAim()
	{
		ApplyAimedBrush( true );
	}

	[Button( "Place At Aim" )]
	public void PlaceAtAim()
	{
		ApplyAimedBrush( false );
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

	private void ApplyAimedBrush( bool dig, bool forceCameraAim = false )
	{
		if ( !TryGetVoxelWorld( out var voxelWorld ) )
		{
			return;
		}

		if ( !TryGetAimTransform( forceCameraAim, out var aim ) )
		{
			return;
		}

		if ( !voxelWorld.TryRaycastSdf( aim.Position, aim.Rotation.Forward, TraceDistance, out var hitPosition ) )
		{
			if ( !_aimMissLogged ) Log.Warning( $"Voxel brush aim did not intersect the authoritative terrain SDF: start={aim.Position}, direction={aim.Rotation.Forward}, range={TraceDistance:F1}." );
			_aimMissLogged = true;
			return;
		}

		_aimMissLogged = false;
		var strength = System.MathF.Abs( SdfDisplacement );
		voxelWorld.DisplaceSdf( hitPosition, Radius, dig ? strength : -strength );
	}

	private bool TryGetAimTransform( bool forceCameraAim, out Transform aim )
	{
		if ( !forceCameraAim )
		{
			foreach ( var controller in Scene.GetAllComponents<PlayerController>() )
			{
				if ( !controller.Active || !controller.GameObject.Enabled || !controller.GameObject.Active ) continue;
				if ( controller.GameObject.Network.Active && !controller.GameObject.Network.IsOwner ) continue;
				aim = controller.EyeTransform;
				return true;
			}
		}

		if ( Scene.Camera is not null )
		{
			aim = Scene.Camera.WorldTransform;
			return true;
		}

		aim = default;
		return false;
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
