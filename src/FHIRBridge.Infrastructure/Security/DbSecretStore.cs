using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

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

    public DbSecretStore(FHIRBridgeDbContext dbContext, IDataProtectionProvider dataProtectionProvider)
    {
        _dbContext = dbContext;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    /// <summary>Returns the decrypted secret, or null when no provisioned secret matches the reference.</summary>
    public async Task<string?> TryGetSecretAsync(SecretReference secretReference, CancellationToken cancellationToken)
    {
        var row = await _dbContext.ProvisionedSecrets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.KeyVaultName == secretReference.KeyVaultName && s.SecretName == secretReference.SecretName,
                cancellationToken);

        return row is null ? null : _protector.Unprotect(row.ProtectedValue);
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
