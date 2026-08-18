namespace FHIRBridge.Infrastructure.Destinations.Blob;

/// <summary>
/// Caches one <see cref="BlobDestinationTarget"/> per destination+credential combination, singleton-scoped so it
/// survives request/pipeline-run scopes — mirrors <see cref="Security.AzureKeyVaultSecretProvider"/>'s
/// resolve-once-and-cache shape, just lifted above the per-request <see cref="Application.Abstractions.Security.ISecretProvider"/>
/// scope that builds the key. Each constructed client owns its own connection pool/token cache, so rebuilding one
/// on every write would churn sockets and, for credential-based modes, throw away <c>DefaultAzureCredential</c>'s
/// in-memory token cache.
/// </summary>
internal sealed class BlobContainerClientCache
{
    private readonly Dictionary<string, BlobDestinationTarget> _targets = new(StringComparer.Ordinal);

    public BlobDestinationTarget GetOrAdd(string cacheKey, Func<BlobDestinationTarget> build)
    {
        lock (_targets)
        {
            if (_targets.TryGetValue(cacheKey, out var existing))
            {
                return existing;
            }

            var target = build();
            _targets[cacheKey] = target;
            return target;
        }
    }
}
