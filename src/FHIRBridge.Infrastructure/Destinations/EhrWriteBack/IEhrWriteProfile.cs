using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>
/// Turns a source FHIR resource into exactly what one vendor write API accepts, or says why it cannot be sent. One
/// implementation per (vendor, resource type), resolved through <see cref="EhrWriteProfileRegistry"/>, never a
/// switch. Profiles build their output from an allow-list in a fixed property order: the content hash in the ledger
/// depends on that order.
/// </summary>
public interface IEhrWriteProfile
{
    SourceSystemType Vendor { get; }

    string ResourceType { get; }

    /// <summary>Shapes <paramref name="source"/>. Never mutates it. References in the output still point at the
    /// source system until <see cref="BindReferences"/> runs.</summary>
    EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options);

    /// <summary>Points the shaped resource at the EHR's own patient and, when the API needs one, encounter.</summary>
    void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId);
}

public enum EhrShapeOutcome
{
    Shaped = 1,

    /// <summary>A record this API is not meant to receive (inactive, another variant, entered in error). Not an
    /// error: counted under its reason and left out.</summary>
    Skipped = 2,

    /// <summary>A record the API should receive but whose data is incomplete or unusable.</summary>
    Rejected = 3,
}

/// <param name="Reason">A short PHI-free reason code, e.g. "missing-code". Null when shaped.</param>
/// <param name="SourcePatientReference">The source system's reference to the record's patient.</param>
/// <param name="SourceEncounterReference">The source system's reference to the record's encounter, if any.</param>
public sealed record EhrShapeResult(
    EhrShapeOutcome Outcome,
    JsonObject? Resource,
    string? Reason,
    string? SourcePatientReference,
    string? SourceEncounterReference)
{
    public static EhrShapeResult Shaped(JsonObject resource, string? sourcePatientReference, string? sourceEncounterReference = null) =>
        new(EhrShapeOutcome.Shaped, resource, null, sourcePatientReference, sourceEncounterReference);

    public static EhrShapeResult Skip(string reason) => new(EhrShapeOutcome.Skipped, null, reason, null, null);

    public static EhrShapeResult Reject(string reason) => new(EhrShapeOutcome.Rejected, null, reason, null, null);
}

/// <summary>Registry of write profiles by (vendor, resource type), built from every <see cref="IEhrWriteProfile"/>
/// registered in DI. Two profiles for the same key fail at construction rather than one silently winning.</summary>
public sealed class EhrWriteProfileRegistry
{
    private readonly IReadOnlyDictionary<(SourceSystemType Vendor, string ResourceType), IEhrWriteProfile> _profiles;

    public EhrWriteProfileRegistry(IEnumerable<IEhrWriteProfile> profiles)
    {
        var map = new Dictionary<(SourceSystemType, string), IEhrWriteProfile>();
        foreach (var profile in profiles)
        {
            if (!map.TryAdd((profile.Vendor, profile.ResourceType), profile))
            {
                throw new InvalidOperationException(
                    $"Two EHR write profiles are registered for {profile.Vendor} {profile.ResourceType}.");
            }
        }

        _profiles = map;
    }

    public IEhrWriteProfile? Find(SourceSystemType vendor, string resourceType) =>
        _profiles.TryGetValue((vendor, resourceType), out var profile) ? profile : null;
}
