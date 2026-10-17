using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A database a CSV / SQL Table source reads from, saved once and picked by name in any workflow. The connection
/// string itself is never stored here: it is a secret (<see cref="KeyVaultName"/> / <see cref="SecretName"/>), and a
/// node keeps the same reference, so replacing the connection string reaches every workflow that uses it.
/// </summary>
public sealed class TabularSqlConnection : Entity<Guid>
{
    private TabularSqlConnection()
    {
    }

    public TabularSqlConnection(string name, string engine, string keyVaultName, string secretName, string? createdBy, DateTime createdOnUtc)
    {
        Id = Guid.NewGuid();
        Rename(name);
        Engine = engine;
        KeyVaultName = keyVaultName;
        SecretName = secretName;
        CreatedBy = createdBy is { Length: > 256 } ? createdBy[..256] : createdBy;
        CreatedOnUtc = createdOnUtc;
        UpdatedOnUtc = createdOnUtc;
    }

    public string Name { get; private set; } = default!;

    /// <summary>"sqlserver", "postgresql" or "mysql".</summary>
    public string Engine { get; private set; } = default!;

    public string KeyVaultName { get; private set; } = default!;

    public string SecretName { get; private set; } = default!;

    public string? CreatedBy { get; private set; }

    public DateTime CreatedOnUtc { get; private set; }

    public DateTime UpdatedOnUtc { get; private set; }

    public void Rename(string name)
    {
        var trimmed = name.Trim();
        Name = trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    /// <summary>The connection string was replaced in its secret (same reference).</summary>
    public void Touch(DateTime utcNow) => UpdatedOnUtc = utcNow;
}
