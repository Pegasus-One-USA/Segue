using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class DestinationConfiguration : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private DestinationConfiguration()
    {
    }

    public DestinationConfiguration(
        string name,
        DestinationType destinationType,
        SecretReference secretReference,
        string? target,
        string? connectionMetadataJson = null)
    {
        Id = Guid.NewGuid();
        Name = name;
        DestinationType = destinationType;
        SecretReference = secretReference;
        Target = target;
        ConnectionMetadataJson = connectionMetadataJson;
        IsEnabled = true;
    }

    public string Name { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => Name;
    public DestinationType DestinationType { get; private set; }
    public SecretReference SecretReference { get; private set; } = default!;
    public string? Target { get; private set; }

    /// <summary>
    /// Non-secret connection fields (server/database/schema/writeMode for SQL; folder/delimiter/encoding/sftp host
    /// etc. for CSV/SFTP) serialized as a flat JSON object, keyed the same way the workflow wizard's own dest_*
    /// config bag is — so the wizard can repopulate its form when a caller reuses this row via "Existing" instead
    /// of re-collecting every field from scratch. Deliberately excludes any secret (password/sftpPassword): those
    /// live only in <see cref="SecretReference"/>'s Key Vault entry, never here.
    /// </summary>
    public string? ConnectionMetadataJson { get; private set; }
    public bool IsEnabled { get; private set; }

    /// <summary>
    /// HIPAA #2: when true, resources routed to this destination are run through <see cref="DeIdentificationMethod"/>
    /// (Safe Harbor / k-anonymity) before delivery. Defaults false — identical behavior to before this flag existed —
    /// so no existing destination changes behavior until a tenant admin explicitly opts it in.
    /// </summary>
    public bool RequiresDeIdentification { get; private set; }

    /// <summary>The de-identification method to apply when <see cref="RequiresDeIdentification"/> is true.</summary>
    public string? DeIdentificationMethod { get; private set; }

    public void SetDeIdentificationRequirement(bool requiresDeIdentification, string? deIdentificationMethod)
    {
        RequiresDeIdentification = requiresDeIdentification;
        DeIdentificationMethod = requiresDeIdentification ? deIdentificationMethod : null;
    }

    public void Update(
        string name,
        DestinationType destinationType,
        SecretReference secretReference,
        string? target,
        string? connectionMetadataJson = null)
    {
        Name = name;
        DestinationType = destinationType;
        SecretReference = secretReference;
        Target = target;
        ConnectionMetadataJson = connectionMetadataJson;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }
}
