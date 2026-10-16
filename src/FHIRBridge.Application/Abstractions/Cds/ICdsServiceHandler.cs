using FHIRBridge.Application.DTOs.Cds;

namespace FHIRBridge.Application.Abstractions.Cds;

/// <summary>
/// A built-in CDS handler TYPE (e.g. drug-allergy). Every implementation is registered in DI and found through
/// <see cref="ICdsServiceHandlerRegistry"/> by <see cref="Key"/>, never by a switch. Admins create any number of
/// configured instances (CdsServiceInstance) of one type, each with its own settings.
/// <para>
/// A handler reads only the request's context and prefetch (the evaluator fills missing prefetch through the FHIR
/// callback before the handler runs; handlers never do I/O), returns cards (with suggestions only for fix types the
/// instance switched on), and must never log or keep anything from the request.
/// </para>
/// </summary>
public interface ICdsServiceHandler
{
    /// <summary>Stable key stored on instances, e.g. <see cref="CdsHandlerKeys.DrugAllergy"/>.</summary>
    string Key { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>The hooks this handler can answer, e.g. <see cref="CdsHookNames.OrderSign"/>.</summary>
    IReadOnlyList<string> SupportedHooks { get; }

    /// <summary>The settings an instance of this handler takes, in display order.</summary>
    IReadOnlyList<CdsSettingDescriptor> Settings { get; }

    /// <summary>
    /// Prefetch key → FHIR query template advertised in discovery for an instance on <paramref name="hook"/>
    /// (e.g. "allergies" → "AllergyIntolerance?patient={{context.patientId}}"). Every key is one the handler
    /// needs; an empty dictionary means the handler uses the context alone.
    /// </summary>
    IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook);

    /// <summary>
    /// Prefetch key → FHIR query template the evaluator runs against the request's <c>fhirServer</c> when that key
    /// is missing from prefetch (Epic does not read discovery, so the health system may configure less prefetch).
    /// Same template syntax as <see cref="GetPrefetchTemplates"/>; usually the same queries. Keys must be a subset of
    /// the prefetch keys. The handler never fetches: the evaluator (through <see cref="ICdsFhirCallbackClient"/>)
    /// fills <c>request.prefetch</c> before <see cref="EvaluateAsync"/> runs.
    /// </summary>
    IReadOnlyDictionary<string, string> GetFallbackQueries(string hook);

    /// <summary>
    /// The one-click fixes this handler can put on its cards, in display order. Empty when it offers none.
    /// Admins switch each on per instance (all off by default) and supply its code mappings.
    /// </summary>
    IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes { get; }

    /// <summary>
    /// True when the rules use the illustrative SAMPLE reference data shipped with Segue (every built-in handler
    /// does). The go-live readiness check then asks for a clinical sign-off before real use. Default false.
    /// </summary>
    bool UsesSampleReferenceData => false;

    /// <summary>
    /// True only for a handler allowed to return CDS Hooks systemActions (the coverage requirements / Da Vinci CRD
    /// handler, which annotates the request's own orders). The evaluator drops systemActions from every other
    /// handler. Default false.
    /// </summary>
    bool ReturnsSystemActions => false;

    /// <summary>
    /// A complete, synthetic CDS request for <paramref name="hook"/> (context and prefetch filled with made-up
    /// data that triggers at least one card with default settings), shown on the admin Test screen.
    /// </summary>
    CdsRequest BuildSampleRequest(string hook);

    Task<CdsHandlerResult> EvaluateAsync(CdsHandlerContext context, CancellationToken cancellationToken);
}

/// <summary>Finds a handler type by key. Built over every DI-registered <see cref="ICdsServiceHandler"/>.</summary>
public interface ICdsServiceHandlerRegistry
{
    /// <summary>Every handler type, ordered by display name.</summary>
    IReadOnlyList<ICdsServiceHandler> All { get; }

    ICdsServiceHandler? Find(string? key);
}

/// <summary>Keys of the built-in handler types.</summary>
public static class CdsHandlerKeys
{
    public const string DrugAllergy = "drug-allergy";
    public const string DrugInteraction = "drug-interaction";
    public const string RenalDose = "renal-dose";
    public const string CareGap = "care-gap";
    public const string PriorAuth = "prior-auth";

    /// <summary>Da Vinci Coverage Requirements Discovery (CRD): payer coverage, prior auth and documentation rules.</summary>
    public const string CoverageRequirements = "coverage-requirements";
}

/// <summary>CDS Hooks hook names used by the built-in handlers.</summary>
public static class CdsHookNames
{
    public const string PatientView = "patient-view";
    public const string OrderSelect = "order-select";
    public const string OrderSign = "order-sign";

    // Da Vinci CRD hooks (only the coverage-requirements handler answers these).
    public const string AppointmentBook = "appointment-book";
    public const string EncounterStart = "encounter-start";
    public const string EncounterDischarge = "encounter-discharge";
    public const string OrderDispatch = "order-dispatch";
}

