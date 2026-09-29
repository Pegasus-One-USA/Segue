using System.Net;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Fabric;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Azure.Cosmos;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Tests a Cosmos DB in Fabric connection before anything is saved, and lists the database's containers.
///
/// <para><b>Why this exists.</b> Cosmos is the one Fabric surface whose target must already exist by default —
/// a container's partition key is fixed at creation, so the writer refuses to invent one. Without this test the
/// first sign of a wrong endpoint, a missing data-plane role, or simply a container nobody created was a FAILED
/// PIPELINE RUN, with the reason in the worker log. Everything checked here is checked again by the writer;
/// the point is only to check it somewhere cheap.</para>
///
/// <para><b>What a pass proves.</b> Reading the database exercises the endpoint, the Entra credential and the
/// identity's data-plane role — the three things that fail independently and produce near-identical errors at
/// write time. It does NOT prove the identity may CREATE a container, which needs a strictly higher role; a
/// destination set to create missing containers can therefore pass this test and still fail its first run.</para>
/// </summary>
public sealed class CosmosDbFabricDestinationConnectionTestService
    : ICosmosDbFabricDestinationConnectionTestService
{
    private readonly ICosmosDbFabricClientFactory _clientFactory;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProvider _secretProvider;

    public CosmosDbFabricDestinationConnectionTestService(
        ICosmosDbFabricClientFactory clientFactory,
        IConfigurationRepository configurationRepository,
        ISecretProvider secretProvider)
    {
        _clientFactory = clientFactory;
        _configurationRepository = configurationRepository;
        _secretProvider = secretProvider;
    }

    public async Task<CosmosDbFabricConnectionTestResultDto> TestConnectionAsync(
        CosmosDbFabricConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        // Re-testing an already-saved destination: the form never re-displays a stored client secret, so a
        // blank one here means "use what is already saved".
        if (string.IsNullOrWhiteSpace(request.Secret) && request.DestinationId is { } destinationId)
        {
            request = request with { Secret = await ResolveStoredSecretAsync(destinationId, cancellationToken) };
        }

        CosmosDbFabricDestinationSettings settings;
        try
        {
            // Parsed through the writer's own parser rather than validated here, so the test cannot pass
            // against an address the writer would then reject. Its exceptions are already user-facing prose.
            settings = CosmosDbFabricDestinationSettings.Parse(BuildAdHocDestination(request));
        }
        catch (Exception exception)
        {
            return new CosmosDbFabricConnectionTestResultDto(false, exception.Message);
        }

        try
        {
            var client = _clientFactory.GetClientAdHoc(settings, request.Secret);
            var database = client.GetDatabase(settings.Database);

            // The first real round trip. CosmosClient connects lazily, so constructing one against an
            // unreachable endpoint or an unusable identity succeeds silently — nothing before this line has
            // proven anything.
            await database.ReadAsync(cancellationToken: cancellationToken);

            var containers = new List<string>();
            using var iterator = database.GetContainerQueryIterator<ContainerProperties>();
            while (iterator.HasMoreResults)
            {
                foreach (var container in await iterator.ReadNextAsync(cancellationToken))
                {
                    containers.Add(container.Id);
                }
            }

            containers.Sort(StringComparer.OrdinalIgnoreCase);

            // Connected with no containers is a legitimate, common answer — a new Fabric Cosmos database has
            // none. The form distinguishes it from a failure, because the next step is different.
            return new CosmosDbFabricConnectionTestResultDto(true, null, containers);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new CosmosDbFabricConnectionTestResultDto(
                false,
                $"Connected to the Cosmos DB endpoint, but database '{settings.Database}' was not found. Check "
                    + "the database name against the item in your Fabric workspace.");
        }
        catch (CosmosException exception) when (
            exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // Worth spelling out: this is the failure users spend longest on, because the identity, the role
            // and the tenant setting are three separate things that all produce a 403.
            return new CosmosDbFabricConnectionTestResultDto(
                false,
                "Reached Cosmos DB, but the configured identity was refused. Cosmos DB in Fabric authorizes "
                    + "through built-in data-plane roles, so the identity needs at least Read on the database "
                    + "item in Fabric. For a service principal, also confirm the tenant setting 'Service "
                    + "principals can use Fabric APIs' is enabled — without it a correctly-permissioned "
                    + "principal is still refused, and nothing in the underlying error says so.");
        }
        catch (Exception exception)
        {
            // Connection/auth failures are an expected UI outcome, not a server error — same contract the
            // Mongo and Fabric probes follow.
            return new CosmosDbFabricConnectionTestResultDto(false, exception.Message);
        }
    }

    private async Task<string?> ResolveStoredSecretAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        var destination = await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
        if (destination is null)
        {
            return null;
        }

        try
        {
            return await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        }
        catch (SecretNotConfiguredException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rebuilds the flat <c>dest_*</c> metadata shape <see cref="CosmosDbFabricDestinationSettings.Parse"/>
    /// reads, from the discrete probe fields. Keeping ONE parser is the point: a probe cannot succeed against
    /// a configuration the writer would reject.
    /// </summary>
    private static DestinationConfiguration BuildAdHocDestination(CosmosDbFabricConnectionTestRequest request)
    {
        var metadata = new Dictionary<string, string?>
        {
            ["dest_cosmosFabricEndpoint"] = request.Endpoint,
            ["dest_cosmosFabricDatabase"] = request.Database,
            ["dest_cosmosFabricAuthMode"] = request.AuthMode,
            ["dest_cosmosFabricTenantId"] = request.TenantId,
            ["dest_cosmosFabricClientId"] = request.ClientId,
            ["dest_cosmosFabricManagedIdentityClientId"] = request.ManagedIdentityClientId,
            ["dest_cosmosFabricAuthorityHost"] = request.AuthorityHost,
            // Deliberately NOT carried: this test only reads. Sending the creation mode would make the probe
            // validate a rule (the configured-key mode needs a partition key) that has nothing to do with
            // connectivity, and would fail the test for a destination whose connection is perfectly fine.
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            metadata.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value!));

        // A placeholder secret reference: this destination exists only to be handed to Parse, and the ad-hoc
        // client path never dereferences it — the typed secret goes to GetClientAdHoc directly.
        return new DestinationConfiguration(
            "Cosmos DB probe",
            DestinationType.CosmosDbFabric,
            new SecretReference("unsaved", "unsaved"),
            null,
            json);
    }
}
