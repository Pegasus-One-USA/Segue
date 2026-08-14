using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Infrastructure.Pipeline;

/// <summary>
/// Builds a <see cref="FhirSourceConfiguration"/> from a configured source connection (resolving secrets) and
/// delegates to <see cref="IFhirSubscriptionClient"/> to create / delete rest-hook Subscriptions on that server.
/// </summary>
public sealed class FhirSubscriptionManagementService : IFhirSubscriptionManagementService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProvider _secretProvider;
    private readonly IFhirSubscriptionClient _subscriptionClient;

    public FhirSubscriptionManagementService(
        IConfigurationRepository configurationRepository,
        ISecretProvider secretProvider,
        IFhirSubscriptionClient subscriptionClient)
    {
        _configurationRepository = configurationRepository;
        _secretProvider = secretProvider;
        _subscriptionClient = subscriptionClient;
    }

    public async Task<SubscriptionRegistrationResult> RegisterAsync(
        RegisterSubscriptionCommand command,
        CancellationToken cancellationToken)
    {
        var source = await BuildSourceConfigurationAsync(command.SourceConnectionId, cancellationToken);

        var registration = await _subscriptionClient.CreateAsync(
            new FhirSubscriptionRequest(command.Criteria, command.CallbackUrl, command.Reason, Headers: command.Headers),
            source,
            cancellationToken);

        return new SubscriptionRegistrationResult(registration.Id, registration.Status, registration.RawJson);
    }

    public async Task DeleteAsync(
        Guid sourceConnectionId,
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        var source = await BuildSourceConfigurationAsync(sourceConnectionId, cancellationToken);
        await _subscriptionClient.DeleteAsync(subscriptionId, source, cancellationToken);
    }

    // Mirrors ConfiguredPipelineService.BuildSourceConfigurationAsync — resolves the source connection + secrets.
    private async Task<FhirSourceConfiguration> BuildSourceConfigurationAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken)
            ?? throw new NotFoundException("SourceConnection", sourceConnectionId);

        var sourceType = sourceConnection.SourceSystemType switch
        {
            SourceSystemType.Sample => RuntimeSourceType.Sample,
            SourceSystemType.Epic => RuntimeSourceType.Epic,
            SourceSystemType.Cerner => RuntimeSourceType.Cerner,
            SourceSystemType.Allscripts => RuntimeSourceType.Allscripts,
            SourceSystemType.GenericFhir => RuntimeSourceType.GenericFhir,
            SourceSystemType.Athenahealth => RuntimeSourceType.Athenahealth,
            SourceSystemType.Healow => RuntimeSourceType.Healow,
            SourceSystemType.MeditechGreenfield => RuntimeSourceType.MeditechGreenfield,
            _ => throw new NotSupportedException($"Source system '{sourceConnection.SourceSystemType}' is not supported.")
        };

        var privateKeyPem = await ResolveSecretAsync(sourceConnection.Authentication.PrivateKey, cancellationToken);
        var clientSecret = await ResolveSecretAsync(sourceConnection.Authentication.ClientSecret, cancellationToken);

        return new FhirSourceConfiguration(
            sourceType,
            sourceConnection.Name,
            sourceConnection.BaseUrl,
            sourceConnection.Authentication.TokenEndpoint,
            sourceConnection.Authentication.ClientId,
            sourceConnection.Authentication.KeyId,
            privateKeyPem,
            sourceConnection.Authentication.Scopes,
            SourceConnectionId: sourceConnection.Id,
            ClientSecret: clientSecret);
    }

    private async Task<string?> ResolveSecretAsync(SecretReference? reference, CancellationToken cancellationToken)
        => reference is null ? null : await _secretProvider.GetSecretAsync(reference, cancellationToken);
}
