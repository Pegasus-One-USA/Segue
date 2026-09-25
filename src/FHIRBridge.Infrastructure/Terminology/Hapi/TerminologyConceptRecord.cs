namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// One concept as parsed from a source release, carrying the per-code fields the 13 sources actually
/// publish beyond code+display. Replaces the old <c>(string Code, string Display)</c> tuple that every
/// Hapi*TerminologySyncService passed to <see cref="HapiLocalTerminologyWriter"/>, which structurally
/// could not carry a second description or a status signal even for the sources that publish both.
///
/// Field availability genuinely varies by source (verified against real downloads of all 13): LOINC
/// publishes SHORTNAME + LONG_COMMON_NAME + a four-valued STATUS, while UCUM publishes a unit name and
/// nothing else. Anything a source does not publish stays null, and <see cref="IsActive"/> defaults to
/// true so a source with no status or expiry signal is treated as wholly in force.
/// </summary>
/// <param name="Code">The concept's code, as published by the source.</param>
/// <param name="Display">The primary display text — unchanged from what each sync service already chose,
/// so existing lookups keep resolving exactly the same string as before this type was introduced.</param>
/// <param name="ShortDescription">The source's abbreviated description, where distinct from the long form.</param>
/// <param name="LongDescription">The source's full-length description.</param>
/// <param name="LongCommonName">LOINC's LONG_COMMON_NAME and its per-source equivalents.</param>
/// <param name="IsActive">Computed at parse time from the source's own status/expiry signal; true when
/// the source publishes no such signal.</param>
public sealed record TerminologyConceptRecord(
    string Code,
    string? Display,
    string? ShortDescription = null,
    string? LongDescription = null,
    string? LongCommonName = null,
    bool IsActive = true);
