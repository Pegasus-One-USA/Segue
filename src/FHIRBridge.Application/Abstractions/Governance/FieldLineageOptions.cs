namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// Binds the "FieldLineage" configuration section. Off by default: field-level lineage writes one row per mapped
/// field per resource (O(fields × resources), not O(resources) like the resource-level lineage trail), so it's an
/// opt-in diagnostic tool for "why does this destination column hold this value" investigations, not an always-on
/// production trail.
/// </summary>
public sealed class FieldLineageOptions
{
    public const string SectionName = "FieldLineage";

    public bool Enabled { get; set; }
}
