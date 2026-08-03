using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>No database configured — nothing to evaluate.</summary>
public sealed class NullAlertEvaluationService : IAlertEvaluationService
{
    public Task<int> EvaluateAsync(DateTime utcNow, CancellationToken cancellationToken) => Task.FromResult(0);
}
