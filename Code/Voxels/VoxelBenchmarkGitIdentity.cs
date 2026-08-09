public readonly record struct VoxelBenchmarkGitIdentity( string Revision, bool WorkingTreeDirty, string CapturedUtc )
{
	internal const string DataPath = "voxel-terrain-benchmarks/git-identity.json";
	public static event System.Action RefreshRequested;

	internal static VoxelBenchmarkGitIdentity Load()
	{
		RefreshRequested?.Invoke();
		if ( !FileSystem.Data.FileExists( DataPath ) )
		{
			return new VoxelBenchmarkGitIdentity( "unknown", true, string.Empty );
		}

		try
		{
			using var document = System.Text.Json.JsonDocument.Parse( FileSystem.Data.ReadAllText( DataPath ) );
			var root = document.RootElement;
			var revision = root.TryGetProperty( "revision", out var revisionValue ) ? revisionValue.GetString() : null;
			var dirty = !root.TryGetProperty( "working_tree_dirty", out var dirtyValue ) || dirtyValue.GetBoolean();
			var capturedUtc = root.TryGetProperty( "captured_utc", out var capturedValue ) ? capturedValue.GetString() : string.Empty;
			if ( string.IsNullOrWhiteSpace( revision ) ) return new VoxelBenchmarkGitIdentity( "unknown", true, capturedUtc );
			return new VoxelBenchmarkGitIdentity( revision, dirty, capturedUtc );
		}
		catch ( System.Exception exception )
		{
			Log.Warning( $"Voxel benchmark Git identity could not be read: {exception.Message}" );
			return new VoxelBenchmarkGitIdentity( "unknown", true, string.Empty );
		}
	}

}
