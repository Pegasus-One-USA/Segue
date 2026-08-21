namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>Evaluates every enabled AlertRule against SecurityEvents and fires (records + emails) real matches.</summary>
public interface IAlertEvaluationService
{
    /// <summary>Returns how many alerts fired this pass.</summary>
    Task<int> EvaluateAsync(DateTime utcNow, CancellationToken cancellationToken);
}
