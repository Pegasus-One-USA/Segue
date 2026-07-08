using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// In-process stand-in for <see cref="DbSecretStore"/> when no database is configured (the "InMemory" dev/test
/// path) -- same read-then-config-fallback shape as <see cref="CompositeSecretProvider"/>, without the
/// <c>FHIRBridgeDbContext</c> dependency that path doesn't register. Not durable across restarts.
/// </summary>
public sealed class InMemorySecretStore : ISecretWriter, ISecretProvider
{
    private readonly ConcurrentDictionary<(string KeyVaultName, string SecretName), string> _secrets = new();
    private readonly IConfiguration _configuration;

    public InMemorySecretStore(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Task WriteSecretAsync(SecretReference secretReference, string secretValue, CancellationToken cancellationToken)
    {
        _secrets[(secretReference.KeyVaultName, secretReference.SecretName)] = secretValue;
        return Task.CompletedTask;
    }

    public Task<string> GetSecretAsync(SecretReference secretReference, CancellationToken cancellationToken)
    {
        if (_secrets.TryGetValue((secretReference.KeyVaultName, secretReference.SecretName), out var stored))
        {
            return Task.FromResult(stored);
        }

        var configured = _configuration[$"Secrets:{secretReference.KeyVaultName}:{secretReference.SecretName}"];
        if (string.IsNullOrEmpty(configured))
        {
            throw new InvalidOperationException(
                $"No secret configured for '{secretReference.KeyVaultName}/{secretReference.SecretName}'.");
        }

        return Task.FromResult(configured);
    }
}
