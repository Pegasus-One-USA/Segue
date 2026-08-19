using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A named, reusable group of pre-mapping de-identification rules (<see cref="TransformationRule"/> rows with
/// <see cref="Enums.TransformExecutionPhase.PreMapping"/>). Destinations opt into de-identification by pointing
/// at one profile via <see cref="DestinationConfiguration.DeIdentificationProfileId"/> — different destinations
/// can select different profiles, so redaction depth is no longer one-size-fits-all for a tenant.
/// </summary>
public sealed class DeIdentificationProfile : AuditableEntity<Guid>, IHasAuditDisplayName
{
    /// <summary>
    /// Fixed id for the seeded "HIPAA Safe Harbor — Default" profile (see <c>DeIdentificationProfileSeeder</c>).
    /// The Runtime workflow-builder canvas's "De-identification" node (<c>DeIdentificationNodeExecutor</c>) has
    /// no config UI for picking a profile, so it falls back to this constant when none is set — preserving the
    /// unconditional Safe Harbor redaction that node always applied before profiles existed, rather than
    /// silently passing PHI through unredacted the moment a profile became required to redact anything at all.
    /// </summary>
    public static readonly Guid DefaultProfileId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private DeIdentificationProfile()
    {
    }

    /// <param name="id">Pins the id — used only by the seeder so <see cref="DefaultProfileId"/> is a stable,
    /// referenceable constant rather than a random id nothing else could know in advance. Omit for any
    /// tenant-created profile.</param>
    public DeIdentificationProfile(string name, string? description = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        Description = description;
    }

    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }

    string? IHasAuditDisplayName.AuditDisplayName => Name;

    public void Update(string name, string? description)
    {
        Name = name;
        Description = description;
    }
}
