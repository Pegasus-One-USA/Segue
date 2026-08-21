using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>CRUD for AlertRule plus read/acknowledge for AlertHistoryEntry.</summary>
public interface IAlertRuleService
{
    Task<IReadOnlyList<AlertRuleDto>> GetRulesAsync(CancellationToken cancellationToken);

    Task<AlertRuleDto> CreateRuleAsync(CreateAlertRuleRequest request, CancellationToken cancellationToken);

    Task<bool> UpdateRuleAsync(Guid id, CreateAlertRuleRequest request, CancellationToken cancellationToken);

    Task<bool> SetRuleEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken);

    Task<IReadOnlyList<AlertHistoryDto>> GetHistoryAsync(int take, CancellationToken cancellationToken);

    Task<bool> AcknowledgeAsync(Guid alertHistoryId, string? acknowledgedBy, CancellationToken cancellationToken);
}
