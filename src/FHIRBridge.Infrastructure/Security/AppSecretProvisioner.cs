using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Ensures the app-level signing secrets exist on first boot: resolves each via <see cref="ISecretProvider"/>,
/// generating and persisting (via <see cref="ISecretWriter"/>) a random value the first time an install has none.
/// Every subsequent boot — on this instance or a peer sharing the same database/Data Protection key ring —
/// resolves the same stored value, so tokens/links minted before a restart keep validating. A marketplace image
/// deployed independently per customer therefore never ships a value shared across installs.
/// Must run after the database has been migrated: the provisioned secret is persisted there (DbSecretStore).
/// </summary>
public static class AppSecretProvisioner
{
    public static async Task ProvisionAsync(IServiceProvider rootServiceProvider, CancellationToken cancellationToken)
    {
        using var scope = rootServiceProvider.CreateScope();
        var secretProvider = scope.ServiceProvider.GetRequiredService<ISecretProvider>();
        var secretWriter = scope.ServiceProvider.GetRequiredService<ISecretWriter>();
        var accessor = rootServiceProvider.GetRequiredService<AppSecretAccessor>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("FHIRBridge.Infrastructure.Security.AppSecretProvisioner");

        var jwtSigningKey = await EnsureSecretAsync(
            secretProvider, secretWriter, AppSecretReferences.JwtSigningKey, logger, cancellationToken);
        var downloadLinkSigningSecret = await EnsureSecretAsync(
            secretProvider, secretWriter, AppSecretReferences.DownloadLinkSigningSecret, logger, cancellationToken);
        var transformHashingKey = await EnsureSecretAsync(
            secretProvider, secretWriter, AppSecretReferences.TransformHashingKey, logger, cancellationToken);

        accessor.Initialize(jwtSigningKey, downloadLinkSigningSecret, transformHashingKey);
    }

    private static async Task<string> EnsureSecretAsync(
        ISecretProvider secretProvider,
        ISecretWriter secretWriter,
        SecretReference reference,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await secretProvider.GetSecretAsync(reference, cancellationToken);
            logger.LogInformation(
                "Resolved existing app secret '{SecretName}' in vault '{KeyVaultName}'.",
                reference.SecretName,
                reference.KeyVaultName);
            return existing;
        }
        catch (SecretNotConfiguredException)
        {
            return await RegenerateAsync(
                secretWriter, reference, logger, cancellationToken,
                "none was found (first boot, or a prior value was deleted)");
        }
        catch (CryptographicException)
        {
            // The Data Protection key ring (e.g. a volume/environment reset) is out of sync with a value already
            // persisted in the database — decrypting it will never succeed again on this key ring. Regenerating
            // (same remediation as "not configured") keeps this self-healing instead of crash-looping forever.
            return await RegenerateAsync(
                secretWriter, reference, logger, cancellationToken,
                "the stored value could not be decrypted — its Data Protection key is no longer in the key ring");
        }
    }

    private static async Task<string> RegenerateAsync(
        ISecretWriter secretWriter,
        SecretReference reference,
        ILogger logger,
        CancellationToken cancellationToken,
        string reason)
    {
        var generated = AppSecretValueGenerator.Generate();
        await secretWriter.WriteSecretAsync(reference, generated, cancellationToken);
        logger.LogWarning(
            "Generated a new app secret '{SecretName}' in vault '{KeyVaultName}' — {Reason}. Any tokens/links " +
            "signed with a previous value are now invalid.",
            reference.SecretName,
            reference.KeyVaultName,
            reason);
        return generated;
    }
}
