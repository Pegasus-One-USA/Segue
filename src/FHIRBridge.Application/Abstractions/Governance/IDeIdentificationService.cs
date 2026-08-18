namespace FHIRBridge.Application.Abstractions.Governance;

public interface IDeIdentificationService
{
    Task<string> DeIdentifyAsync(
        DeIdentificationRequest request,
        CancellationToken cancellationToken);
}

public sealed record DeIdentificationRequest(
    string ResourceType,
    string? ResourceId,
    string RawJson,
    IReadOnlyCollection<string> AppliedPolicies,
    // Which DeIdentificationProfile's pre-mapping rules to apply. Null means "no profile assigned" — the
    // implementation should return RawJson unchanged rather than falling back to some other rule set, since
    // profiles (not a tenant-wide default) are now the unit of "what redaction applies here."
    Guid? ProfileId = null);
