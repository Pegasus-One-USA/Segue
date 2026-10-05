using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FHIRBridge.Observability.Logging;

namespace FHIRBridge.Governance;

/// <summary>The PHI-scrubbed, maximally detailed view of an exception.</summary>
/// <param name="ExceptionType">Short type name of the outermost exception (matches what ErrorLogs has always stored).</param>
/// <param name="Message">Scrubbed outer message; the root cause is appended when it differs.</param>
/// <param name="Detail">Scrubbed full chain: every exception (inner and aggregate included) with type, message,
/// source, HResult, data-key names and its complete stack trace. Stored in the StackTrace column.</param>
/// <param name="RootCauseType">Type name of the deepest exception in the chain.</param>
/// <param name="RootCauseMessage">Scrubbed message of the deepest exception.</param>
public sealed record ScrubbedError(
    string ExceptionType,
    string Message,
    string Detail,
    string RootCauseType,
    string RootCauseMessage);

/// <summary>
/// The single place error text is made safe to persist or ship off-box (ErrorLogs table, Application Insights).
/// Everything that records an error goes through <see cref="Scrub"/> so the rule set cannot drift between sinks.
/// </summary>
public interface IErrorScrubber
{
    ScrubbedError Scrub(Exception exception);

    /// <summary>Scrubs arbitrary free text (messages, rendered log lines).</summary>
    string ScrubText(string? text);

    /// <summary>Scrubs a stored stack-trace / detail block with the full rule set and the larger detail length cap.
    /// Used to re-scrub rows on export, including rows captured before the central scrubber existed.</summary>
    string ScrubStackTrace(string? stackTrace);
}

/// <summary>
/// Layered, best-effort PHI/secret scrubbing. Order: the shared <see cref="IPhiRedactor"/> (key=value / JSON pairs),
/// then value-shape rules (SSN, e-mail, phone, dates, long numeric identifiers), secrets/tokens/connection strings,
/// URL query strings, database "duplicate key value" echoes, FHIR JSON name/address fields, and finally quoted
/// free-text literals. Stack frames only get the secret/identifier subset — they contain code locations, never data.
/// <para>Limits (by design): free-text names that carry no key, quote or pattern (e.g. "Patient John Smith not
/// found") cannot be recognised. The mitigation is to keep PHI out of exception messages at the throw site; this
/// is the backstop. Exception <c>Data</c> values are never emitted — keys only.</para>
/// </summary>
public sealed class ErrorScrubber : IErrorScrubber
{
    private const int MaxMessageLength = 16_000;
    private const int MaxDetailLength = 96_000;
    private const int MaxChainLength = 12;
    private const string Mask = "***";
    private const string Withheld = "[text withheld: could not be scrubbed safely]";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private sealed record Rule(Regex Pattern, string Replacement, bool AppliesToStackFrames, MatchEvaluator? Evaluator = null);

    private static Rule R(string pattern, string replacement, bool frames = false, RegexOptions extra = RegexOptions.None) =>
        new(new Regex(pattern, Opts | extra, MatchTimeout), replacement, frames);

    // The FHIR resource types whose ids are patient-linked identifiers. "Patient/eXyz123" becomes "Patient/#3f9a1c2e":
    // lines about the same resource still match each other, but the id itself never reaches a log, a stored error,
    // an export or Application Insights.
    private static readonly string[] ResourceTypes =
    [
        "Patient", "Practitioner", "PractitionerRole", "Organization", "Location", "Encounter", "Observation", "Condition",
        "Procedure", "MedicationRequest", "Medication", "MedicationStatement", "MedicationAdministration", "MedicationDispense",
        "Immunization", "AllergyIntolerance", "DiagnosticReport", "DocumentReference", "Coverage", "Claim", "ClaimResponse",
        "ExplanationOfBenefit", "CarePlan", "CareTeam", "Goal", "Device", "ServiceRequest", "Specimen", "RelatedPerson",
        "Provenance", "Appointment", "Schedule", "Slot", "Binary", "Bundle", "Composition", "QuestionnaireResponse", "Task",
        "Communication", "EpisodeOfCare", "FamilyMemberHistory", "ImagingStudy", "Account", "Person", "Group", "Consent",
        "AuditEvent", "Flag", "Basic", "Media", "NutritionOrder", "Procedure", "RiskAssessment", "SupplyDelivery",
    ];

    private static readonly HashSet<string> KnownResourceTypes = new(ResourceTypes, StringComparer.Ordinal);

