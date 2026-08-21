using System.Net.Http.Headers;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Destinations.Auth;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Authenticated reachability check for <see cref="DestinationType.FhirRepository"/> destinations — the generic
/// <c>TargetReachabilityDestinationHealthCheckProvider</c> (anonymous HEAD/directory check) can't be reused here
/// since a real FHIR-repository (e.g. Aidbox) instance typically requires an authenticated call. Does
/// <c>GET {baseUrl}/metadata</c> (a real FHIR CapabilityStatement check, more meaningful than a bare HEAD) with the
/// same optional auth header the writer attaches, so behavior for an unauthenticated destination (today's default)
/// is an anonymous GET. <see cref="DestinationType.FhirRepository"/> previously had no registered health-check
/// provider at all — this adds the first one.
/// </summary>
public sealed class FhirRepositoryHealthCheckProvider : IDestinationHealthCheckProvider
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFhirDestinationTokenProvider _tokenProvider;

    public FhirRepositoryHealthCheckProvider(
        ISecretProvider secretProvider,
        IHttpClientFactory httpClientFactory,
        IFhirDestinationTokenProvider tokenProvider)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
    }

    public DestinationType DestinationType => DestinationType.FhirRepository;

    public async Task<DestinationHealthCheckResult> CheckAsync(DestinationConfiguration destination, CancellationToken cancellationToken)
    {
        string baseUrl;
        AuthenticationHeaderValue? authHeader;
        try
        {
            var authType = ConnectionMetadataReader.GetString(destination.ConnectionMetadataJson, "dest_fhirAuthType") ?? "none";
            if (string.Equals(authType, "none", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = (destination.Target ?? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken)).TrimEnd('/');
                authHeader = null;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(destination.Target))
                {
                    return new DestinationHealthCheckResult(false, "Target (FHIR base URL) is required when dest_fhirAuthType is not 'none'.");
                }

                baseUrl = destination.Target.TrimEnd('/');
                authHeader = await FhirRepositoryAuthResolver.ResolveAsync(
                    destination.ConnectionMetadataJson, destination.SecretReference, _secretProvider, _tokenProvider, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            return new DestinationHealthCheckResult(false, $"Could not resolve target/credentials: {exception.Message}");
        }

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(FhirRepositoryHealthCheckProvider));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/metadata");
            if (authHeader is not null)
            {
                request.Headers.Authorization = authHeader;
            }

            using var response = await client.SendAsync(request, cancellationToken);
            return new DestinationHealthCheckResult((int)response.StatusCode < 400, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception)
        {
            return new DestinationHealthCheckResult(false, exception.Message);
        }
    }
}
