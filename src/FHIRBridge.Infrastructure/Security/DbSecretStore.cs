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

    /// <summary>Returns the decrypted secret, or null when no provisioned secret matches the reference — including
    /// when a row exists but was encrypted under a Data Protection key no longer in the current key ring (e.g. the
    /// key ring was reset/regenerated since the row was written). That row is unrecoverable either way, so it's
    /// treated the same as "no value provisioned yet" rather than throwing and taking down the whole caller — for
    /// the app-provisioned secrets (see AppSecretProvisioner) this makes recovery self-healing (a fresh value is
    /// generated), and for a connector secret it surfaces as that connector's normal "not configured" error instead
    /// of crashing Api/Worker startup entirely.</summary>
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
                "Provisioned secret '{SecretName}' in vault '{KeyVaultName}' could not be decrypted with the " +
                "current Data Protection key ring (row id {SecretId}) — treating it as absent.",
                secretReference.SecretName,
                secretReference.KeyVaultName,
                row.Id);
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
