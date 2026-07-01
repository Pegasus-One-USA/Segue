namespace FHIRBridge.Application.Abstractions.Governance;

public interface IDeIdentificationService
{
    Task<string> DeIdentifyAsync(
        DeIdentificationRequest request,
        CancellationToken cancellationToken);
}

public sealed record DeIdentificationRequest(
    Guid TenantId,
    string ResourceType,
    string? ResourceId,
    string RawJson,
    IReadOnlyCollection<string> AppliedPolicies);
