using Azure.Core;
using Azure.Identity;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Opens a Fabric Warehouse connection using the destination's own Entra identity, attached as an access token
/// rather than embedded in the connection string.
///
/// <para><b>Unverified against a live Fabric tenant.</b> The shape below — SQL audience token on
/// <see cref="SqlConnection.AccessToken"/> — is the documented way to authenticate to a Fabric Warehouse, and it
/// is the same mechanism Azure SQL uses. It has not been exercised against a real Warehouse, so treat the exact
/// scope and any tenant-side grant requirements as the first thing to check if authentication fails.</para>
/// </summary>
internal sealed class FabricWarehouseConnectionFactory : IFabricWarehouseConnectionFactory
{
    /// <summary>
    /// The SQL data-plane audience. Note this is NOT a Fabric-specific scope: a Warehouse's TDS endpoint
    /// authenticates as SQL does, which is why the same value works for Azure SQL.
    /// </summary>
    private const string SqlScope = "https://database.windows.net/.default";

    private readonly ISecretProvider _secretProvider;
    private readonly ILogger<FabricWarehouseConnectionFactory> _logger;

    public FabricWarehouseConnectionFactory(
        ISecretProvider secretProvider, ILogger<FabricWarehouseConnectionFactory> logger)
    {
        _secretProvider = secretProvider;
        _logger = logger;
    }

    public async Task<SqlConnection> OpenAsync(
        DestinationConfiguration destination,
        FabricDestinationSettings settings,
        CancellationToken cancellationToken)
    {
        var secret = settings.RequiresSecret
            ? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken)
            : null;

        var credential = BuildCredential(settings, secret);
        var token = await credential.GetTokenAsync(new TokenRequestContext([SqlScope]), cancellationToken);

        _logger.LogDebug(
            "Opening Fabric Warehouse connection for destination {DestinationId} ({DestinationName}), auth mode "
                + "{AuthMode}, warehouse {Warehouse}.",
            destination.Id,
            destination.Name,
            settings.AuthMode,
            settings.ItemName);

        var connection = new SqlConnection(settings.WarehouseSqlEndpoint) { AccessToken = token.Token };
        await connection.OpenAsync(cancellationToken);

        return connection;
    }

    /// <summary>
    /// Same two-mode Entra dispatch <see cref="OneLakeClientFactory"/> performs, kept in step with it deliberately:
    /// one destination configures one identity, and it must reach both OneLake and the Warehouse.
    /// </summary>
    private static TokenCredential BuildCredential(FabricDestinationSettings settings, string? secret)
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
