using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Tests an ad-hoc Azure Blob Storage connection before anything is saved. Builds the container client for the
/// requested auth mode from the raw form credentials — mirroring <c>BlobContainerClientFactory.Build</c>'s
/// per-mode construction (duplicated rather than reused because that factory resolves its secret from Key Vault
/// via <c>ISecretProvider</c>, which an unsaved connection has no reference to) — then does a real reachability
/// round-trip (container <c>ExistsAsync</c>), which validates credentials + network without the container needing
/// to already exist. A short linked timeout keeps a dead endpoint from hanging on the SDK's default retry budget.
/// Keep the auth-mode switch in sync with <c>BlobContainerClientFactory</c> when modes change.
/// </summary>
public sealed class BlobDestinationConnectionTestService : IBlobDestinationConnectionTestService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

    public async Task<ConnectionTestResultDto> TestConnectionAsync(
        BlobConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Container))
        {
            return new ConnectionTestResultDto(false, "Container name is required.");
        }

        BlobContainerClient container;
        try
        {
            container = BuildContainerClient(request);
        }
        catch (Exception exception)
        {
            return new ConnectionTestResultDto(false, exception.Message);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);
        try
        {
            // ExistsAsync is a real authenticated round-trip; it returns false (not an error) when the container
            // simply doesn't exist yet, so a successful call proves reachability + credentials regardless.
            await container.ExistsAsync(timeoutCts.Token);
            return new ConnectionTestResultDto(true, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResultDto(false, $"Timed out after {ProbeTimeout.TotalSeconds:0}s connecting to the storage endpoint.");
        }
        catch (Exception exception)
        {
            return new ConnectionTestResultDto(false, exception.Message);
        }
    }

    private static BlobContainerClient BuildContainerClient(BlobConnectionTestRequest r)
    {
        var container = r.Container;
        var endpointSuffix = string.IsNullOrWhiteSpace(r.EndpointSuffix) ? "core.windows.net" : r.EndpointSuffix!;
        var accountUrl = r.AccountUrl;
        if (string.IsNullOrWhiteSpace(accountUrl) && !string.IsNullOrWhiteSpace(r.AccountName))
        {
            accountUrl = $"https://{r.AccountName}.blob.{endpointSuffix}";
        }

        switch (r.AuthMode?.Trim().ToLowerInvariant())
        {
            case "connectionstring":
                RequireSecret(r.Secret, "A connection string is required.");
                return new BlobServiceClient(r.Secret).GetBlobContainerClient(container);

            case "accountkey":
                RequireSecret(r.Secret, "An account key is required.");
                if (string.IsNullOrWhiteSpace(r.AccountName))
                    throw new InvalidOperationException("Account name is required for Account Key auth.");
                if (string.IsNullOrWhiteSpace(accountUrl))
                    throw new InvalidOperationException("Account URL or account name is required.");
                return new BlobServiceClient(new Uri(accountUrl), new StorageSharedKeyCredential(r.AccountName, r.Secret))
                    .GetBlobContainerClient(container);

            case "sasurl":
                RequireSecret(r.Secret, "A SAS URL or token is required.");
                return BuildFromSas(r.Secret!, accountUrl, container);

            case "managedidentity":
                if (string.IsNullOrWhiteSpace(accountUrl))
                    throw new InvalidOperationException("Account URL or account name is required for Managed Identity auth.");
                return new BlobServiceClient(
                    new Uri(accountUrl),
                    new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = r.ManagedIdentityClientId }))
                    .GetBlobContainerClient(container);

            case "serviceprincipal":
                RequireSecret(r.Secret, "A client secret is required.");
                if (string.IsNullOrWhiteSpace(accountUrl))
                    throw new InvalidOperationException("Account URL or account name is required for Service Principal auth.");
                if (string.IsNullOrWhiteSpace(r.TenantId) || string.IsNullOrWhiteSpace(r.ClientId))
                    throw new InvalidOperationException("Tenant ID and client ID are required for Service Principal auth.");
                return new BlobServiceClient(new Uri(accountUrl), new ClientSecretCredential(r.TenantId, r.ClientId, r.Secret))
                    .GetBlobContainerClient(container);

            default:
                throw new InvalidOperationException($"Unsupported blob auth mode '{r.AuthMode}'.");
        }
    }

    private static BlobContainerClient BuildFromSas(string sas, string? accountUrl, string container)
    {
        if (Uri.TryCreate(sas, UriKind.Absolute, out var sasUri))
        {
            // A container-scoped SAS's path is "/{container}[/...]"; an account/service SAS has no path segment.
            var hasContainerSegment = sasUri.AbsolutePath.Trim('/').Length > 0;
            return hasContainerSegment
                ? new BlobContainerClient(sasUri)
                : new BlobServiceClient(sasUri).GetBlobContainerClient(container);
        }

        if (string.IsNullOrWhiteSpace(accountUrl))
        {
            throw new InvalidOperationException("A bare SAS token requires an account URL or account name.");
        }

        return new BlobServiceClient(new Uri($"{accountUrl.TrimEnd('/')}?{sas.TrimStart('?')}")).GetBlobContainerClient(container);
    }

    private static void RequireSecret(string? secret, string message)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(message);
        }
    }
}
