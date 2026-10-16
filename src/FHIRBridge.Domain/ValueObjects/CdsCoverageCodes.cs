namespace FHIRBridge.Domain.ValueObjects;

/// <summary>
/// The codes of the Da Vinci CRD STU2 coverage-information extension
/// (http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information) that a coverage rule can
/// assert. All come from the CRD temporary code system http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp.
/// </summary>
public static class CdsCoverageCodes
{
    /// <summary>Sub-extension <c>covered</c> (1..1).</summary>
    public static class Covered
    {
        public const string IsCovered = "covered";
        public const string NotCovered = "not-covered";
        public const string Conditional = "conditional";

        public static readonly IReadOnlyList<string> All = [IsCovered, NotCovered, Conditional];
    }

    /// <summary>
    /// Sub-extension <c>pa-needed</c> (0..1). <c>satisfied</c> is part of the value set but needs a payer-issued
    /// satisfied-pa-id per patient, so local rules do not offer it (a relayed payer may still return it).
    /// </summary>
    public static class PaNeeded
    {
        public const string NoAuth = "no-auth";
        public const string AuthNeeded = "auth-needed";
        public const string Satisfied = "satisfied";
        public const string PerformPa = "performpa";
        public const string Conditional = "conditional";

        public static readonly IReadOnlyList<string> All = [NoAuth, AuthNeeded, Satisfied, PerformPa, Conditional];

        /// <summary>What a local rule may assert (everything but <see cref="Satisfied"/>).</summary>
        public static readonly IReadOnlyList<string> ForRules = [NoAuth, AuthNeeded, PerformPa, Conditional];
    }

    /// <summary>Sub-extension <c>doc-needed</c> (0..1).</summary>
    public static class DocNeeded
    {
        public const string Clinical = "clinical";
        public const string Admin = "admin";
        public const string Both = "both";
        public const string Conditional = "conditional";

        public static readonly IReadOnlyList<string> All = [Clinical, Admin, Both, Conditional];
    }

    /// <summary>Sub-extension <c>doc-purpose</c> (0..*): what the gathered documentation is for.</summary>
    public static class DocPurpose
    {
        public const string WithPa = "withpa";
        public const string WithClaim = "withclaim";
        public const string WithOrder = "withorder";
        public const string RetainDoc = "retain-doc";

        /// <summary>v3-NullFlavor OTH ("other"), which the CRD DocReason value set includes.</summary>
        public const string Other = "OTH";

        public static readonly IReadOnlyList<string> All = [WithPa, WithClaim, WithOrder, RetainDoc, Other];
    }

    /// <summary>Sub-extension <c>info-needed</c> (0..*): what is missing for a firm decision.</summary>
    public static class InfoNeeded
    {
        public const string Performer = "performer";
        public const string Location = "location";
        public const string Timeframe = "timeframe";
        public const string ContractWindow = "contract-window";

        /// <summary>v3-NullFlavor OTH ("other"), which the CRD informationNeeded value set includes.</summary>
        public const string Other = "OTH";

        public static readonly IReadOnlyList<string> All = [Performer, Location, Timeframe, ContractWindow, Other];
    }

    /// <summary>
    /// Code systems that have both a URL and an OID (or older URL) form. A rule stores the canonical form, and matching
    /// compares canonical forms, so "urn:oid:2.16.840.1.113883.6.12" and "http://www.ama-assn.org/go/cpt" are one system
    /// for uniqueness, import and matching alike.
    /// </summary>
    public static class CodeSystems
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["urn:oid:2.16.840.1.113883.6.12"] = "http://www.ama-assn.org/go/cpt",
            ["urn:oid:2.16.840.1.113883.6.285"] = "https://bluebutton.cms.gov/resources/codesystem/hcpcs",
            ["http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets"] = "https://bluebutton.cms.gov/resources/codesystem/hcpcs",
            ["urn:oid:2.16.840.1.113883.6.1"] = "http://loinc.org",
            ["urn:oid:2.16.840.1.113883.6.96"] = "http://snomed.info/sct",
            ["urn:oid:2.16.840.1.113883.6.88"] = "http://www.nlm.nih.gov/research/umls/rxnorm",
            ["urn:oid:2.16.840.1.113883.6.4"] = "http://www.cms.gov/Medicare/Coding/ICD10",
            ["urn:oid:2.16.840.1.113883.6.69"] = "http://hl7.org/fhir/sid/ndc",
        };

        /// <summary>The canonical form: trimmed, no trailing "/", an alias replaced by its URL. Null stays null.</summary>
        public static string? Canonical(string? system)
        {
            if (system is null)
            {
                return null;
            }

            var trimmed = system.Trim().TrimEnd('/');
            return Aliases.TryGetValue(trimmed, out var alias) ? alias : trimmed;
        }

        /// <summary>True when both name the same code system (canonical forms equal, ignoring case).</summary>
        public static bool Same(string left, string right) =>
            string.Equals(Canonical(left), Canonical(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How a rule's code is compared with an order's code.</summary>
    public static class CodeMatch
    {
        public const string Exact = "exact";
        public const string Prefix = "prefix";

        public static readonly IReadOnlyList<string> All = [Exact, Prefix];
    }
}
