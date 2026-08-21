using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>No database configured — alert rules have nowhere to persist.</summary>
public sealed class InMemoryAlertRuleService : IAlertRuleService
{
    public Task<IReadOnlyList<AlertRuleDto>> GetRulesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AlertRuleDto>>([]);

    public Task<AlertRuleDto> CreateRuleAsync(CreateAlertRuleRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("No database is configured for this environment — alert rules cannot be created.");

    public Task<bool> UpdateRuleAsync(Guid id, CreateAlertRuleRequest request, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task<bool> SetRuleEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task<IReadOnlyList<AlertHistoryDto>> GetHistoryAsync(int take, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AlertHistoryDto>>([]);

    public Task<bool> AcknowledgeAsync(Guid alertHistoryId, string? acknowledgedBy, CancellationToken cancellationToken)
        => Task.FromResult(false);
}
