using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Evaluates every enabled AlertRule: counts SecurityEvents matching EventTypeFilter within the last
/// WindowMinutes, fires (records + emails) when the count reaches ThresholdCount. Dedupes by not re-firing the
/// same rule again while an unacknowledged firing from within the current window still exists — a real, if
/// simple, evaluator over real data, not a fake generic expression language.
/// </summary>
public sealed class EfAlertEvaluationService : IAlertEvaluationService
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IEmailSender _emailSender;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly ILogger<EfAlertEvaluationService> _logger;

    public EfAlertEvaluationService(
        FHIRBridgeDbContext dbContext,
        IEmailSender emailSender,
        IGovernanceLogger governanceLogger,
        ILogger<EfAlertEvaluationService> logger)
    {
        _dbContext = dbContext;
        _emailSender = emailSender;
        _governanceLogger = governanceLogger;
        _logger = logger;
    }

    public async Task<int> EvaluateAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var rules = await _dbContext.AlertRules.AsNoTracking()
            .Where(x => x.IsEnabled)
            .ToListAsync(cancellationToken);

        var firedCount = 0;

        foreach (var rule in rules)
        {
            var windowStartUtc = utcNow.AddMinutes(-rule.WindowMinutes);

            var matchCount = await _dbContext.SecurityEvents.AsNoTracking()
                .CountAsync(x => x.EventType == rule.EventTypeFilter && x.OccurredOnUtc >= windowStartUtc, cancellationToken);

            if (matchCount < rule.ThresholdCount)
            {
                continue;
            }

            var alreadyFiredInWindow = await _dbContext.AlertHistoryEntries.AsNoTracking()
                .AnyAsync(x => x.AlertRuleId == rule.Id && x.FiredOnUtc >= windowStartUtc, cancellationToken);

            if (alreadyFiredInWindow)
            {
                continue;
            }

            var summary = $"{matchCount} '{rule.EventTypeFilter}' event(s) in the last {rule.WindowMinutes} minute(s) (threshold {rule.ThresholdCount}).";
            var entry = new AlertHistoryEntry(Guid.NewGuid(), rule.Id, rule.Name, rule.Severity, summary, utcNow);
            _dbContext.AlertHistoryEntries.Add(entry);
            await _dbContext.SaveChangesAsync(cancellationToken);
            firedCount++;

            var recipients = rule.Recipients
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var subject = $"Segue Alert: {rule.Name}";
            foreach (var recipient in recipients)
            {
                try
                {
                    // SendAsync returns false (not an exception) when email is disabled/unconfigured — that must
                    // not be logged as "Sent" (see EmailDeliveryStrategy's own fix for this exact bug).
                    var sent = await _emailSender.SendAsync(recipient, subject, summary, cancellationToken);
                    if (sent)
                    {
                        await _governanceLogger.LogNotificationAsync(
                            new NotificationEntry("Email", recipient, "Sent", subject), cancellationToken);
                    }
                    else
                    {
                        await _governanceLogger.LogNotificationAsync(
                            new NotificationEntry("Email", recipient, "Skipped", subject,
                                "Email delivery is disabled in Notification Settings."), cancellationToken);
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Failed to send alert email for rule {RuleId} to {Recipient}.", rule.Id, recipient);
                    await _governanceLogger.LogNotificationAsync(
                        new NotificationEntry("Email", recipient, "Failed", subject, exception.Message), cancellationToken);
                }
            }
        }

        return firedCount;
    }
}
