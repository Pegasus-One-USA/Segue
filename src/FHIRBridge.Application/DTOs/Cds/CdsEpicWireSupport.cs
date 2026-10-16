using System.Text.Json;
using System.Text.Json.Nodes;

namespace FHIRBridge.Application.DTOs.Cds;

/// <summary>Epic's CDS Hooks extension keys (Epic CDS Hooks tutorial on fhir.epic.com).</summary>
public static class CdsEpicExtensions
{
    // Request (CdsRequest.Extension)
    public const string RequestBpaTriggerAction = "com.epic.cdshooks.request.bpa-trigger-action";
    public const string RequestSpecificationVersion = "com.epic.cdshooks.request.cds-hooks-specification-version";
    public const string RequestFhirVersion = "com.epic.cdshooks.request.fhir-version";
    public const string RequestCriteriaId = "com.epic.cdshooks.request.criteria-id";
    public const string RequestEpicVersion = "com.epic.cdshooks.request.epic-version";
    public const string RequestImplementationVersion = "com.epic.cdshooks.request.cds-hooks-implementation-version";
    public const string RequestSuVersion = "com.epic.cdshooks.request.cds-hooks-su-version";

    // Card (CdsCard.Extension): "text/plain", "text/html", ...; absent means markdown.
    public const string CardDetailContentType = "com.epic.cdshooks.card.detail.content-type";

    // Feedback (CdsOverrideReason.Extension)
    public const string FeedbackReasonCategory = "com.epic.cdshooks.feedback.overrideReason.reasonCategory";
    public const string FeedbackReasonDisplay = "com.epic.cdshooks.feedback.overrideReason.reasonDisplay";
    public const string FeedbackReasonTitle = "com.epic.cdshooks.feedback.overrideReason.reasonTitle";
}

/// <summary>Code systems a fix uses in the resources it asks the EHR to create.</summary>
public static class CdsFixCodeSystems
{
    /// <summary>Epic preference-list item (MedicationRequest, ServiceRequest, ProcedureRequest).</summary>
    public const string EpicPreferenceListItem = "urn:com.epic.cdshooks.action.code.system.preference-list-item";

    /// <summary>Epic order set item (ServiceRequest).</summary>
    public const string EpicOrderSetItem = "urn:com.epic.cdshooks.action.code.system.orderset-item";

    /// <summary>Epic CMS-HCC (Condition).</summary>
    public const string EpicCmsHcc = "urn:com.epic.cdshooks.action.code.system.cms-hcc";

    /// <summary>Epic HHS-HCC (Condition).</summary>
    public const string EpicHhsHcc = "urn:com.epic.cdshooks.action.code.system.hhs-hcc";

    /// <summary>ICD-10-CM (Condition).</summary>
    public const string Icd10Cm = "urn:oid:2.16.840.1.113883.6.90";

    /// <summary>NDC (MedicationRequest).</summary>
    public const string Ndc = "urn:oid:2.16.840.1.113883.6.69";

    /// <summary>RxNorm as an OID (MedicationRequest; Epic's form).</summary>
    public const string RxNormOid = "urn:oid:2.16.840.1.113883.6.88";
}

/// <summary>
/// The non-PHI metadata Segue records from a request's vendor extensions (Epic). Values are kept only when they
/// are short tokens (letters, digits, '.', '-', '_'); anything else is dropped rather than stored.
/// </summary>
public sealed record CdsRequestMetadata(
    string? TriggerAction,
    string? EpicVersion,
    string? CriteriaId,
    string? ImplementationVersion,
    string? SpecificationVersion,
    string? FhirVersion,
    string? SuVersion)
{
    public static CdsRequestMetadata Empty { get; } = new(null, null, null, null, null, null, null);

    public static CdsRequestMetadata Read(CdsRequest? request)
    {
        var extension = request?.Extension;
        if (extension is null)
        {
            return Empty;
        }

        return new CdsRequestMetadata(
            SafeToken(Value(extension, CdsEpicExtensions.RequestBpaTriggerAction), 20),
            SafeToken(Value(extension, CdsEpicExtensions.RequestEpicVersion), 50),
            SafeToken(Value(extension, CdsEpicExtensions.RequestCriteriaId), 100),
            SafeToken(Value(extension, CdsEpicExtensions.RequestImplementationVersion), 50),
            SafeToken(Value(extension, CdsEpicExtensions.RequestSpecificationVersion), 50),
            SafeToken(Value(extension, CdsEpicExtensions.RequestFhirVersion), 50),
            SafeToken(Value(extension, CdsEpicExtensions.RequestSuVersion), 50));
    }

    /// <summary>The value trimmed when it is 1..<paramref name="maxLength"/> of letters, digits, '.', '-', '_'; else null.</summary>
    public static string? SafeToken(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength)
        {
            return null;
        }

        return trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? trimmed : null;
    }

    /// <summary>A string or number value (Epic sends some as numbers); arrays/objects give null.</summary>
    private static string? Value(JsonObject extension, string key) =>
        extension[key] is JsonValue value && value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : null;
}

/// <summary>
/// Card topic codes (<c>source.topic</c>). Epic ties a clinician's override to the topic code, so each rule keeps
/// one static code: letters and digits only, at most 100 characters.
/// </summary>
public static class CdsTopic
{
    public const int CodeMaxLength = 100;
    public const string System = "urn:segue:cds:topic";

    public static bool IsValidCode(string? code) =>
        code is { Length: > 0 and <= CodeMaxLength } && code.All(char.IsAsciiLetterOrDigit);

    /// <exception cref="ArgumentException">The code is not alphanumeric or is longer than 100 characters (a programming error).</exception>
    public static CdsCoding Create(string code, string? display = null)
    {
        if (!IsValidCode(code))
        {
            throw new ArgumentException("A CDS topic code must be 1-100 letters or digits.", nameof(code));
        }

        return new CdsCoding { System = System, Code = code, Display = display };
    }
}
