using System.Text.Json;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence.Workflows;

/// <summary>
/// SQL-backed <see cref="IWorkflowNodeResourceHistoryRecorder"/>. Encrypts <see cref="WorkflowNodeRunPayload.PayloadJson"/>
/// at rest with the same <see cref="IPhiFieldEncryptor"/> used for the Configured Pipeline's execution history
/// (<c>EfExecutionResourceHistoryRecorder</c>), since a node's output can carry PHI (raw fetched FHIR resources,
/// mapped field values).
/// </summary>
public sealed class EfWorkflowNodeResourceHistoryRecorder : IWorkflowNodeResourceHistoryRecorder
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IPhiFieldEncryptor _encryptor;

    public EfWorkflowNodeResourceHistoryRecorder(FHIRBridgeDbContext dbContext, IPhiFieldEncryptor encryptor)
    {
        _dbContext = dbContext;
        _encryptor = encryptor;
    }

    public async Task RecordNodeOutputAsync(
        Guid workflowRunId,
        Guid workflowNodeRunId,
        string nodeType,
        string contract,
        object? payload,
        CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(payload);
        var itemCount = payload is System.Collections.ICollection collection ? collection.Count : (int?)null;

        var record = new WorkflowNodeRunPayload(
            Guid.NewGuid(),
            workflowRunId,
            workflowNodeRunId,
            nodeType,
            contract,
            _encryptor.Encrypt(payloadJson),
            itemCount,
            DateTimeOffset.UtcNow);

        await _dbContext.WorkflowNodeRunPayloads.AddAsync(record, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<WorkflowPagedResult<WorkflowNodeRunPayloadDto>> GetPagedAsync(
        Guid workflowRunId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.WorkflowNodeRunPayloads
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId);

        var totalCount = await query.CountAsync(cancellationToken);

        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);

        var records = await query
            .OrderBy(x => x.RecordedAtUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var items = records.Select(record => new WorkflowNodeRunPayloadDto(
            record.Id,
            record.WorkflowNodeRunId,
            record.NodeType,
            record.Contract,
            _encryptor.Decrypt(record.PayloadJson),
            record.ItemCount,
            record.RecordedAtUtc)).ToList();

        return new WorkflowPagedResult<WorkflowNodeRunPayloadDto>(items, totalCount, page, take);
    }

    public async Task<WorkflowPagedResult<WorkflowNodeRunHistoryDto>> GetNodeRunHistoryPagedAsync(
        Guid workflowRunId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var nodeRunsQuery = _dbContext.WorkflowNodeRuns
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId);

        var totalCount = await nodeRunsQuery.CountAsync(cancellationToken);

        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);

        var nodeRuns = await nodeRunsQuery
            .OrderBy(x => x.Rank).ThenBy(x => x.SubRank).ThenBy(x => x.StartedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var nodeRunIds = nodeRuns.Select(x => x.Id).ToList();
        var payloadsByNodeRunId = await _dbContext.WorkflowNodeRunPayloads
            .AsNoTracking()
            .Where(p => nodeRunIds.Contains(p.WorkflowNodeRunId))
            .ToListAsync(cancellationToken);

        // A node run can, in principle, have recorded more than one payload — take the earliest, matching what
        // GetPagedAsync would surface first for the same node run.
        var payloadByNodeRunId = payloadsByNodeRunId
            .GroupBy(p => p.WorkflowNodeRunId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.RecordedAtUtc).First());

        var items = nodeRuns.Select(nodeRun =>
        {
            payloadByNodeRunId.TryGetValue(nodeRun.Id, out var payload);
            return new WorkflowNodeRunHistoryDto(
                nodeRun.Id,
                nodeRun.NodeType,
                nodeRun.Rank,
                nodeRun.SubRank,
                nodeRun.Status.ToString(),
                nodeRun.ErrorMessage,
                nodeRun.StartedAt,
                nodeRun.CompletedAt,
                payload?.Contract,
                payload is null ? null : _encryptor.Decrypt(payload.PayloadJson),
                payload?.ItemCount);
        }).ToList();

        return new WorkflowPagedResult<WorkflowNodeRunHistoryDto>(items, totalCount, page, take);
    }

    public async Task<WorkflowPagedResult<FieldLineageChainDto>> GetFieldLineagePagedAsync(
        Guid workflowRunId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var entries = await _dbContext.FieldLineageEntries
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId)
            .ToListAsync(cancellationToken);

        // Grouped in-memory (not via EF GroupBy translation) so a field's hop chain — usually a handful of
        // rows — is assembled once per chain rather than split across whatever page boundary the raw rows
        // happened to land on.
        var chains = entries
            .GroupBy(x => (x.ResourceId, x.ResourceType, x.DestinationField, x.SourceField))
            .OrderBy(g => g.Key.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.DestinationField, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FieldLineageChainDto(
                g.Key.ResourceType,
                g.Key.ResourceId,
                g.Key.DestinationField,
                g.Key.SourceField,
                g.OrderBy(x => x.NodeOrder)
                    .Select(x => new FieldLineageHopDto(
                        x.NodeOrder,
                        x.NodeType,
                        x.ConfigJson,
                        x.SourceValueJson,
                        x.DestinationValueJson,
                        x.Success,
                        x.ErrorMessage,
                        x.DurationMs))
                    .ToArray()))
            .ToList();

        var totalCount = chains.Count;
        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);
        var items = chains.Skip(skip).Take(take).ToList();

        return new WorkflowPagedResult<FieldLineageChainDto>(items, totalCount, page, take);
    }
}
