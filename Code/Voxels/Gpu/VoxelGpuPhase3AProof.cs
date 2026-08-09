internal readonly record struct VoxelGpuPhase3AProofResult( bool Passed, string Test, string Failure );

internal static class VoxelGpuPhase3AProof
{
	public static VoxelGpuPhase3AProofResult ValidateProductionRender( string test, VoxelGpuTerrainDiagnostics diagnostics )
	{
		var failure = diagnostics.Failure;
		if ( string.IsNullOrEmpty( failure ) && !diagnostics.Available ) failure = "persistent GPU backend unavailable";
		if ( string.IsNullOrEmpty( failure ) && !diagnostics.ProductionLighting ) failure = "production standard lighting path is not active";
		if ( string.IsNullOrEmpty( failure ) && !diagnostics.DepthPrepass ) failure = "terrain material does not expose a depth pass";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.DepthPrepassCommandLists == 0 ) failure = "no depth-prepass command list was attached";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.OpaqueCommandLists == 0 ) failure = "no opaque command list was attached";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.VisibleDrawCommands == 0 ) failure = "production renderer has no visible indexed commands";
		if ( string.IsNullOrEmpty( failure ) && diagnostics.GeometryReadbackBytes != 0 ) failure = $"read back {diagnostics.GeometryReadbackBytes} geometry bytes";
		return new VoxelGpuPhase3AProofResult( string.IsNullOrEmpty( failure ), test, failure ?? string.Empty );
	}
}
