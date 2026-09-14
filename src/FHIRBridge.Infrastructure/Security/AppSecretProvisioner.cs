using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Configuration;
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
        var metadataProvider = scope.ServiceProvider.GetRequiredService<IAppSecretMetadataProvider>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var accessor = rootServiceProvider.GetRequiredService<AppSecretAccessor>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("FHIRBridge.Infrastructure.Security.AppSecretProvisioner");

        var allowRegeneration = configuration.GetValue(AllowRegenerationKey, false);

        var jwtSigningKey = await EnsureSecretAsync(
            secretProvider, secretWriter, metadataProvider, AppSecretReferences.JwtSigningKey, allowRegeneration, logger, cancellationToken);
        var downloadLinkSigningSecret = await EnsureSecretAsync(
            secretProvider, secretWriter, metadataProvider, AppSecretReferences.DownloadLinkSigningSecret, allowRegeneration, logger, cancellationToken);
        var transformHashingKey = await EnsureSecretAsync(
            secretProvider, secretWriter, metadataProvider, AppSecretReferences.TransformHashingKey, allowRegeneration, logger, cancellationToken);
        var phiEncryptionKey = await EnsureSecretAsync(
            secretProvider, secretWriter, metadataProvider, AppSecretReferences.PhiEncryptionKey, allowRegeneration, logger, cancellationToken);
        var installationId = await EnsureSecretAsync(
            secretProvider, secretWriter, metadataProvider, AppSecretReferences.InstallationId, allowRegeneration, logger, cancellationToken);

        accessor.Initialize(
            jwtSigningKey, downloadLinkSigningSecret, transformHashingKey, phiEncryptionKey, installationId);
    }

    /// <summary>
    /// Escape hatch for the one legitimate case where regenerating over an unreadable secret is what the operator
    /// wants: the Data Protection key ring is genuinely gone (e.g. a container volume was lost) and they accept
    /// that previously encrypted execution-history payloads are unrecoverable. Off by default so the far more
    /// common case — a key ring that is merely resolving to the wrong place — fails loudly instead of destroying
    /// data. See <see cref="DataProtectionKeyRingPathResolver"/>.
    /// </summary>
    private const string AllowRegenerationKey = "DataProtection:AllowAppSecretRegeneration";

    private static async Task<string> EnsureSecretAsync(
        ISecretProvider secretProvider,
        ISecretWriter secretWriter,
        IAppSecretMetadataProvider metadataProvider,
        SecretReference reference,
        bool allowRegeneration,
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
            // "Not configured" covers two very different situations that the provider cannot distinguish, because
            // an undecryptable row is reported as absent (see DbSecretStore.TryGetSecretAsync). Metadata reads the
            // row's existence WITHOUT decrypting it, which separates them:
            //   • no row at all  -> genuine first boot on this install; generate and persist.
            //   • row present    -> it exists but this process cannot decrypt it, i.e. the Data Protection key ring
            //                       is not the one that wrote it. Overwriting here is what silently orphans every
            //                       already-encrypted execution-history payload, so refuse and fail the boot.
            var metadata = await metadataProvider.GetMetadataAsync(reference, cancellationToken);
            if (metadata.Provisioned && !allowRegeneration)
            {
                throw new InvalidOperationException(
                    $"App secret '{reference.SecretName}' (vault '{reference.KeyVaultName}') exists but could not be " +
                    "decrypted, which means this process resolved a different Data Protection key ring than the one " +
                    "that wrote it. Startup has been stopped deliberately: regenerating it would permanently orphan " +
                    "every execution-history payload already encrypted with the current key, and would invalidate " +
                    "issued tokens and download links. Fix the key ring instead — confirm DataProtection:KeyRingPath " +
                    "is set to the same persistent location for BOTH the Api and Worker hosts (in a container " +
                    "deployment, that the keys volume is mounted), and that the account this process runs as can read " +
                    $"it. If the key ring is genuinely unrecoverable and losing that data is acceptable, set " +
                    $"{AllowRegenerationKey}=true for a single boot to regenerate.");
            }

            var generated = AppSecretValueGenerator.Generate();
            await secretWriter.WriteSecretAsync(reference, generated, cancellationToken);

            if (metadata.Provisioned)
            {
                logger.LogWarning(
                    "Regenerated app secret '{SecretName}' in vault '{KeyVaultName}' over an existing but " +
                    "undecryptable value because {AllowRegenerationKey} is enabled. Data encrypted with the previous " +
                    "value — including recorded execution-history payloads — is now permanently unreadable.",
                    reference.SecretName,
                    reference.KeyVaultName,
                    AllowRegenerationKey);
            }
            else
            {
                logger.LogInformation(
                    "Generated app secret '{SecretName}' in vault '{KeyVaultName}' — no value was provisioned yet " +
                    "(first boot on this install).",
                    reference.SecretName,
                    reference.KeyVaultName);
            }

            return generated;
        }
    }
}
