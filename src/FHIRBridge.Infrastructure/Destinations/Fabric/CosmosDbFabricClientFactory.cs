using Azure.Core;
using Azure.Identity;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Builds the <see cref="CosmosClient"/> for a Cosmos DB in Fabric destination.
///
/// <para>The seam that lets <see cref="MappedCosmosDbFabricDestinationWriter"/> be unit-tested without a live
/// Fabric tenant — the same role <see cref="IOneLakeClientFactory"/> plays for the OneLake surfaces.</para>
/// </summary>
public interface ICosmosDbFabricClientFactory
{
    Task<CosmosClient> GetClientAsync(
        DestinationConfiguration destination,
        CosmosDbFabricDestinationSettings settings,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds a client for an UNSAVED destination — the wizard's Test Connection button, where there is no
    /// destination row and therefore no Key Vault reference to resolve, so the typed secret is passed straight
    /// through. Mirrors <c>IOneLakeClientFactory.GetWorkspaceAdHocAsync</c>, which exists for the same reason.
    /// </summary>
    CosmosClient GetClientAdHoc(CosmosDbFabricDestinationSettings settings, string? secret);
}

/// <inheritdoc />
internal sealed class CosmosDbFabricClientFactory : ICosmosDbFabricClientFactory
{
    private readonly ISecretProvider _secretProvider;
    private readonly ILogger<CosmosDbFabricClientFactory> _logger;

    public CosmosDbFabricClientFactory(
        ISecretProvider secretProvider, ILogger<CosmosDbFabricClientFactory> logger)
    {
        _secretProvider = secretProvider;
        _logger = logger;
    }

    public async Task<CosmosClient> GetClientAsync(
        DestinationConfiguration destination,
        CosmosDbFabricDestinationSettings settings,
        CancellationToken cancellationToken)
    {
        var secret = settings.RequiresSecret
            ? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken)
            : null;

        _logger.LogDebug(
            "Building Cosmos DB (Fabric) client for destination {DestinationId} ({DestinationName}), auth mode "
                + "{AuthMode}, database {Database}.",
            destination.Id,
            destination.Name,
            settings.AuthMode,
            settings.Database);

        // Gateway mode is REQUIRED, and the SDK does not default to it. Cosmos DB in Fabric supports only
        // Gateway connectivity, while the .NET SDK defaults to Direct — so leaving this unset produces a client
        // that cannot connect at all, with an error about connectivity rather than about the mode. Documented
        // at https://learn.microsoft.com/fabric/database/cosmos-db/how-to-authenticate.
        var options = new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway };

        return new CosmosClient(settings.Endpoint, BuildCredential(settings, secret), options);
    }

    /// <inheritdoc />
    public CosmosClient GetClientAdHoc(CosmosDbFabricDestinationSettings settings, string? secret)
        // Same Gateway-mode requirement as the saved path above — an ad-hoc test that connected in Direct mode
        // would be testing something the writer never does.
        => new(
            settings.Endpoint,
            BuildCredential(settings, secret),
            new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });

    /// <summary>
    /// Same two-mode Entra dispatch the OneLake and Warehouse factories perform, kept deliberately in step with
    /// them: one Fabric identity should be configured the same way whichever Fabric surface it reaches.
    ///
    /// <para>Note a service principal also needs the Fabric tenant setting "Service principals can use Fabric
    /// APIs" enabled — it is on by default for newer tenants, but a principal with correct database permissions
    /// still cannot connect when it is off, and nothing in the resulting error says so.</para>
    /// </summary>
    private static TokenCredential BuildCredential(CosmosDbFabricDestinationSettings settings, string? secret)
        => settings.AuthMode switch
        {
            FabricAuthMode.ServicePrincipal => new ClientSecretCredential(
                settings.TenantId,
                settings.ClientId,
                secret,
                new ClientSecretCredentialOptions { AuthorityHost = ParseAuthorityHost(settings.AuthorityHost) }),

            FabricAuthMode.ManagedIdentity => new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = settings.ManagedIdentityClientId,
                AuthorityHost = ParseAuthorityHost(settings.AuthorityHost),
            }),

            _ => throw new InvalidOperationException($"Unsupported Fabric auth mode '{settings.AuthMode}'."),
        };

    private static Uri? ParseAuthorityHost(string? raw)
        => !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;
}