/// <summary>
/// One field of a handler's settings form. <see cref="Type"/> is one of <see cref="CdsSettingTypes"/>;
/// <see cref="DefaultValue"/> is the JSON value used when an instance leaves the field unset (a number, string,
/// boolean, or for "list" an array of strings).
/// </summary>
public sealed record CdsSettingDescriptor(
    string Key,
    string Label,
    string Type,
    System.Text.Json.Nodes.JsonNode? DefaultValue,
    string? Help = null,
    IReadOnlyList<CdsSettingOption>? Options = null,
    bool Required = false,
    double? Min = null,
    double? Max = null);

/// <summary>One choice of a "select" setting.</summary>
public sealed record CdsSettingOption(string Value, string Label);

public static class CdsSettingTypes
{
    public const string Number = "number";
    public const string Text = "text";
    public const string Select = "select";
    public const string Boolean = "boolean";

    /// <summary>A list of strings (e.g. codes).</summary>
    public const string List = "list";

    public static readonly IReadOnlyList<string> All = [Number, Text, Select, Boolean, List];
}

/// <summary>
/// One kind of one-click fix a handler can offer (becomes a CDS Hooks suggestion with exactly one action).
/// </summary>
/// <param name="Key">Stable key stored in the instance's fix settings, e.g. <see cref="CdsFixTypeKeys.RemoveDraftOrder"/>.</param>
/// <param name="Label">Shown in the portal, e.g. "Remove the draft order".</param>
/// <param name="ActionType"><see cref="DTOs.Cds.CdsActionTypes.Create"/> or <see cref="DTOs.Cds.CdsActionTypes.Delete"/>.</param>
/// <param name="ResourceType">The FHIR resource the action creates or deletes: MedicationRequest, ServiceRequest or Condition.</param>
/// <param name="MappingFields">
/// The code-mapping columns the admin fills in (keys from <see cref="CdsFixMappingFieldKeys"/>). Empty for a fix
/// that needs no code (a delete).
/// </param>
/// <param name="RequiresMapping">True when the fix cannot be offered until at least one mapping is saved.</param>
/// <param name="Triggers">
/// What a mapping can be keyed by (e.g. drug classes, care gaps). Null when the fix takes one mapping for every case
/// (the mapping's trigger is then left empty).
/// </param>
/// <param name="CodeSystems">The code systems the admin may pick for the mapping (first is the default).</param>
/// <param name="Help">Shown under the fix in the portal.</param>
public sealed record CdsFixTypeDescriptor(
    string Key,
    string Label,
    string ActionType,
    string ResourceType,
    IReadOnlyList<CdsFixMappingField> MappingFields,
    bool RequiresMapping,
    IReadOnlyList<CdsSettingOption>? Triggers = null,
    IReadOnlyList<CdsSettingOption>? CodeSystems = null,
    string? Help = null);

/// <summary>One column of a fix type's code-mapping table.</summary>
public sealed record CdsFixMappingField(string Key, string Label, bool Required, string? Help = null);

/// <summary>The code-mapping columns (each maps to a member of the Domain CdsFixCodeMapping).</summary>
public static class CdsFixMappingFieldKeys
{
    public const string Trigger = "trigger";
    public const string System = "system";
    public const string Code = "code";
    public const string Display = "display";

    public static readonly IReadOnlyList<string> All = [Trigger, System, Code, Display];
}

/// <summary>Keys of the built-in fix types.</summary>
public static class CdsFixTypeKeys
{
    /// <summary>delete MedicationRequest/ServiceRequest: removes the draft order the card is about.</summary>
    public const string RemoveDraftOrder = "remove-draft-order";

    /// <summary>create MedicationRequest: adds the mapped alternative drug (preference-list item, RxNorm or NDC).</summary>
    public const string AddAlternativeMedication = "add-alternative-medication";

    /// <summary>create ServiceRequest: adds the mapped test / procedure order (preference-list or order set item).</summary>
    public const string AddTestOrder = "add-test-order";

    /// <summary>create Condition, category problem-list-item (ICD-10-CM, CMS-HCC or HHS-HCC).</summary>
    public const string AddProblem = "add-problem";

    /// <summary>create Condition, category encounter-diagnosis (only when the request has an encounterId).</summary>
    public const string AddEncounterDiagnosis = "add-encounter-diagnosis";
}

/// <summary>What a handler's <see cref="ICdsServiceHandler.EvaluateAsync"/> produced.</summary>
/// <param name="Cards">Advice cards (may be empty).</param>
/// <param name="DegradedReason">
/// Set when the handler could not evaluate fully (e.g. a prefetch entry was null) and answered with fewer or no
/// cards. A short, non-PHI sentence; it is written to the CDS call log.
/// </param>
/// <param name="SystemActions">
/// systemActions (coverage requirements only, see <see cref="ICdsServiceHandler.ReturnsSystemActions"/>); null for none.
/// </param>
/// <param name="SourceLabel">
/// The card <c>source.label</c> to use instead of the instance's (e.g. "&lt;payer&gt; via Segue" for cards relayed
/// from a payer's CRD service); null keeps the instance's label.
/// </param>
public sealed record CdsHandlerResult(
    IReadOnlyList<CdsCard> Cards,
    string? DegradedReason = null,
    IReadOnlyList<CdsAction>? SystemActions = null,
    string? SourceLabel = null)
{
    public static CdsHandlerResult Empty { get; } = new([]);
}
