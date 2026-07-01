using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FHIRBridge.Infrastructure.Health;

public sealed class KeyVaultConfigurationHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly ISecretProvider _secretProvider;

    public KeyVaultConfigurationHealthCheck(IConfiguration configuration, ISecretProvider secretProvider)
    {
        _configuration = configuration;
        _secretProvider = secretProvider;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var useAzureKeyVault = _configuration.GetValue("KeyVault:UseAzureKeyVault", false);
        if (!useAzureKeyVault)
        {
            return HealthCheckResult.Healthy("Azure Key Vault is disabled; local configuration secrets are active.");
        }

        var probeVault = _configuration["KeyVault:HealthProbe:KeyVaultName"];
        var probeSecret = _configuration["KeyVault:HealthProbe:SecretName"];
        if (string.IsNullOrWhiteSpace(probeVault) || string.IsNullOrWhiteSpace(probeSecret))
        {
            return HealthCheckResult.Degraded("Azure Key Vault is enabled but no health probe secret is configured.");
        }

        try
        {
            await _secretProvider.GetSecretAsync(new SecretReference(probeVault, probeSecret), cancellationToken);

            return HealthCheckResult.Healthy("Azure Key Vault secret resolution is healthy.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Azure Key Vault secret resolution failed.", exception);
        }
    }
}
