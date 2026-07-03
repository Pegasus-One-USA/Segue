using System.Security.Cryptography;
using System.Text;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Governance;

public sealed class OperationalAuditLineageTracker : ILineageTracker
{
    private readonly IOperationalAuditService _auditService;
    private readonly ILineageStore? _lineageStore;

    public OperationalAuditLineageTracker(IOperationalAuditService auditService, ILineageStore? lineageStore = null)
    {
        _auditService = auditService;
        _lineageStore = lineageStore;
    }

    public async Task RecordAsync(
        ResourceLineageRecord record,
        CancellationToken cancellationToken)
    {
        // Append to the queryable lineage store so the full chain can be reconstructed later (PHI-free).
        if (_lineageStore is not null)
        {
            await _lineageStore.AppendAsync(record, cancellationToken);
        }

        var resourceReference = string.IsNullOrWhiteSpace(record.SourceResourceId)
            ? "resource-id:unknown"
            : $"resource-id-hash:{ComputeHash(record.SourceResourceId)}";

        await _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                record.PipelineRunId,
                record.RouteId,
                record.SourceConnectionId,
                record.DestinationId,
                record.MappingProfileId,
                record.ResourceType,
                record.Action,
                record.Status,
                $"Resource lineage event recorded for {resourceReference}. PHI omitted.",
                1,
                "system",
                record.PipelineRunId.ToString()),
            cancellationToken);
    }

    private static string ComputeHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return Convert.ToHexString(hash)[..16];
    }
}
