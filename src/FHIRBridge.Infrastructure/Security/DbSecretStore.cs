using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Reads and writes application-provisioned secrets (B1) in the control-plane DB, encrypted at rest with
/// DataProtection. The composite secret provider checks this store before configuration/Key Vault, so a secret
/// provisioned via <see cref="WriteSecretAsync"/> resolves through the normal <c>ISecretProvider</c> path.
/// </summary>
public sealed class DbSecretStore : ISecretWriter, IAppSecretMetadataProvider
{
    private const string ProtectorPurpose = "FHIRBridge.Secrets.v1";

    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IDataProtector _protector;
    private readonly ILogger<DbSecretStore> _logger;

    public DbSecretStore(
        FHIRBridgeDbContext dbContext,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<DbSecretStore> logger)
    {
        _dbContext = dbContext;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    /// <summary>Returns the decrypted secret, or null when no provisioned secret matches the reference — including
    /// when a row exists but the DataProtection key that encrypted it is no longer in the key ring (e.g. the key
    /// ring's persistent storage was reset). Treating that the same as "not provisioned" lets
    /// <see cref="AppSecretProvisioner"/>'s existing first-boot self-healing regenerate the value instead of the
    /// raw <see cref="CryptographicException"/> crashing app startup.</summary>
    public async Task<string?> TryGetSecretAsync(SecretReference secretReference, CancellationToken cancellationToken)
    {
        var row = await _dbContext.ProvisionedSecrets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.KeyVaultName == secretReference.KeyVaultName && s.SecretName == secretReference.SecretName,
                cancellationToken);

        if (row is null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(row.ProtectedValue);
        }
        catch (CryptographicException exception)
        {
            _logger.LogWarning(
                exception,
                "Could not decrypt provisioned secret '{SecretName}' in vault '{KeyVaultName}' — its DataProtection " +
                "key is no longer in the key ring. Treating it as unset so it gets regenerated.",
                secretReference.SecretName,
                secretReference.KeyVaultName);
            return null;
        }
    }

    public async Task WriteSecretAsync(
        SecretReference secretReference,
        string secretValue,
        CancellationToken cancellationToken)
    {
        var protectedValue = _protector.Protect(secretValue);

        var row = await _dbContext.ProvisionedSecrets.FirstOrDefaultAsync(
            s => s.KeyVaultName == secretReference.KeyVaultName && s.SecretName == secretReference.SecretName,
            cancellationToken);

        if (row is null)
        {
            _dbContext.ProvisionedSecrets.Add(
                new ProvisionedSecret(secretReference.KeyVaultName, secretReference.SecretName, protectedValue));
        }
        else
        {
            row.Update(protectedValue);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Whether a value exists and when it was last written — never decrypts <c>ProtectedValue</c>.</summary>
    public async Task<ProvisionedSecretMetadata> GetMetadataAsync(
        SecretReference secretReference, CancellationToken cancellationToken)
    {
        var row = await _dbContext.ProvisionedSecrets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.KeyVaultName == secretReference.KeyVaultName && s.SecretName == secretReference.SecretName,
                cancellationToken);

        return row is null
            ? new ProvisionedSecretMetadata(false, null)
            : new ProvisionedSecretMetadata(true, row.ModifiedOnUtc);
    }
}
