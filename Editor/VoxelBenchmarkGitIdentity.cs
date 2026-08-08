public static class VoxelBenchmarkGitIdentityWriter
{
	private const string DataPath = "voxel-terrain-benchmarks/git-identity.json";
	private static System.DateTime _nextRefreshUtc;
	private static string _lastIdentity;

	[EditorEvent.Hotload]
	public static void OnHotload() => Refresh();

	[EditorEvent.Frame]
	public static void OnFrame()
	{
		if ( System.DateTime.UtcNow < _nextRefreshUtc ) return;
		Refresh();
	}

	private static void Refresh()
	{
		_nextRefreshUtc = System.DateTime.UtcNow.AddSeconds( 2.0 );
		var root = Project.Current?.GetRootPath();
		if ( string.IsNullOrWhiteSpace( root ) ) return;

		var revision = RunGit( root, "rev-parse", "HEAD" ).Trim();
		if ( revision.Length != 40 ) revision = "unknown";
		var dirty = RunGit( root, "status", "--porcelain", "--untracked-files=normal" ).Length > 0;
		var identity = $"{revision}:{dirty}";
		if ( identity == _lastIdentity ) return;
		var capturedUtc = System.DateTime.UtcNow.ToString( "O", System.Globalization.CultureInfo.InvariantCulture );
		var payload = $"{{\"revision\":\"{revision}\",\"working_tree_dirty\":{dirty.ToString().ToLowerInvariant()},\"captured_utc\":\"{capturedUtc}\"}}";
		Sandbox.FileSystem.Data.CreateDirectory( "voxel-terrain-benchmarks" );
		Sandbox.FileSystem.Data.WriteAllText( DataPath, payload );
		_lastIdentity = identity;
	}

	private static string RunGit( string root, params string[] arguments )
	{
		try
		{
			using var process = new System.Diagnostics.Process();
			process.StartInfo.FileName = "git";
			process.StartInfo.WorkingDirectory = root;
			process.StartInfo.UseShellExecute = false;
			process.StartInfo.CreateNoWindow = true;
			process.StartInfo.RedirectStandardOutput = true;
			process.StartInfo.RedirectStandardError = true;
			foreach ( var argument in arguments ) process.StartInfo.ArgumentList.Add( argument );
			process.Start();
			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit( 2000 );
			return process.ExitCode == 0 ? output : string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}
}
