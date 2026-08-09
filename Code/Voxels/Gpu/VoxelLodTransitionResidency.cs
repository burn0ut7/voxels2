internal sealed class VoxelLodTransitionResidency
{
	private readonly HashSet<VoxelLodTransitionDescriptor> _desired = new();
	private readonly HashSet<VoxelLodTransitionDescriptor> _published = new();
	private readonly List<VoxelLodTransitionDescriptor> _entering = new();
	private readonly List<VoxelLodTransitionDescriptor> _leaving = new();
	private ulong _publishedDependencyGeneration;
	private bool _hasPublishedGeneration;

	public int DesiredCount => _desired.Count;
	public int PublishedCount => _published.Count;
	public IReadOnlyCollection<VoxelLodTransitionDescriptor> Entering => _entering;
	public IReadOnlyCollection<VoxelLodTransitionDescriptor> Leaving => _leaving;
	public IReadOnlyCollection<VoxelLodTransitionDescriptor> Published => _published;

	public void Update( IEnumerable<VoxelLodTransitionDescriptor> transitions )
	{
		if ( transitions is null ) throw new System.ArgumentNullException( nameof( transitions ) );
		_entering.Clear();
		_leaving.Clear();
		var next = new HashSet<VoxelLodTransitionDescriptor>( transitions );
		foreach ( var current in _desired ) if ( !next.Contains( current ) ) _leaving.Add( current );
		foreach ( var candidate in next ) if ( !_desired.Contains( candidate ) ) _entering.Add( candidate );
		_desired.Clear();
		_desired.UnionWith( next );
	}

	/// <summary>
	/// Publishes only the seam set whose regular dependencies are visible in the
	/// same generation. An incomplete replacement leaves the old seam set intact.
	/// </summary>
	public bool TryPublishCoherent( IEnumerable<VoxelVisualBlockKey> visibleRegularKeys, ulong dependencyGeneration = 0 )
	{
		if ( visibleRegularKeys is null ) throw new System.ArgumentNullException( nameof( visibleRegularKeys ) );
		var visible = visibleRegularKeys is HashSet<VoxelVisualBlockKey> set
			? set
			: new HashSet<VoxelVisualBlockKey>( visibleRegularKeys );
		if ( _hasPublishedGeneration && _publishedDependencyGeneration == dependencyGeneration && _published.SetEquals( _desired ) && DependenciesVisible( visible ) ) return true;
		if ( !DependenciesVisible( visible ) ) return false;
		_published.Clear();
		_published.UnionWith( _desired );
		_publishedDependencyGeneration = dependencyGeneration;
		_hasPublishedGeneration = true;
		return true;
	}

	private bool DependenciesVisible( HashSet<VoxelVisualBlockKey> visible )
	{
		foreach ( var transition in _desired )
		{
			if ( !visible.Contains( transition.Fine ) || !visible.Contains( transition.Coarse ) ) return false;
		}
		return true;
	}

	public bool IsCoherent => _published.SetEquals( _desired );
}
