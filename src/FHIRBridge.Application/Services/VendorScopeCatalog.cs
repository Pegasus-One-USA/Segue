using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services;

/// <summary>
/// A vendor's real <c>system/</c> scope vocabulary, for vendors that do NOT accept the uniform
/// <c>system/{Resource}.{read|rs}</c> shape <see cref="ScopeGeneratorService"/> otherwise emits.
/// <para>
/// Two things vary per vendor and cannot be expressed by a single scope-version suffix:
/// the <b>access level</b> a given resource's read scope is spelled with, and <b>which resource types have a
/// system read scope at all</b>. A vendor that rejects the entire token request on one unrecognized scope (eCW and
/// athenahealth both do) turns either mismatch into a total authentication failure, not a partial result.
/// </para>
/// </summary>
public sealed class VendorScopeProfile
{
    public VendorScopeProfile(
        IReadOnlyDictionary<string, string> readAccessLevelByResourceType,
        string wildcardReadAccessLevel)
    {
        ReadAccessLevelByResourceType = readAccessLevelByResourceType;
        WildcardReadAccessLevel = wildcardReadAccessLevel;
    }

    /// <summary>Access level (<c>read</c> / <c>rs</c> / <c>r</c>) this vendor spells each resource type's system
    /// read scope with. A resource type absent from this map has no system read scope on this vendor at all and is
    /// excluded from the generated scope string entirely (and reported in
    /// <c>GeneratedScopesDto.UnsupportedScopes</c>) rather than requested and rejected.</summary>
    public IReadOnlyDictionary<string, string> ReadAccessLevelByResourceType { get; }

    /// <summary>Access level for this vendor's <c>system/*</c> wildcard — the fallback when a source has no
    /// resource types configured at all.</summary>
    public string WildcardReadAccessLevel { get; }

    public bool TryGetReadAccessLevel(string resourceType, out string accessLevel) =>
        ReadAccessLevelByResourceType.TryGetValue(resourceType, out accessLevel!);
}

/// <summary>
/// Registry of the per-vendor scope profiles above. A vendor with no entry keeps the uniform version-suffix
/// behaviour unchanged — so adding a vendor here is additive and cannot affect any other vendor.
/// </summary>
public static class VendorScopeCatalog
{
    /// <summary>
    /// eClinicalWorks (Healow), derived from the practice's own live
    /// <c>/.well-known/smart-configuration</c> <c>scopes_supported</c> (165 <c>system/</c> scopes on the
    /// FFBJCD staging sandbox, captured 2026-09-02). eCW's vocabulary is NOT the generic SMART one:
    /// <list type="bullet">
    ///   <item>Most resources expose the SMARTv1 coarse <c>.read</c>.</item>
    ///   <item>Nine expose ONLY the SMARTv2 short form <c>.r</c> — no <c>.read</c> and no <c>.rs</c>:
    ///   Binary, Claim, Coverage, Media, MedicationDispense, QuestionnaireResponse, RelatedPerson,
    ///   <b>ServiceRequest</b> and Specimen. ServiceRequest is in FHIRBridge's own MVP1 resource set, so the
    ///   uniform <c>.read</c> suffix produced <c>system/ServiceRequest.read</c> and eCW failed the whole token
    ///   request with <c>invalid_grant</c>.</item>
    ///   <item>The only system wildcard is <c>system/*.r</c> — <c>system/*.read</c> does not exist.</item>
    ///   <item>Resource types eCW advertises no system read scope for (Appointment, Schedule, Slot, Task,
    ///   Consent, ... and FamilyMemberHistory, which is create/update only) are absent from the map below and so
    ///   are never requested.</item>
    /// </list>
    /// <c>Group</c> is deliberately omitted even though eCW advertises <c>system/Group.read</c>: eCW's Backend
    /// Authentication guide requires <c>system/Group.read</c> for bulk (Group <c>$export</c>) requests and
    /// requires it to be EXCLUDED from Backend Single Patient calls, which is the only eCW backend flow
    /// FHIRBridge supports. Re-add it here if eCW bulk export is ever implemented.
    /// </summary>
    private static readonly VendorScopeProfile EClinicalWorks = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AllergyIntolerance"] = "read",
            ["Basic"] = "read",
            ["Binary"] = "r",
            ["CarePlan"] = "read",
            ["CareTeam"] = "read",
            ["ChargeItem"] = "read",
            ["Claim"] = "r",
            ["Condition"] = "read",
            ["Coverage"] = "r",
            ["Device"] = "read",
            ["DiagnosticReport"] = "read",
            ["DocumentReference"] = "read",
            ["Encounter"] = "read",
            ["Goal"] = "read",
            ["Immunization"] = "read",
            ["Location"] = "read",
            ["Media"] = "r",
            ["Medication"] = "read",
            ["MedicationAdministration"] = "read",
            ["MedicationDispense"] = "r",
            ["MedicationRequest"] = "read",
            ["Observation"] = "read",
            ["Organization"] = "read",
            ["Patient"] = "read",
            ["Practitioner"] = "read",
            ["PractitionerRole"] = "read",
            ["Procedure"] = "read",
            ["Provenance"] = "read",
            ["Questionnaire"] = "read",
            ["QuestionnaireResponse"] = "r",
            ["RelatedPerson"] = "r",
            ["ServiceRequest"] = "r",
            ["Specimen"] = "r",
        },
        wildcardReadAccessLevel: "r");

    private static readonly IReadOnlyDictionary<SourceSystemType, VendorScopeProfile> ProfilesByVendor =
        new Dictionary<SourceSystemType, VendorScopeProfile>
        {
            [SourceSystemType.Healow] = EClinicalWorks,
        };

    /// <summary>The vendor's scope profile, or null when it accepts the uniform version-suffix shape (every
    /// vendor except the ones registered above).</summary>
    public static VendorScopeProfile? For(SourceSystemType? vendor) =>
        vendor is { } value && ProfilesByVendor.TryGetValue(value, out var profile) ? profile : null;
}
