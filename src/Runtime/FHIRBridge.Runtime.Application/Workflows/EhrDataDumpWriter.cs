using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.Application.Workflows;

/// <summary>
/// One record in a dump: the resource's type, its id, and its raw FHIR JSON. Both extraction paths project their
/// own envelope type onto this, so neither has to agree on which <c>ResourceEnvelope</c> it holds.
/// </summary>
public readonly record struct EhrDataDumpRecord(string ResourceType, string? ResourceId, string RawJson);

/// <summary>
/// Writes everything a run fetched from the EHR to <c>&lt;Directory&gt;/&lt;workflowDefinitionId&gt;.txt</c>.
/// </summary>
/// <remarks>
/// Shared by BOTH source-extraction paths, which is the whole point of it being a service rather than a method on
/// the executor: a search-REST run materializes its resources inside SourceNodeExecutor, while a bulk-export run
/// defers, pauses, and materializes them later in RankedWorkflowOrchestrator.ResumeAfterBulkExportAsync — usually
/// in the Worker process, not the API. A dump that lives on only one of those silently produces nothing for every
/// workflow that uses the other.
///
/// The file holds raw, unmasked FHIR resources — exactly what the PHI-masking log enricher keeps out of the logs —
/// so it stays disabled unless <c>EhrDataDump:Enabled</c> is set, which only the Development configs do.
/// </remarks>
public sealed class EhrDataDumpWriter
{
    private readonly EhrDataDumpOptions? _options;
    private readonly ILogger<EhrDataDumpWriter> _logger;

    public EhrDataDumpWriter(
        IOptions<EhrDataDumpOptions>? options = null,
        ILogger<EhrDataDumpWriter>? logger = null)
    {
        _options = options?.Value;
        _logger = logger ?? NullLogger<EhrDataDumpWriter>.Instance;
    }

    public bool IsEnabled => _options is { Enabled: true };

    /// <summary>
    /// Writes the dump for <paramref name="workflowDefinitionId"/>, replacing any previous run's file. Never throws:
    /// a dump is a debugging side effect, and a locked file or read-only directory must not fail a run that
    /// otherwise fetched its data successfully.
    /// </summary>
    public async Task WriteAsync(
        Guid workflowDefinitionId,
        IReadOnlyDictionary<string, string?> header,
        IReadOnlyList<EhrDataDumpRecord> records,
        IReadOnlyList<string>? skippedResourceTypes,
        CancellationToken cancellationToken)
    {
        if (_options is not { Enabled: true } options || string.IsNullOrWhiteSpace(options.Directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(options.Directory);

            // Named for the workflow DEFINITION, not the run: the file is the current picture of what this workflow
            // pulls, rewritten each execution, rather than one file per run accumulating forever.
            var path = Path.Combine(options.Directory, $"{workflowDefinitionId}.txt");

            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"WorkflowDefinitionId : {workflowDefinitionId}");
            foreach (var entry in header)
            {
                builder.AppendLine($"{entry.Key,-21}: {entry.Value}");
            }

            builder.AppendLine($"{"ExtractedAtUtc",-21}: {DateTime.UtcNow:o}");
            builder.AppendLine($"{"ResourceCount",-21}: {records.Count}");

            foreach (var group in records
                .GroupBy(record => record.ResourceType, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"  {group.Key} : {group.Count()}");
            }

            if (skippedResourceTypes is { Count: > 0 })
            {
                builder.AppendLine("SkippedResourceTypes :");
                foreach (var skipped in skippedResourceTypes)
                {
                    builder.AppendLine($"  {skipped}");
                }
            }

            foreach (var record in records)
            {
                builder.AppendLine();
                builder.AppendLine("--------------------------------------------------------------------------------");
                builder.AppendLine($"{record.ResourceType}/{record.ResourceId}");
                builder.AppendLine("--------------------------------------------------------------------------------");
                builder.AppendLine(record.RawJson);
            }

            await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);

            // The path only — never the contents, which are the very PHI the enricher exists to keep out of logs.
            _logger.LogInformation(
                "Wrote {RecordCount} extracted record(s) for workflow {WorkflowDefinitionId} to {DumpPath}.",
                records.Count, workflowDefinitionId, path);
        }
        catch (Exception dumpFailure)
        {
            _logger.LogWarning(
                dumpFailure,
                "Could not write the EHR data dump for workflow {WorkflowDefinitionId}; the run is unaffected.",
                workflowDefinitionId);
        }
    }
}
