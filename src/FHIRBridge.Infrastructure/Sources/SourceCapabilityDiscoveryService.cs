using System.Net.Http.Headers;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Discovers a source endpoint's supported FHIR resource types from its CapabilityStatement (<c>/metadata</c>)
/// and persists a <see cref="SourceCapabilityProfile"/> snapshot. Mirrors <see cref="SourceConnectionTestService"/>
/// for token acquisition and the metadata call; outbound resilience (retry/timeout) is applied globally via the
/// HttpClient defaults configured in <c>AddFHIRBridgeInfrastructure</c>.
/// </summary>
public sealed class SourceCapabilityDiscoveryService : ISourceCapabilityDiscoveryService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISourceCapabilityRepository _capabilityRepository;
    private readonly ISecretProvider _secretProvider;
    private readonly IFhirAccessTokenProvider _accessTokenProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SourceCapabilityDiscoveryService> _logger;

    public SourceCapabilityDiscoveryService(
        IConfigurationRepository configurationRepository,
        ISourceCapabilityRepository capabilityRepository,
        ISecretProvider secretProvider,
        IFhirAccessTokenProvider accessTokenProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<SourceCapabilityDiscoveryService> logger)
    {
        _configurationRepository = configurationRepository;
        _capabilityRepository = capabilityRepository;
        _secretProvider = secretProvider;
        _accessTokenProvider = accessTokenProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<SourceCapabilityProfileDto> DiscoverAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken)
            ?? throw new NotFoundException("SourceConnection", sourceConnectionId);

        if (sourceConnection.SourceSystemType != SourceSystemType.Epic)
        {
            throw new InvalidOperationException(
                $"Capability discovery is not implemented for {sourceConnection.SourceSystemType}.");
        }

        var configuration = await BuildEpicSourceConfigurationAsync(sourceConnection, cancellationToken);
        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(configuration, cancellationToken);
        var metadataUrl = $"{sourceConnection.BaseUrl.TrimEnd('/')}/metadata";

        var httpClient = _httpClientFactory.CreateClient(nameof(SourceCapabilityDiscoveryService));
        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Epic metadata endpoint returned {(int)response.StatusCode}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var (fhirVersion, resources) = ParseCapabilityStatement(json);

        var profile = new SourceCapabilityProfile(
            sourceConnectionId,
            fhirVersion,
            resources,
            sourceConnection.Authentication.Scopes,
            json,
            DateTime.UtcNow);

        await _capabilityRepository.UpsertAsync(profile, cancellationToken);

        _logger.LogInformation(
            "Discovered {ResourceCount} supported FHIR resource types for source {SourceConnectionId}.",
            resources.Count,
            sourceConnectionId);

        return ToDto(profile);
    }

    public async Task<SourceCapabilityProfileDto?> GetAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var profile = await _capabilityRepository.GetBySourceConnectionIdAsync(
            sourceConnectionId,
            cancellationToken);

        return profile is null ? null : ToDto(profile);
    }

    public async Task<SmartConfigurationDto> DiscoverSmartConfigurationAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken)
            ?? throw new NotFoundException("SourceConnection", sourceConnectionId);

        // The SMART discovery document is public (no bearer token) per the SMART App Launch spec.
        var smartConfigUrl = $"{sourceConnection.BaseUrl.TrimEnd('/')}/.well-known/smart-configuration";
        var httpClient = _httpClientFactory.CreateClient(nameof(SourceCapabilityDiscoveryService));
        using var request = new HttpRequestMessage(HttpMethod.Get, smartConfigUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"SMART configuration endpoint returned {(int)response.StatusCode} for source {sourceConnectionId}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var configuration = ParseSmartConfiguration(json);

        _logger.LogInformation(
            "Discovered SMART configuration for source {SourceConnectionId}; authorization endpoint {HasAuthorize}, token endpoint {HasToken}.",
            sourceConnectionId,
            configuration.AuthorizationEndpoint is not null,
            configuration.TokenEndpoint is not null);

        return configuration;
    }

    /// <summary>
    /// Parses the standard SMART discovery fields from the <c>.well-known/smart-configuration</c> document. Uses raw
    /// JSON to stay consistent with the rest of the runtime (no Firely SDK dependency).
    /// </summary>
    internal static SmartConfigurationDto ParseSmartConfiguration(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? GetString(string name) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;

        IReadOnlyList<string> GetStringArray(string name)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToList();
        }

        return new SmartConfigurationDto(
            GetString("authorization_endpoint"),
            GetString("token_endpoint"),
            GetString("introspection_endpoint"),
            GetString("revocation_endpoint"),
            GetString("registration_endpoint"),
            GetStringArray("scopes_supported"),
            GetStringArray("grant_types_supported"),
            GetStringArray("response_types_supported"),
            GetStringArray("code_challenge_methods_supported"),
            GetStringArray("capabilities"),
            GetStringArray("token_endpoint_auth_methods_supported"));
    }

    private async Task<FhirSourceConfiguration> BuildEpicSourceConfigurationAsync(
        SourceConnection sourceConnection,
        CancellationToken cancellationToken)
    {
        if (sourceConnection.Authentication.PrivateKey is null)
        {
            throw new InvalidOperationException("Epic private key secret reference is missing.");
        }

        var privateKeyPem = await _secretProvider.GetSecretAsync(
            sourceConnection.Authentication.PrivateKey,
            cancellationToken);

        return new FhirSourceConfiguration(
            RuntimeSourceType.Epic,
            sourceConnection.Name,
            sourceConnection.BaseUrl,
            sourceConnection.Authentication.TokenEndpoint,
            sourceConnection.Authentication.ClientId,
            sourceConnection.Authentication.KeyId,
            privateKeyPem,
            sourceConnection.Authentication.Scopes,
            1,
            1,
            sourceConnection.Id);
    }

    /// <summary>
    /// Extracts the FHIR version and per-resource interaction codes from a CapabilityStatement document
    /// (<c>rest[].resource[].{type,interaction[].code}</c>). Parsed with raw JSON to match the rest of the
    /// runtime, which does not take a dependency on the Firely SDK.
    /// </summary>
    internal static (string FhirVersion, IReadOnlyList<CapabilityResource> Resources) ParseCapabilityStatement(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var fhirVersion = root.TryGetProperty("fhirVersion", out var versionElement)
            ? versionElement.GetString() ?? "4.0.1"
            : "4.0.1";

        var resources = new List<CapabilityResource>();
        if (root.TryGetProperty("rest", out var restElement) && restElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var rest in restElement.EnumerateArray())
            {
                if (!rest.TryGetProperty("resource", out var resourceArray) ||
                    resourceArray.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var resource in resourceArray.EnumerateArray())
                {
                    var type = resource.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(type))
                    {
                        continue;
                    }

                    var interactions = new List<string>();
                    if (resource.TryGetProperty("interaction", out var interactionArray) &&
                        interactionArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var interaction in interactionArray.EnumerateArray())
                        {
                            if (interaction.TryGetProperty("code", out var codeElement) &&
                                codeElement.GetString() is { Length: > 0 } code)
                            {
                                interactions.Add(code);
                            }
                        }
                    }

                    resources.Add(new CapabilityResource(type, interactions));
                }
            }
        }

        return (fhirVersion, resources);
    }

    private static SourceCapabilityProfileDto ToDto(SourceCapabilityProfile profile)
    {
        return new SourceCapabilityProfileDto(
            profile.SourceConnectionId,
            profile.FhirVersion,
            profile.DiscoveredOnUtc,
            profile.ConfiguredScopes,
            profile.Resources
                .Select(resource => new CapabilityResourceDto(resource.ResourceType, resource.Interactions))
                .ToList());
    }
}
