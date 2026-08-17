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

    public DbSecretStore(FHIRBridgeDbContext dbContext, IDataProtectionProvider dataProtectionProvider, ILogger<DbSecretStore> logger)
    {
        _dbContext = dbContext;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    /// <summary>
    /// Returns the decrypted secret, or null when no provisioned secret matches the reference OR the stored
    /// value can no longer be decrypted (its Data Protection key ring is gone — e.g. it was written before
    /// persistent key storage was wired up, or the key ring's underlying storage was reset/lost for any other
    /// reason). Treating an undecryptable row the same as "no row found" lets <see cref="AppSecretProvisioner"/>
    /// self-heal by generating and writing a fresh secret, rather than this exception crashing the whole
    /// process on every single boot from then on.
    /// </summary>
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
        catch (CryptographicException ex)
        {
            _logger.LogWarning(
                ex,
                "Stored secret '{SecretName}' in vault '{KeyVaultName}' could not be decrypted with the current " +
                "Data Protection key ring — treating it as missing so a fresh value gets provisioned.",
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
