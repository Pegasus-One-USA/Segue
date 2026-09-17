namespace FHIRBridge.Application.Abstractions.Workflows;

/// <summary>
/// Produces a plain-text dump of every configuration table row that a single workflow depends on, for offline
/// analysis and support triage. Each table section carries the <c>SELECT</c> that produced it plus its rows
/// printed point-wise (one <c>Column : Value</c> line per column), so the file can be read on its own and the
/// query can be re-run by hand against the database.
/// </summary>
public interface IWorkflowConfigurationExporter
{
    /// <summary>
    /// Builds the report for <paramref name="workflowId"/>, or returns <c>null</c> when no such workflow exists.
    /// </summary>
    Task<WorkflowConfigurationExport?> ExportAsync(Guid workflowId, CancellationToken cancellationToken);
}

/// <param name="FileName">Suggested download name, already filesystem-safe.</param>
/// <param name="Content">The full report text.</param>
public sealed record WorkflowConfigurationExport(string FileName, string Content);