    // Keyed (HMAC) so a guessable id such as Patient/123 cannot be recovered from an exported report by hashing guesses.
    // Configure a stable secret per deployment (ErrorCapture:ResourceIdTokenKey) so Api and Worker tokens match; without
    // one, each process uses its own random key (tokens then match only within one process run).
    private static byte[] _tokenKey = RandomNumberGenerator.GetBytes(32);

    public static void ConfigureTokenKey(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret))
        {
            _tokenKey = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        }
    }

    private static string Token(string id) =>
        Convert.ToHexString(HMACSHA256.HashData(_tokenKey, Encoding.UTF8.GetBytes(id)))[..8].ToLowerInvariant();

    private static string ResourceIdToken(Match match)
    {
        var type = match.Groups["type"].Value;
        var id = match.Groups["id"].Value;
        // Any CapitalisedName/id counts (HealthcareService, Endpoint, List, custom resources...), but a name outside the
        // known list needs a digit in the id, so ordinary text such as "Content/Types" is left alone.
        if (!KnownResourceTypes.Contains(type) && !id.Any(char.IsDigit))
        {
            return match.Value;
        }

        return type + "/#" + Token(id);
    }

    private const string AnyResourceType = @"[A-Z][a-z]{2,}(?:[A-Z][a-z]*)*";

    private static readonly Rule[] Rules =
    [
        // FHIR resource ids first, so no other rule sees them half-masked.
        new Rule(
            new Regex(@"\b(?<type>" + string.Join('|', ResourceTypes.Distinct()) + "|" + AnyResourceType + @")/(?<id>[A-Za-z0-9][A-Za-z0-9\-\.]{0,255})\b", Opts, MatchTimeout),
            string.Empty, true, ResourceIdToken),
        // Relative search URLs: Patient?identifier=12345 - the values are identifiers.
        new Rule(
            new Regex(@"\b(?<type>" + string.Join('|', ResourceTypes.Distinct()) + "|" + AnyResourceType + @")\?(?=[A-Za-z_.:\-]+=)[^\s""'<>]*", Opts, MatchTimeout),
            "${type}?[query-removed]", true),
        // Tokens and secrets first, so nothing below can partially mangle them.
        R(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*", "[jwt]", true),
        R(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}", "$1 " + Mask, true, RegexOptions.IgnoreCase),
        R(@"\b(password|pwd|passwd|secret|client[_-]?secret|account[_-]?key|shared[_-]?access[_-]?key|sharedaccesssignature|sig|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|authorization|token)\b(\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^;,\s&""']+)",
            "$1$2" + Mask, true, RegexOptions.IgnoreCase),
        R(@"\b(https?|ftp|sftp|mongodb(?:\+srv)?|postgres(?:ql)?|mysql|amqps?)://[^/\s:@]+:[^/\s@]+@", "$1://" + Mask + ":" + Mask + "@", true, RegexOptions.IgnoreCase),
        R(@"(\b[a-z][a-z0-9+.-]*://[^\s?#""'<>]+)\?[^\s""'<>#]*", "$1?[query-removed]", true, RegexOptions.IgnoreCase),

        // Direct identifiers by shape.
        R(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "[email]", true),
        R(@"\b\d{3}-\d{2}-\d{4}\b", "[ssn]", true),
        R(@"(?<![\w-])(?:\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]\d{3}[\s.-]\d{4}(?![\w-])", "[phone]"),
        R(@"\b(?:19|20)\d{2}-\d{2}-\d{2}\b(?!T)", "[date]"),
        R(@"\b\d{1,2}/\d{1,2}/(?:19|20)?\d{2}\b", "[date]"),
        R(@"(?<![\w-])\d{8,}(?![\w-])", "[number]"),

        // Database engines echoing the offending value.
        R(@"(duplicate key value is\s*)\([^)\r\n]*\)", "$1(" + Mask + ")", false, RegexOptions.IgnoreCase),
        R(@"(Key\s*\([^)\r\n]*\)\s*=\s*)\([^)\r\n]*\)", "$1(" + Mask + ")", false, RegexOptions.IgnoreCase),
        R(@"(Duplicate entry\s*)'[^']*'", "$1'" + Mask + "'", false, RegexOptions.IgnoreCase),
        R(@"(dup key:\s*)\{[^}]*\}", "$1{ " + Mask + " }", false, RegexOptions.IgnoreCase),

        // FHIR / JSON payload fragments that leak into upstream error bodies.
        R(@"(""(?:given|line|prefix|suffix)""\s*:\s*)\[[^\]]*\]", "$1[\"" + Mask + "\"]", false, RegexOptions.IgnoreCase),
        R(@"(""(?:family|city|district|state|postalCode|country|value|display|firstName|lastName|middleName|fullName|dob|dateOfBirth|mobile|fax)""\s*:\s*)""(?:[^""\\]|\\.)*""",
            "$1\"" + Mask + "\"", false, RegexOptions.IgnoreCase),

        // Safety net: quoted free text (contains whitespace) is far more likely a name/address than an identifier.
        // The opening quote must not follow a letter/digit, so contractions ("can't ... don't") never match.
        R(@"(?<![A-Za-z0-9])(['""])(?=[^'""\r\n]{0,200}\s)[^'""\r\n]{1,200}\1(?![A-Za-z0-9])", "$1" + Mask + "$1"),
    ];

    private readonly IPhiRedactor _redactor;

    public ErrorScrubber(IPhiRedactor? redactor = null)
    {
        _redactor = redactor ?? new PhiRedactor();
    }

    public string ScrubText(string? text) => ScrubCore(text, frameMode: false, MaxMessageLength);

    public string ScrubStackTrace(string? stackTrace) => ScrubCore(stackTrace, frameMode: false, MaxDetailLength);

    public ScrubbedError Scrub(Exception exception)
    {
        var chain = Flatten(exception);
        var root = chain[^1];

        var message = ScrubText(exception.Message);
        var rootMessage = ScrubText(root.Message);
        if (!ReferenceEquals(root, exception) && !string.Equals(message, rootMessage, StringComparison.Ordinal))
        {
            message = Truncate($"{message} (root cause: {root.GetType().Name}: {rootMessage})", MaxMessageLength);
        }

        return new ScrubbedError(
            exception.GetType().Name,
            message,
            BuildDetail(chain),
            root.GetType().Name,
            rootMessage);
    }

    private string BuildDetail(IReadOnlyList<Exception> chain)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < chain.Count; i++)
        {
            var ex = chain[i];
            sb.AppendLine(i == 0 ? $"[{i}] {ex.GetType().FullName}" : $"--- Inner exception [{i}] {ex.GetType().FullName} ---");
            sb.Append("Message: ").AppendLine(ScrubText(ex.Message));

            var facts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ex.Source)) facts.Add($"Source={ex.Source}");
            if (ex.HResult != 0) facts.Add($"HResult=0x{ex.HResult:X8}");
            if (ex is HttpRequestException { StatusCode: { } status }) facts.Add($"HttpStatus={(int)status}");
            if (ex.TargetSite?.DeclaringType is { } declaring) facts.Add($"ThrownBy={declaring.FullName}.{ex.TargetSite.Name}");
            if (facts.Count > 0) sb.AppendLine(string.Join(" | ", facts));

            if (ex.Data.Count > 0)
            {
                var keys = ex.Data.Keys.Cast<object>().Select(k => k?.ToString() ?? "?").Take(20);
                sb.Append("Data keys (values withheld): ").AppendLine(string.Join(", ", keys));
            }

            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            {
                sb.AppendLine("StackTrace:");
                sb.AppendLine(ScrubCore(ex.StackTrace, frameMode: true, MaxDetailLength));
            }
            else
            {
                sb.AppendLine("StackTrace: (none — exception was never thrown)");
            }

            if (sb.Length >= MaxDetailLength) break;
        }

        return Truncate(sb.ToString().TrimEnd(), MaxDetailLength);
    }

    /// <summary>Outermost-first list of every exception in the chain, descending through aggregates, cycle- and
    /// length-bounded. Always contains at least the supplied exception; the last item is the deepest cause.</summary>
    private static List<Exception> Flatten(Exception exception)
    {
        var result = new List<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);

        void Visit(Exception? ex)
        {
            if (ex is null || result.Count >= MaxChainLength || !seen.Add(ex)) return;
            result.Add(ex);
            if (ex is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions) Visit(inner);
            }
            else
            {
                Visit(ex.InnerException);
            }
        }

        Visit(exception);
        return result;
    }

    private string ScrubCore(string? text, bool frameMode, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        try
        {
            var s = text;
            if (!frameMode)
            {
                s = _redactor.Redact(s) ?? s;
            }

            foreach (var rule in Rules)
            {
                if (frameMode && !rule.AppliesToStackFrames) continue;
                s = rule.Evaluator is null ? rule.Pattern.Replace(s, rule.Replacement) : rule.Pattern.Replace(s, rule.Evaluator);
            }

            return Truncate(s, maxLength);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: never persist text we could not finish scrubbing.
            return Withheld;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "… [truncated]";
}
