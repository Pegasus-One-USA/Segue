namespace FHIRBridge.Infrastructure.Workflows;

/// <summary>
/// Deterministic name for the launch graph projected from a source connection, so a source maps to a single
/// durable graph that the resolver can find by id alone (before deciding whether to project a fresh one).
/// </summary>
public static class LaunchWorkflowNaming
{
    public static string ForSource(Guid sourceConnectionId) => $"launch:source:{sourceConnectionId:N}";
}
