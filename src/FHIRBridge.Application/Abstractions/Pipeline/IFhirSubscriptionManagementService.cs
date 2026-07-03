namespace FHIRBridge.Application.Abstractions.Pipeline;

/// <summary>
/// Registers / removes rest-hook <c>Subscription</c> resources on a configured source FHIR server so the
/// source pushes changes to FHIRBridge's webhook endpoint. Resolves the source connection + secrets from config.
/// </summary>
public interface IFhirSubscriptionManagementService
{
    Task<SubscriptionRegistrationResult> RegisterAsync(
        RegisterSubscriptionCommand command,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid sourceConnectionId,
        string subscriptionId,
        CancellationToken cancellationToken);
}

public sealed record RegisterSubscriptionCommand(
    Guid SourceConnectionId,
    string Criteria,
    string CallbackUrl,
    IReadOnlyCollection<string>? Headers = null,
    string? Reason = null);

public sealed record SubscriptionRegistrationResult(string Id, string Status, string RawJson);
