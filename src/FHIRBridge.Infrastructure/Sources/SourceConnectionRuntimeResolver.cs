using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Loads a SourceConnection by id and maps it to the runtime <see cref="FhirSourceConfiguration"/> (resolving any
/// client-secret / private-key references), mirroring the route pipeline's source-config builder so a graph run and
/// a route run behave identically. Returns null when the connection does not exist so the caller can fall back.
/// </summary>
public sealed class SourceConnectionRuntimeResolver : ISourceConnectionRuntimeResolver
{
    private readonly IConfigurationRepository _repository;
    private readonly ISecretProvider _secretProvider;

    public SourceConnectionRuntimeResolver(
        IConfigurationRepository repository,
        ISecretProvider secretProvider)
    {
        _repository = repository;
        _secretProvider = secretProvider;
    }

    public async Task<FhirSourceConfiguration?> ResolveAsync(
        Guid sourceConnectionId,
        string? searchParameters,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _repository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        if (sourceConnection is null)
        {
            return null;
        }

        var sourceType = sourceConnection.SourceSystemType switch
        {
            SourceSystemType.Sample => RuntimeSourceType.Sample,
            SourceSystemType.Epic => RuntimeSourceType.Epic,
            SourceSystemType.Cerner => RuntimeSourceType.Cerner,
            SourceSystemType.Allscripts => RuntimeSourceType.Allscripts,
            SourceSystemType.GenericFhir => RuntimeSourceType.GenericFhir,
            SourceSystemType.Athenahealth => RuntimeSourceType.GenericFhir,
            SourceSystemType.Healow => RuntimeSourceType.Healow,
            SourceSystemType.MeditechGreenfield => RuntimeSourceType.MeditechGreenfield,
            _ => throw new NotSupportedException($"Source system '{sourceConnection.SourceSystemType}' is not supported by the workflow engine.")
        };

        string? privateKeyPem = null;
        if (sourceConnection.Authentication.PrivateKey is not null)
        {
            privateKeyPem = await _secretProvider.GetSecretAsync(sourceConnection.Authentication.PrivateKey, cancellationToken);
        }

        string? clientSecret = null;
        if (sourceConnection.Authentication.ClientSecret is not null)
        {
            clientSecret = await _secretProvider.GetSecretAsync(sourceConnection.Authentication.ClientSecret, cancellationToken);
        }

        return new FhirSourceConfiguration(
            sourceType,
            sourceConnection.Name,
            sourceConnection.BaseUrl,
            sourceConnection.Authentication.TokenEndpoint,
            sourceConnection.Authentication.ClientId,
            sourceConnection.Authentication.KeyId,
            privateKeyPem,
            sourceConnection.Authentication.Scopes,
            100,
            5,
            sourceConnection.Id,
            searchParameters,
            clientSecret,
            ApplicationType: sourceConnection.ApplicationType);
    }
}
