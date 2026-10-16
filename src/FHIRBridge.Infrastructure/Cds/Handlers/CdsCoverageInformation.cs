using System.Globalization;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>
/// Builds and reads the Da Vinci CRD STU2 coverage-information extension
/// (<see cref="ExtensionUrl"/>, profile ext-coverage-information): sub-extensions coverage (Reference, 1..1),
/// covered (code, 1..1), pa-needed (code, 0..1), doc-needed (code, 0..1), doc-purpose (code, 0..*), info-needed
/// (code, 0..*), reason (CodeableConcept, 0..*), questionnaire (canonical, 0..*, only with doc-needed), date (date,
/// 1..1) and coverage-assertion-id (string, 1..1). Pure, never throws on odd input.
/// </summary>
internal static class CdsCoverageInformation
{
    public const string ExtensionUrl = "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information";

    /// <summary>The CRD temporary code system (card types, coverage codes).</summary>
    public const string CrdTempSystem = "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp";

    public static JsonObject Build(CdsCoverageDecision decision, string coverageReference, string assertionId, DateOnly date)
    {
        var parts = new JsonArray
        {
            Part("coverage", "valueReference", new JsonObject { ["reference"] = coverageReference }),
            Part("covered", "valueCode", JsonValue.Create(decision.Covered)),
        };

        if (decision.PaNeeded is { } pa)
        {
            parts.Add(Part("pa-needed", "valueCode", JsonValue.Create(pa)));
        }

        if (decision.DocNeeded is { } doc)
        {
            parts.Add(Part("doc-needed", "valueCode", JsonValue.Create(doc)));
            foreach (var purpose in decision.DocPurposes)
            {
                parts.Add(Part("doc-purpose", "valueCode", JsonValue.Create(purpose)));
            }
        }

        foreach (var info in decision.InfoNeeded)
        {
            parts.Add(Part("info-needed", "valueCode", JsonValue.Create(info)));
        }

        if (!string.IsNullOrWhiteSpace(decision.Reason))
        {
            parts.Add(Part("reason", "valueCodeableConcept", new JsonObject { ["text"] = decision.Reason }));
        }

        if (decision.DocNeeded is not null && !string.IsNullOrWhiteSpace(decision.QuestionnaireCanonical))
        {
            parts.Add(Part("questionnaire", "valueCanonical", JsonValue.Create(decision.QuestionnaireCanonical)));
        }

        parts.Add(Part("date", "valueDate", JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        parts.Add(Part("coverage-assertion-id", "valueString", JsonValue.Create(assertionId)));

        return new JsonObject { ["url"] = ExtensionUrl, ["extension"] = parts };
    }

    /// <summary>
    /// A copy of <paramref name="resource"/> carrying <paramref name="extension"/>: any coverage-information
    /// extension for the same coverage is replaced, everything else is kept.
    /// </summary>
    public static JsonObject Annotate(JsonObject resource, JsonObject extension, string coverageReference)
    {
        var copy = (JsonObject)resource.DeepClone();
        var kept = new JsonArray();
        if (copy["extension"] is JsonArray existing)
        {
            foreach (var item in existing)
            {
                if (item is JsonObject ext && IsCoverageInformation(ext) && CoverageOf(ext) == coverageReference)
                {
                    continue;
                }

                kept.Add(item?.DeepClone());
            }
        }

        kept.Add(extension.Parent is null ? extension : extension.DeepClone());
        copy["extension"] = kept;
        return copy;
    }

    /// <summary>
    /// True when the resource already carries coverage information for this coverage with the same covered,
    /// pa-needed and doc-needed values (CRD: no systemAction unless something is new).
    /// </summary>
    public static bool AlreadyCarries(JsonObject resource, CdsCoverageDecision decision, string coverageReference) =>
        Read(resource).Any(entry => entry.CoverageReference == coverageReference
                                     && entry.Decision.Covered == decision.Covered
                                     && entry.Decision.PaNeeded == decision.PaNeeded
                                     && entry.Decision.DocNeeded == decision.DocNeeded);

    /// <summary>Every coverage-information extension on a resource, as decisions (values outside the CRD value sets are dropped).</summary>
    public static IReadOnlyList<(string? CoverageReference, CdsCoverageDecision Decision, string? AssertionId)> Read(JsonObject? resource)
    {
        var result = new List<(string?, CdsCoverageDecision, string?)>();
        if (resource?["extension"] is not JsonArray extensions)
        {
            return result;
        }

        foreach (var ext in extensions.OfType<JsonObject>().Where(IsCoverageInformation))
        {
            var parts = (ext["extension"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            string? Code(string url) => parts.Where(p => CdsFhirReader.Text(p["url"]) == url).Select(p => CdsFhirReader.Text(p["valueCode"])).FirstOrDefault();
            IReadOnlyList<string> Codes(string url, IReadOnlyList<string> allowed) => parts
                .Where(p => CdsFhirReader.Text(p["url"]) == url)
                .Select(p => CdsFhirReader.Text(p["valueCode"]))
                .OfType<string>()
                .Where(code => allowed.Contains(code))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var covered = Code("covered");
            if (covered is null || !CdsCoverageCodes.Covered.All.Contains(covered))
            {
                continue;
            }

            var pa = Code("pa-needed");
            var doc = Code("doc-needed");
            var reason = parts.Where(p => CdsFhirReader.Text(p["url"]) == "reason")
                .Select(p => CdsFhirReader.Label(p["valueCodeableConcept"], string.Empty))
                .FirstOrDefault(text => text.Length > 0);
            var questionnaire = parts.Where(p => CdsFhirReader.Text(p["url"]) == "questionnaire")
                .Select(p => CdsFhirReader.Text(p["valueCanonical"]))
                .FirstOrDefault(text => text is not null);
            var coverage = parts.Where(p => CdsFhirReader.Text(p["url"]) == "coverage")
                .Select(p => CdsFhirReader.Text((p["valueReference"] as JsonObject)?["reference"]))
                .FirstOrDefault();
            var assertion = parts.Where(p => CdsFhirReader.Text(p["url"]) == "coverage-assertion-id")
                .Select(p => CdsFhirReader.Text(p["valueString"]))
                .FirstOrDefault();

            result.Add((coverage, new CdsCoverageDecision(
                covered,
                pa is not null && CdsCoverageCodes.PaNeeded.All.Contains(pa) ? pa : null,
                doc is not null && CdsCoverageCodes.DocNeeded.All.Contains(doc) ? doc : null,
                Codes("doc-purpose", CdsCoverageCodes.DocPurpose.All),
                Codes("info-needed", CdsCoverageCodes.InfoNeeded.All),
                reason,
                null,
                questionnaire), assertion));
        }

        return result;
    }

    /// <summary>Coverage-information extensions across the resources of <paramref name="actions"/>.</summary>
    public static int Count(IEnumerable<CdsAction>? actions) =>
        (actions ?? []).Sum(action => action.Resource?["extension"] is JsonArray extensions
            ? extensions.OfType<JsonObject>().Count(IsCoverageInformation)
            : 0);

    public static bool IsCoverageInformation(JsonObject extension) =>
        string.Equals(CdsFhirReader.Text(extension["url"]), ExtensionUrl, StringComparison.Ordinal);

    /// <summary>The "Coverage/id" the extension's <c>coverage</c> part names; null when it has none.</summary>
    public static string? CoverageOf(JsonObject extension) =>
        (extension["extension"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(p => CdsFhirReader.Text(p["url"]) == "coverage")
            .Select(p => CdsFhirReader.Text((p["valueReference"] as JsonObject)?["reference"]))
            .FirstOrDefault();

    /// <summary>Longest string value kept from a relayed extension (reason text, ids, codings).</summary>
    public const int MaxRelayedTextLength = 500;

    /// <summary>
    /// A fresh copy of a coverage-information extension from a payer (relay), keeping only what the CRD profile
    /// defines and in the right shape, or null when it is not usable. Required: <c>coverage</c> naming one of
    /// <paramref name="allowedCoverages"/>, <c>covered</c> from the value set, a valid <c>date</c> and a
    /// <c>coverage-assertion-id</c>. Kept when well formed: pa-needed, doc-needed, doc-purpose, info-needed (codes
    /// from their value sets), reason (text and codings), questionnaire (http(s) canonical, only with doc-needed),
    /// satisfied-pa-id (only with pa-needed satisfied), billingCode (codings). Anything else (other sub-extensions,
    /// nested extensions, ids, unknown value types) is dropped.
    /// </summary>
    public static JsonObject? Clean(JsonObject extension, IReadOnlySet<string> allowedCoverages)
    {
        if (!IsCoverageInformation(extension))
        {
            return null;
        }

        var parts = (extension["extension"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        IEnumerable<JsonObject> Named(string url) => parts.Where(p => CdsFhirReader.Text(p["url"]) == url);
        string? FirstCode(string url, IReadOnlyList<string> allowed) =>
            Named(url).Select(p => CdsFhirReader.Text(p["valueCode"])).FirstOrDefault(code => code is not null && allowed.Contains(code));
        string? FirstString(string url, string key) =>
            Named(url).Select(p => CdsFhirReader.Text(p[key])?.Trim()).FirstOrDefault(text => text is { Length: > 0 and <= MaxRelayedTextLength });

        var coverage = Named("coverage")
            .Select(p => CdsFhirReader.Text((p["valueReference"] as JsonObject)?["reference"]))
            .FirstOrDefault(reference => reference is not null && allowedCoverages.Contains(reference));
        var covered = FirstCode("covered", CdsCoverageCodes.Covered.All);
        var date = FirstString("date", "valueDate");
        var assertion = FirstString("coverage-assertion-id", "valueString");
        if (coverage is null || covered is null || assertion is null
            || date is null || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return null;
        }

        var clean = new JsonArray
        {
            Part("coverage", "valueReference", new JsonObject { ["reference"] = coverage }),
            Part("covered", "valueCode", JsonValue.Create(covered)),
        };

        var pa = FirstCode("pa-needed", CdsCoverageCodes.PaNeeded.All);
        if (pa is not null)
        {
            clean.Add(Part("pa-needed", "valueCode", JsonValue.Create(pa)));
        }

        var doc = FirstCode("doc-needed", CdsCoverageCodes.DocNeeded.All);
        if (doc is not null)
        {
            clean.Add(Part("doc-needed", "valueCode", JsonValue.Create(doc)));
        }

        foreach (var url in new[] { "doc-purpose", "info-needed" })
        {
            var allowed = url == "doc-purpose" ? CdsCoverageCodes.DocPurpose.All : CdsCoverageCodes.InfoNeeded.All;
            if (url == "doc-purpose" && doc is null)
            {
                continue;
            }

            foreach (var code in Named(url).Select(p => CdsFhirReader.Text(p["valueCode"])).OfType<string>()
                         .Where(allowed.Contains).Distinct(StringComparer.Ordinal))
            {
                clean.Add(Part(url, "valueCode", JsonValue.Create(code)));
            }
        }

        foreach (var reason in Named("reason").Select(p => CleanConcept(p["valueCodeableConcept"] as JsonObject)).OfType<JsonObject>().Take(5))
        {
            clean.Add(Part("reason", "valueCodeableConcept", reason));
        }

        if (doc is not null)
        {
            foreach (var questionnaire in Named("questionnaire").Select(p => CdsFhirReader.Text(p["valueCanonical"])?.Trim())
                         .OfType<string>().Where(IsHttpCanonical).Distinct(StringComparer.Ordinal).Take(5))
            {
                clean.Add(Part("questionnaire", "valueCanonical", JsonValue.Create(questionnaire)));
            }
        }

        if (pa == CdsCoverageCodes.PaNeeded.Satisfied && FirstString("satisfied-pa-id", "valueString") is { } paId)
        {
            clean.Add(Part("satisfied-pa-id", "valueString", JsonValue.Create(paId)));
        }

        foreach (var billing in Named("billingCode").Select(p => CleanCoding(p["valueCoding"] as JsonObject)).OfType<JsonObject>().Take(10))
        {
            clean.Add(Part("billingCode", "valueCoding", billing));
        }

        clean.Add(Part("date", "valueDate", JsonValue.Create(date)));
        clean.Add(Part("coverage-assertion-id", "valueString", JsonValue.Create(assertion)));
        return new JsonObject { ["url"] = ExtensionUrl, ["extension"] = clean };
    }

    private static bool IsHttpCanonical(string value) =>
        value.Length <= MaxRelayedTextLength
        && Uri.TryCreate(value.Split('|')[0], UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static JsonObject? CleanConcept(JsonObject? concept)
    {
        if (concept is null)
        {
            return null;
        }

        var result = new JsonObject();
        var codings = (concept["coding"] as JsonArray ?? []).OfType<JsonObject>().Select(CleanCoding).OfType<JsonObject>().Take(5).ToArray();
        if (codings.Length > 0)
        {
            result["coding"] = new JsonArray(codings.Select(c => (JsonNode)c).ToArray());
        }

        if (CdsFhirReader.Text(concept["text"])?.Trim() is { Length: > 0 } text)
        {
            result["text"] = text.Length > MaxRelayedTextLength ? text[..MaxRelayedTextLength] : text;
        }

        return result.Count > 0 ? result : null;
    }

    private static JsonObject? CleanCoding(JsonObject? coding)
    {
        var code = CdsFhirReader.Text(coding?["code"])?.Trim();
        if (coding is null || code is not { Length: > 0 and <= 100 })
        {
            return null;
        }

        var result = new JsonObject();
        if (CdsFhirReader.Text(coding["system"])?.Trim() is { Length: > 0 and <= MaxRelayedTextLength } system)
        {
            result["system"] = system;
        }

        result["code"] = code;
        if (CdsFhirReader.Text(coding["display"])?.Trim() is { Length: > 0 } display)
        {
            result["display"] = display.Length > MaxRelayedTextLength ? display[..MaxRelayedTextLength] : display;
        }

        return result;
    }

    private static JsonObject Part(string url, string valueKey, JsonNode? value) => new() { ["url"] = url, [valueKey] = value };
}
