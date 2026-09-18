using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Destinations.Fabric;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Data.SqlClient;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Tests a Microsoft Fabric connection before anything is saved.
///
/// <para><b>Why this probes two endpoints separately.</b> "Fabric" is not one service. OneLake authenticates
/// against a storage audience; a Warehouse authenticates against the SQL audience over TDS. One identity, two
/// tokens, two things that can independently be wrong — and when a pipeline run fails, both look the same to a
/// user. Reporting them apart is the whole value of this test: it turns "the Fabric destination failed" into
/// "OneLake is fine, the Warehouse endpoint is refusing you".</para>
///
/// <para><b>Why 403 gets its own message.</b> The expected first failure on a new Fabric setup is not a bad
/// secret — it is an identity that authenticated correctly and still cannot write, because OneLake authorizes
/// through Fabric's own workspace permissions rather than Azure RBAC. Granting a storage role does nothing. That
/// is not discoverable from "403 Forbidden", so it is spelled out.</para>
///
/// Mirrors <see cref="BlobDestinationConnectionTestService"/>'s contract: never throws for connection failures,
/// returns Connected=false + Error instead, and resolves a saved destination's stored secret when the form has
/// not re-displayed it.
/// </summary>
public sealed class FabricDestinationConnectionTestService : IFabricDestinationConnectionTestService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The SQL data-plane audience — a Warehouse's TDS endpoint authenticates as Azure SQL does.</summary>
    private const string SqlScope = "https://database.windows.net/.default";

    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProvider _secretProvider;

    public FabricDestinationConnectionTestService(
        IConfigurationRepository configurationRepository, ISecretProvider secretProvider)
    {
        _configurationRepository = configurationRepository;
        _secretProvider = secretProvider;
    }

    public async Task<FabricConnectionTestResultDto> TestConnectionAsync(
        FabricConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Workspace))
        {
            return Failure("Workspace is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ItemName))
        {
            return Failure("Lakehouse (item) name is required.");
        }

        var isWarehouse = string.Equals(request.Mode, "warehouseTable", StringComparison.OrdinalIgnoreCase);
        if (isWarehouse && string.IsNullOrWhiteSpace(request.WarehouseSqlEndpoint))
        {
            return Failure("Warehouse SQL connection string is required for the Warehouse landing mode.");
        }

        // Re-testing a saved destination: the form never re-displays the stored secret, so a blank Secret means
        // "use what's already saved", not "no secret".
        if (string.IsNullOrWhiteSpace(request.Secret) && request.DestinationId is { } destinationId)
        {
            var storedSecret = await ResolveStoredSecretAsync(destinationId, cancellationToken);
            if (storedSecret is not null)
            {
                request = request with { Secret = storedSecret };
            }
        }

        TokenCredential credential;
        try
        {
            credential = BuildCredential(request);
        }
        catch (Exception exception)
        {
            return Failure(exception.Message);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);

        // ── OneLake ────────────────────────────────────────────────────────────────────────────────────────
        // Probed even in Warehouse mode: that mode stages its Parquet through OneLake, so OneLake being
        // unreachable breaks a Warehouse load just as surely as the TDS endpoint being unreachable.
        var (oneLakeOk, oneLakeError, oneLakePermissionHint) =
            await ProbeOneLakeAsync(request, credential, timeoutCts.Token, cancellationToken);

        if (!oneLakeOk)
        {
            return new FabricConnectionTestResultDto(
                false, oneLakeError, false, isWarehouse ? false : null, oneLakePermissionHint);
        }

        if (!isWarehouse)
        {
            return new FabricConnectionTestResultDto(true, null, true, null, null);
        }

        // ── Warehouse (TDS) ────────────────────────────────────────────────────────────────────────────────
        var (warehouseOk, warehouseError, warehousePermissionHint) =
            await ProbeWarehouseAsync(request, credential, timeoutCts.Token, cancellationToken);

        return warehouseOk
            ? new FabricConnectionTestResultDto(true, null, true, true, null)
            : new FabricConnectionTestResultDto(
                false,
                $"OneLake is reachable, but the Warehouse endpoint is not: {warehouseError}",
                true,
                false,
                warehousePermissionHint);
    }

    private static async Task<(bool Ok, string? Error, string? PermissionHint)> ProbeOneLakeAsync(
        FabricConnectionTestRequest request,
        TokenCredential credential,
        CancellationToken probeToken,
        CancellationToken callerToken)
    {
        try
        {
            // Derived through FabricDestinationSettings.AccountUrl rather than rebuilt here, so the probe can only
            // ever target the URL a real write would use. These were two separate expressions once, and they drifted:
            // this one blank-checked the override while the settings record only null-checked it, so a destination
            // saved with an untouched (empty-string) override tested Connected and then failed the actual write with
            // "The URI is empty". One derivation means that class of divergence cannot recur.
            var accountUrl = new FabricDestinationSettings(
                Mode: FabricLandingMode.OneLakeFiles,
                AuthMode: FabricAuthMode.ManagedIdentity,
                Workspace: request.Workspace,
                ItemName: request.ItemName,
                ItemType: "Lakehouse",
                BasePath: string.Empty,
                FileFormat: FabricFileFormat.Ndjson,
                Partitioning: FabricPartitionScheme.None,
                TenantId: null,
                ClientId: null,
                ManagedIdentityClientId: null,
                AuthorityHost: null,
                EndpointSuffix: Blank(request.EndpointSuffix) ? "fabric.microsoft.com" : request.EndpointSuffix!.Trim(),
                AccountUrlOverride: Blank(request.AccountUrl) ? null : request.AccountUrl!.Trim()).AccountUrl;

            // The workspace is the container. ExistsAsync is a real authenticated round-trip and returns false
            // rather than throwing when the target is simply absent, so reaching it at all proves credentials
            // and network regardless of what is provisioned inside.
            var container = new BlobServiceClient(new Uri(accountUrl), credential)
                .GetBlobContainerClient(request.Workspace);

            var exists = await container.ExistsAsync(probeToken);
            if (!exists.Value)
            {
                return (false,
                    $"Authenticated, but workspace '{request.Workspace}' was not found at {accountUrl}. Check the "
                        + "workspace name — it is the name as it appears in Fabric, not a URL or a path.",
                    null);
            }

            return (true, null, null);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return (false, $"Timed out after {ProbeTimeout.TotalSeconds:0}s connecting to OneLake.", null);
        }
        catch (Azure.RequestFailedException exception) when (exception.Status is 401 or 403)
        {
            return (false, exception.Message, WorkspacePermissionHint());
        }
        catch (Exception exception)
        {
            return (false, exception.Message, null);
        }
    }

    private static async Task<(bool Ok, string? Error, string? PermissionHint)> ProbeWarehouseAsync(
        FabricConnectionTestRequest request,
        TokenCredential credential,
        CancellationToken probeToken,
        CancellationToken callerToken)
    {
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([SqlScope]), probeToken);

            await using var connection = new SqlConnection(request.WarehouseSqlEndpoint)
            {
                AccessToken = token.Token,
            };
            await connection.OpenAsync(probeToken);

            // Opening the connection already proves auth + network; SELECT 1 confirms the session can actually
            // run a statement, which is what a COPY INTO will need.
            await using var command = new SqlCommand("SELECT 1;", connection);
            await command.ExecuteScalarAsync(probeToken);

            return (true, null, null);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return (false, $"timed out after {ProbeTimeout.TotalSeconds:0}s.", null);
        }
        catch (SqlException exception) when (exception.Number is 18456 or 4060 or 40615)
        {
            return (false, exception.Message, WarehousePermissionHint());
        }
        catch (Exception exception)
        {
            return (false, exception.Message, null);
        }
    }

    private static string WorkspacePermissionHint()
        => "The identity authenticated but was refused. OneLake authorizes through Fabric's own workspace "
            + "permissions, not Azure RBAC — a Storage Blob Data role on a storage account has no effect here. "
            + "Grant this identity Contributor (or another Write-capable role) on the Fabric workspace itself. "
            + "For a service principal, also confirm the tenant setting 'Service principals can use Fabric APIs' "
            + "is enabled.";

    private static string WarehousePermissionHint()
        => "The identity reached the Warehouse but was refused. Grant it access to the Warehouse item in Fabric, "
            + "and confirm the connection string names the Warehouse (not a Lakehouse SQL analytics endpoint, "
            + "which is read-only and cannot accept a load).";

    /// <summary>
    /// Entra only — OneLake accepts no account key or SAS, so this has two modes where the Blob test has five.
    /// Kept in step with <c>OneLakeClientFactory.BuildCredential</c> and
    /// <c>FabricWarehouseConnectionFactory.BuildCredential</c>.
    /// </summary>
    private static TokenCredential BuildCredential(FabricConnectionTestRequest request)
    {
        var authorityHost = ParseAuthorityHost(request.AuthorityHost);

        switch (request.AuthMode?.Trim().ToLowerInvariant())
        {
            case "serviceprincipal":
                if (Blank(request.Secret))
                {
                    throw new InvalidOperationException("A client secret is required for service principal auth.");
                }

                if (Blank(request.TenantId) || Blank(request.ClientId))
                {
                    throw new InvalidOperationException(
                        "Tenant ID and client ID are required for service principal auth.");
                }

                return new ClientSecretCredential(
                    request.TenantId,
                    request.ClientId,
                    request.Secret,
                    new ClientSecretCredentialOptions { AuthorityHost = authorityHost });

            case "managedidentity":
            case null:
            case "":
                return new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ManagedIdentityClientId = request.ManagedIdentityClientId,
                    AuthorityHost = authorityHost,
                });

            default:
                throw new InvalidOperationException(
                    $"Unsupported Fabric authentication mode '{request.AuthMode}'. OneLake accepts Entra "
                        + "credentials only: managedIdentity or servicePrincipal.");
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

    private static FabricConnectionTestResultDto Failure(string error)
        => new(false, error, false, null, null);

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static Uri? ParseAuthorityHost(string? raw)
        => !Blank(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;
}
