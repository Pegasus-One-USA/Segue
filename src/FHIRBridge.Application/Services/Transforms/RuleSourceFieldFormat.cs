using System.Text.RegularExpressions;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// The one place that converts a mapping field's internal JsonPath into the format
/// <c>TransformationRule.SourceField</c> is actually persisted as, so every caller that resolves rules for a
/// mapped field agrees on the convention. Shared because the repository's match on SourceField is an exact
/// string comparison: a caller that skips this normalization silently resolves NO rule rather than failing
/// loudly, which is exactly how mapping-profile validation came to reject rule-backed fields.
/// </summary>
public static class RuleSourceFieldFormat
{
    private static readonly Regex ArrayIndexAnnotation = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    /// <summary>
    /// Converts a mapping field's internal JsonPath format (e.g. "$.birthDate", or "$.code.coding[*].code" for
    /// a repeating element, from MappingFieldDto.JsonPath) into the "ResourceType.field" format the portal's
    /// rule-authoring UI saves <c>TransformationRule.SourceField</c> as (see
    /// field-mapping-join-popover.component.ts's saveRule/loadRuleFor, both built from
    /// MappingRow.sources[].fhirPath) — <c>EfTransformationRuleRepository.GetFieldScopedAsync</c>'s match
    /// on SourceField is an exact string comparison, so both sides of it must agree on one convention. The UI's
    /// is the one actually persisted, so this side has to match it, not the other way around.
    ///
    /// Two normalizations, both confirmed against real saved rows: strip the leading "$." (the UI's fhirPath has
    /// none), and strip every "[...]" index/wildcard annotation (the UI's fhirPath never carries these either,
    /// e.g. "Condition.code.coding.code" — not "code.coding[*].code" — regardless of which repeating instance
    /// the field mapping itself resolves at runtime). Without the second normalization specifically, a
    /// Field-scope rule on ANY array-nested source field — codings, identifiers, telecoms, names, essentially
    /// most of FHIR — could never resolve, silently falling through to "no rule → pass the value through
    /// unchanged" for every record (reproduced: Condition.code.coding[*].code vs the saved
    /// "Condition.code.coding.code").
    /// </summary>
    public static string? FromJsonPath(string resourceType, string? jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath))
        {
            return null;
        }

        // A MULTI-SOURCE (joined) column's JsonPath is every source's path joined with "|" (see
        // JsonMappingEngine.ResolveJoinedFieldRows). The rule-authoring UI has no such concept: it saves
        // SourceField as sources[0].fhirPath alone (field-mapping-join-popover's saveRule/loadRuleFor), so the
        // first sub-path is the one both sides agree on. Normalizing the whole joined string instead produced
        // the malformed key "Patient.name.given|$.name.family" — matching no rule at all, which the repository
        // expresses as "SourceField == null || SourceField == key" and therefore filters the rule out
        // entirely. The symptom is silent: the column writes its untransformed value and nothing reports why.
        var single = jsonPath.Split('|', 2, StringSplitOptions.TrimEntries)[0];
        if (string.IsNullOrEmpty(single))
        {
            return null;
        }

        var bare = single.StartsWith("$.", StringComparison.Ordinal) ? single[2..] : single.TrimStart('$', '.');
        bare = ArrayIndexAnnotation.Replace(bare, string.Empty);

        // "$" is the whole-document path a whole-payload-as-JSON mapping carries (workflow-build-assembler-v2
        // .service.ts's toJsonPath returns it for the resource's own root node) — it leaves nothing after the
        // "$", so the interpolation below would produce a trailing-dot "Patient." that matches no persisted
        // SourceField. The portal writes the bare node id for that row ("Patient", MappingRow.childNodeId),
        // so a rule authored on a whole-node mapping would otherwise resolve to nothing at run time.
        return bare.Length == 0 ? resourceType : $"{resourceType}.{bare}";
    }
}
