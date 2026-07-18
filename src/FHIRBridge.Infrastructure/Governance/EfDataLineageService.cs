using System.Text.Json;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>See IDataLineageService's remarks for the structure-vs-value split this implementation preserves.</summary>
public sealed class EfDataLineageService : IDataLineageService
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IPhiFieldEncryptor _phiFieldEncryptor;

    public EfDataLineageService(FHIRBridgeDbContext dbContext, IPhiFieldEncryptor phiFieldEncryptor)
    {
        _dbContext = dbContext;
        _phiFieldEncryptor = phiFieldEncryptor;
    }

    public async Task<DataLineageDto?> GetLineageAsync(Guid resourceRecordId, CancellationToken cancellationToken)
    {
        // Deliberately projects away FetchedJson/NormalizedJson/MappedValuesJson — this call never decrypts or
        // even reads the PHI-bearing columns, only the safe identifying/stage fields.
        var record = await _dbContext.PipelineRunResourceRecords
            .AsNoTracking()
            .Where(x => x.Id == resourceRecordId)
            .Select(x => new { x.Id, x.RouteExecutionId, x.ResourceType, x.SourceResourceId })
            .FirstOrDefaultAsync(cancellationToken);

        if (record is null)
        {
            return null;
        }

        var execution = await _dbContext.PipelineRunRouteExecutions
            .AsNoTracking()
            .Where(x => x.Id == record.RouteExecutionId)
            .Select(x => new { x.MappingProfileId, x.PipelineRunId })
            .FirstOrDefaultAsync(cancellationToken);

        if (execution is null)
        {
            return null;
        }

        var mapping = await _dbContext.MappingProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == execution.MappingProfileId, cancellationToken);

        var fields = (mapping?.Fields ?? [])
            .Select(f => new DataLineageFieldDto(f.JsonPath, f.NormalizationType, f.TargetField))
            .ToList();

        var exports = await _dbContext.ExportHistory
            .AsNoTracking()
            .Where(x => x.PipelineRunId == execution.PipelineRunId)
            .OrderByDescending(x => x.OccurredOnUtc)
            .Select(x => new DataLineageExportDto(x.DestinationName, x.Format, x.Status, x.OccurredOnUtc))
            .ToListAsync(cancellationToken);

        return new DataLineageDto(
            record.Id,
            record.ResourceType,
            record.SourceResourceId,
            mapping?.Name ?? "Unknown mapping profile",
            fields,
            exports);
    }

    public async Task<LineageFieldValueDto> RevealFieldValueAsync(
        Guid resourceRecordId, string targetField, CancellationToken cancellationToken)
    {
        var record = await _dbContext.PipelineRunResourceRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == resourceRecordId, cancellationToken);

        if (record?.MappedValuesJson is null)
        {
            return new LineageFieldValueDto(targetField, null);
        }

        var decrypted = _phiFieldEncryptor.Decrypt(record.MappedValuesJson);

        using var document = JsonDocument.Parse(decrypted);
        if (!document.RootElement.TryGetProperty(targetField, out var valueElement))
        {
            return new LineageFieldValueDto(targetField, null);
        }

        var value = valueElement.ValueKind == JsonValueKind.String
            ? valueElement.GetString()
            : valueElement.GetRawText();

        return new LineageFieldValueDto(targetField, value);
    }
}
