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
}
