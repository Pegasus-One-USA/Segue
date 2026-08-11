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
        Guid workflowRunId, int page, int pageSize, FieldLineageFilter? filter, CancellationToken cancellationToken)
    {
        var entries = await LoadEntriesAsync(workflowRunId, filter, cancellationToken);

        // Grouped in-memory (not via EF GroupBy translation) so a field's hop chain — usually a handful of
        // rows — is assembled once per chain rather than split across whatever page boundary the raw rows
        // happened to land on.
        var chains = entries
            .GroupBy(x => (x.ResourceId, x.ResourceType, x.DestinationField, x.SourceField))
            .OrderBy(g => g.Key.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.DestinationField, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new FieldLineageChainDto(
                    g.Key.ResourceType,
                    g.Key.ResourceId,
                    g.Key.DestinationField,
                    g.Key.SourceField,
                    g.OrderBy(x => x.NodeOrder)
                        .Select(x => new FieldLineageHopDto(
                            x.NodeOrder,
                            x.NodeType,
                            x.ConfigJson,
                            DecryptOrNull(x.SourceValueJson),
                            DecryptOrNull(x.DestinationValueJson),
                            x.Success,
                            x.ErrorMessage,
                            x.DurationMs,
                            x.ExecutedAtUtc))
                        .ToArray(),
                    first.SourceSystemType,
                    first.SourceConnectionName,
                    first.DestinationTypeName,
                    first.DestinationName);
            })
            .ToList();

        var totalCount = chains.Count;
        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);
        var items = chains.Skip(skip).Take(take).ToList();

        return new WorkflowPagedResult<FieldLineageChainDto>(items, totalCount, page, take);
    }

    public async Task<LineageSummaryDto> GetLineageSummaryAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        var entries = await _dbContext.FieldLineageEntries
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId)
            .Select(x => new { x.ResourceId, x.DestinationField, x.NodeType, x.Success })
            .ToListAsync(cancellationToken);

        if (entries.Count == 0)
        {
            return new LineageSummaryDto(0, 0, 0, 0);
        }

        var resourcesProcessed = entries.Select(x => x.ResourceId).Distinct().Count();
        var fieldsTransformed = entries.Select(x => x.DestinationField).Distinct().Count();
        var nodesExecuted = entries.Select(x => x.NodeType).Distinct().Count();
        var successRate = entries.Count(x => x.Success) / (double)entries.Count;

        return new LineageSummaryDto(resourcesProcessed, fieldsTransformed, nodesExecuted, successRate);
    }

    public async Task<IReadOnlyList<ResourceTypeSummaryDto>> GetLineageResourceTreeAsync(
        Guid workflowRunId, CancellationToken cancellationToken)
    {
        var entries = await _dbContext.FieldLineageEntries
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId)
            .Select(x => new { x.ResourceType, x.ResourceId, x.DestinationField })
            .ToListAsync(cancellationToken);

        return entries
            .GroupBy(x => x.ResourceType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(resourceGroup => new ResourceTypeSummaryDto(
                resourceGroup.Key,
                resourceGroup.Select(x => x.ResourceId).Distinct().Count(),
                resourceGroup
                    .GroupBy(x => x.DestinationField, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(fieldGroup => fieldGroup.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(fieldGroup => new FieldSummaryDto(
                        fieldGroup.Key,
                        fieldGroup.Select(x => x.ResourceId).Distinct().Count()))
                    .ToArray()))
            .ToArray();
    }

    private async Task<List<FieldLineageEntry>> LoadEntriesAsync(
        Guid workflowRunId, FieldLineageFilter? filter, CancellationToken cancellationToken)
    {
        var query = _dbContext.FieldLineageEntries
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId);

        if (filter is not null)
        {
            if (!string.IsNullOrWhiteSpace(filter.ResourceType))
            {
                query = query.Where(x => x.ResourceType == filter.ResourceType);
            }

            if (!string.IsNullOrWhiteSpace(filter.DestinationField))
            {
                query = query.Where(x => x.DestinationField == filter.DestinationField);
            }

            if (!string.IsNullOrWhiteSpace(filter.ResourceId))
            {
                query = query.Where(x => x.ResourceId == filter.ResourceId);
            }

            if (!string.IsNullOrWhiteSpace(filter.NodeType))
            {
                query = query.Where(x => x.NodeType == filter.NodeType);
            }

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                // Values are encrypted at rest and can't be searched in SQL — search only spans the plaintext
                // identifying columns (field/node names, resource id), not SourceValueJson/DestinationValueJson.
                var search = filter.Search;
                query = query.Where(x =>
                    x.DestinationField.Contains(search) ||
                    (x.SourceField != null && x.SourceField.Contains(search)) ||
                    x.NodeType.Contains(search) ||
                    x.ResourceId.Contains(search));
            }
        }

        return await query.ToListAsync(cancellationToken);
    }

    private string? DecryptOrNull(string? ciphertext)
    {
        return ciphertext is null ? null : _encryptor.Decrypt(ciphertext);
    }
}
