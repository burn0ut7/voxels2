internal readonly record struct VoxelGpuCapabilityReport(
	bool Available,
	bool AsyncCountReadback,
	bool MultiDrawIndirect,
	bool IndirectBaseVertex,
	bool IndirectFirstInstance,
	bool ExplicitResourceBarriers,
	bool ConservativeEpochRetirement,
	int IndirectCommandGroupSize,
	string VertexAddressing,
	string RetirementMechanism,
	string Failure );

internal static class VoxelGpuCapabilities
{
	public const int RetirementEpochs = 4;
	public const int DefaultIndirectCommandGroupSize = 16;

	public static VoxelGpuCapabilityReport Detect( bool dedicatedServer = false )
	{
		if ( dedicatedServer || Application.IsDedicatedServer )
		{
			return new VoxelGpuCapabilityReport( false, false, false, false, false, false, false, 0, "none", "none", "dedicated server intentionally owns no GPU terrain resources" );
		}
		// The installed multi-draw path honors BaseVertex, but FirstInstance is not exposed
		// consistently to the generic vertex-input path. Phase 2B therefore emits world-space
		// positions while retaining one bounded multi-draw submission.
		return new VoxelGpuCapabilityReport( true, true, true, true, false, true, true, DefaultIndirectCommandGroupSize,
			"world_space_vertex_fallback", $"frame_epoch_{RetirementEpochs}", string.Empty );
	}
}
