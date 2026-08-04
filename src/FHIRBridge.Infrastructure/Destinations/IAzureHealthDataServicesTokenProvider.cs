namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Acquires an Entra ID access token scoped to an Azure Health Data Services FHIR service, for either the
/// client-credentials (service principal in the client's own tenant) or managed-identity auth mode.
/// </summary>
public interface IAzureHealthDataServicesTokenProvider
{
    Task<string> GetAccessTokenAsync(
        AzureHealthDataServicesConnectionOptions options,
        string? clientSecret,
        CancellationToken cancellationToken);
}
