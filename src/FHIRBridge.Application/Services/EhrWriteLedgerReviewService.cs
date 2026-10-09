using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Application.Services;

/// <summary>
/// The review list for EHR write-back: writes whose outcome is unknown (timeout, reset, 5xx), that the EHR refused,
/// or that were abandoned mid-send. None of them is retried automatically, because the EHR files a replayed create a
/// second time, so a person checks the chart and either records the EHR's id ("it is there") or releases the record
/// for one more send ("it is not there").
/// </summary>
public interface IEhrWriteLedgerReviewService
{
    Task<PagedResult<EhrWriteLedgerReviewItemDto>> ListAsync(string? resourceType, int page, int pageSize, CancellationToken cancellationToken);

    Task<EhrWriteLedgerReviewItemDto> ResolveAsWrittenAsync(Guid id, string targetResourceId, CancellationToken cancellationToken);

    Task<EhrWriteLedgerReviewItemDto> ReleaseForResendAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class EhrWriteLedgerReviewService : IEhrWriteLedgerReviewService
{
    /// <summary>Shown instead of Pending for a row the review list picked up because it went stale.</summary>
    public const string AbandonedState = "Abandoned";

    private readonly IEhrWriteLedgerRepository _ledger;
    private readonly IConfigurationRepository _configuration;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<EhrWriteLedgerReviewService> _logger;

    public EhrWriteLedgerReviewService(
        IEhrWriteLedgerRepository ledger,
        IConfigurationRepository configuration,
        ICurrentUserService currentUser,
        ILogger<EhrWriteLedgerReviewService> logger)
    {
        _ledger = ledger;
        _configuration = configuration;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<PagedResult<EhrWriteLedgerReviewItemDto>> ListAsync(
        string? resourceType, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var (items, total) = await _ledger.ListNeedingReviewAsync(
            string.IsNullOrWhiteSpace(resourceType) ? null : resourceType.Trim(),
            DateTime.UtcNow,
            (page - 1) * pageSize,
            pageSize,
            cancellationToken);

        var connections = new Dictionary<Guid, SourceConnection?>();
        var dtos = new List<EhrWriteLedgerReviewItemDto>(items.Count);
        foreach (var item in items)
        {
            if (!connections.TryGetValue(item.TargetConnectionId, out var connection))
            {
                connection = await _configuration.GetSourceConnectionAsync(item.TargetConnectionId, cancellationToken);
                connections[item.TargetConnectionId] = connection;
            }

            dtos.Add(ToDto(item, connection));
        }

        return new PagedResult<EhrWriteLedgerReviewItemDto>(dtos, total, page, pageSize);
    }

    public Task<EhrWriteLedgerReviewItemDto> ResolveAsWrittenAsync(Guid id, string targetResourceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetResourceId) || targetResourceId.Trim().Length > 200)
        {
            throw new BusinessRuleException("Enter the id the EHR gave the record (at most 200 characters).");
        }

        return ResolveAsync(id, (entry, reviewer, now) => entry.ResolveAsWritten(targetResourceId, reviewer, now), "written", cancellationToken);
    }

    public Task<EhrWriteLedgerReviewItemDto> ReleaseForResendAsync(Guid id, CancellationToken cancellationToken) =>
        ResolveAsync(id, (entry, reviewer, now) => entry.ReleaseForResend(reviewer, now), "released for resend", cancellationToken);

    private async Task<EhrWriteLedgerReviewItemDto> ResolveAsync(
        Guid id,
        Action<EhrWriteLedgerEntry, string, DateTime> resolve,
        string outcome,
        CancellationToken cancellationToken)
    {
        var entry = await _ledger.GetAsync(id, cancellationToken)
            ?? throw new NotFoundException("EHR write ledger entry", id);

        var user = _currentUser.CurrentUser;
        var reviewer = user.Email ?? user.ExternalUserId ?? "unknown";
        try
        {
            resolve(entry, reviewer, DateTime.UtcNow);
        }
        catch (InvalidOperationException exception)
        {
            throw new BusinessRuleException(exception.Message);
        }

        // State is a concurrency token: a run that claimed the row meanwhile makes this save fail rather than be
        // silently overwritten.
        if (!await _ledger.TryClaimAsync(entry, cancellationToken))
        {
            throw new BusinessRuleException("This write changed while you were reviewing it. Reload the list and try again.");
        }

        _logger.LogInformation(
            "EHR write ledger entry {LedgerEntryId} ({ResourceType}) marked {Outcome} by a reviewer.",
            entry.Id, entry.ResourceType, outcome);

        var connection = await _configuration.GetSourceConnectionAsync(entry.TargetConnectionId, cancellationToken);
        return ToDto(entry, connection);
    }

    private static EhrWriteLedgerReviewItemDto ToDto(EhrWriteLedgerEntry entry, SourceConnection? connection)
    {
        var state = entry.State == EhrWriteLedgerState.Pending ? AbandonedState : entry.State;
        return new EhrWriteLedgerReviewItemDto(
            entry.Id,
            entry.TargetConnectionId,
            connection?.Name,
            connection?.SourceSystemType.ToString(),
            entry.ResourceType,
            state,
            entry.TargetResourceId,
            entry.HttpStatus,
            entry.OutcomeCodes,
            entry.AttemptCount,
            entry.DestinationId,
            entry.WorkflowRunId,
            entry.CreatedOnUtc,
            entry.UpdatedOnUtc,
            entry.ReviewedBy,
            entry.ReviewedOnUtc);
    }
}
