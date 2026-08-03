using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Governance;

public sealed class EfAlertRuleService : IAlertRuleService
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfAlertRuleService(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<AlertRuleDto>> GetRulesAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.AlertRules.AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new AlertRuleDto(
                x.Id, x.Name, x.EventTypeFilter, x.ThresholdCount, x.WindowMinutes, x.Severity, x.Recipients, x.IsEnabled))
            .ToListAsync(cancellationToken);
    }

    public async Task<AlertRuleDto> CreateRuleAsync(CreateAlertRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = new AlertRule(
            Guid.NewGuid(), request.Name, request.EventTypeFilter, request.ThresholdCount,
            request.WindowMinutes, request.Severity, request.Recipients, isEnabled: true);

        _dbContext.AlertRules.Add(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(rule);
    }

    public async Task<bool> UpdateRuleAsync(Guid id, CreateAlertRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.AlertRules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (rule is null)
        {
            return false;
        }

        rule.Update(request.Name, request.EventTypeFilter, request.ThresholdCount, request.WindowMinutes, request.Severity, request.Recipients);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetRuleEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.AlertRules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (rule is null)
        {
            return false;
        }

        rule.SetEnabled(isEnabled);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<AlertHistoryDto>> GetHistoryAsync(int take, CancellationToken cancellationToken)
    {
        var normalizedTake = take is <= 0 or > 500 ? 200 : take;

        return await _dbContext.AlertHistoryEntries.AsNoTracking()
            .OrderByDescending(x => x.FiredOnUtc)
            .Take(normalizedTake)
            .Select(x => new AlertHistoryDto(
                x.Id, x.AlertRuleId, x.RuleName, x.Severity, x.Summary, x.FiredOnUtc,
                x.Acknowledged, x.AcknowledgedOnUtc, x.AcknowledgedBy))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> AcknowledgeAsync(Guid alertHistoryId, string? acknowledgedBy, CancellationToken cancellationToken)
    {
        var entry = await _dbContext.AlertHistoryEntries.FirstOrDefaultAsync(x => x.Id == alertHistoryId, cancellationToken);
        if (entry is null)
        {
            return false;
        }

        entry.Acknowledge(acknowledgedBy, DateTime.UtcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static AlertRuleDto ToDto(AlertRule rule) => new(
        rule.Id, rule.Name, rule.EventTypeFilter, rule.ThresholdCount, rule.WindowMinutes,
        rule.Severity, rule.Recipients, rule.IsEnabled);
}
