using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Infrastructure.Security;

public sealed class ConfigurationSecretProvider : ISecretProvider
{
    private readonly IConfiguration _configuration;

    public ConfigurationSecretProvider(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Task<string> GetSecretAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        var value = _configuration[$"Secrets:{secretReference.KeyVaultName}:{secretReference.SecretName}"];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SecretNotConfiguredException(secretReference.SecretName, secretReference.KeyVaultName);
        }

        return Task.FromResult(value);
    }

    /// <summary>
    /// Null-returning twin of <see cref="GetSecretAsync"/>, so <see cref="CompositeSecretProvider"/> can probe
    /// several candidate references in turn and raise one exception naming them all, instead of the first miss
    /// throwing an exception that names only one of the vaults it looked in.
    /// </summary>
    public string? TryGetSecret(SecretReference secretReference)
    {
        var value = _configuration[$"Secrets:{secretReference.KeyVaultName}:{secretReference.SecretName}"];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
