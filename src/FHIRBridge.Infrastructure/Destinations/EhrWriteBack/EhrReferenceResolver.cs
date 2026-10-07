using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Fhir;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>How a source patient maps onto the EHR.</summary>
public enum EhrPatientResolutionKind
{
    /// <summary>An existing EHR patient; <see cref="EhrPatientResolution.TargetPatientId"/> is set.</summary>
    Resolved = 1,

    /// <summary>The EHR has no such patient and the destination opted into creating one.</summary>
    WouldCreate = 2,

    /// <summary>No safe answer; <see cref="EhrPatientResolution.Reason"/> says why.</summary>
    Unresolved = 3,
}

/// <param name="Method">How it was resolved: "ledger", "identifier", "match", "clone" or "created".</param>
public sealed record EhrPatientResolution(
    EhrPatientResolutionKind Kind,
    string? TargetPatientId,
    string? Reason,
    string? Method = null,
    JsonObject? ShapedPatient = null);

/// <summary>
/// Resolves a source record's patient and encounter to records that already exist in the EHR. Only certain answers
/// are used: a wrong-patient write is a patient-safety incident, so there is no fuzzy matching anywhere.
///
/// <para>Patients, in order: the ledger
/// (a patient written or resolved before); identifier search (exactly one hit); <c>Patient/$match</c> with
/// certain-only matching. <c>$match</c> needs phone and address as well as name, birth date and gender to reach a
/// certain match, so the source patient comes from the batch or, failing that, from the source system. A vendor with no
/// certain-only <c>$match</c> (eClinicalWorks, athenahealth) asks the master patient index instead
/// (<see cref="IEhrTargetPatientMatcher"/>); with no index available the patient waits as
/// <c>patient-awaiting-mpi</c>.</para>
///
/// <para>One resolver serves one write call and caches every answer, so a batch of 200 observations for one patient
/// makes one set of calls.</para>
///
/// <para><b>Clone mode</b> never searches or matches: the real patient is exactly what must not be written to. A source
/// patient resolves to its clone through the ledger (clone-keyed rows), or else to a new clone to create
/// (<see cref="EhrClonePatient"/>).</para>
///
/// <para><b>A test run</b> (a Generic FHIR server standing in for a vendor) resolves by ledger and identifier only. A
/// test server has neither the vendor's <c>$match</c> nor the MPI, and holds only test data, so a patient its identifier
/// search does not find is one it does not have.</para>
/// </summary>
public sealed class EhrReferenceResolver
{
    private const int MaxIdentifierSearches = 3;

    // Epic's FHIR-id identifier systems describe a record in the SOURCE Epic; searching another EHR by them only
    // spends the search budget before an SSN or MRN is tried.
    private static readonly HashSet<string> SourceOnlyIdentifierSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://open.epic.com/FHIR/StructureDefinition/patient-fhir-id",
        "http://open.epic.com/FHIR/StructureDefinition/patient-dstu2-fhir-id",
    };

    private readonly IEhrWriteChannel _channel;
    private readonly IEhrWriteLedgerRepository _ledger;
    private readonly IEhrWriteProfile? _patientProfile;
    private readonly EhrWriteVendorProfile _vendor;
    private readonly string _targetKey;
    private readonly string? _sourceBaseUrl;
    private readonly IReadOnlyDictionary<string, JsonObject> _batchPatients;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _fetchFromSource;
    private readonly bool _cloneMode;
    private readonly IEhrTargetPatientMatcher? _patientMatcher;
    private readonly bool _testRun;
    private readonly Dictionary<string, EhrPatientResolution> _patients = new(StringComparer.Ordinal);
    private readonly Dictionary<(string PatientId, bool OpenOnly), string?> _encounters = new();

    public EhrReferenceResolver(
        IEhrWriteChannel channel,
        IEhrWriteLedgerRepository ledger,
        IEhrWriteProfile? patientProfile,
        EhrWriteVendorProfile vendor,
        string targetKey,
        string? sourceBaseUrl,
        IReadOnlyDictionary<string, JsonObject> batchPatients,
        Func<string, string, CancellationToken, Task<string?>>? fetchFromSource,
        bool cloneMode = false,
        IEhrTargetPatientMatcher? patientMatcher = null,
        bool testRun = false)
    {
        _channel = channel;
        _ledger = ledger;
        _patientProfile = patientProfile;
        _vendor = vendor;
        _targetKey = targetKey;
        _sourceBaseUrl = sourceBaseUrl;
        _batchPatients = batchPatients;
        _fetchFromSource = fetchFromSource;
        _cloneMode = cloneMode;
        _patientMatcher = patientMatcher;
        _testRun = testRun;
    }

    /// <summary>The source patient's id from a reference like <c>Patient/123</c>, or null when it is not one.</summary>
    public static string? PatientId(string? reference) =>
        EhrFhirReference.TryParse(reference, out var type, out var id) && type == "Patient" ? id : null;

    public async Task<EhrPatientResolution> ResolvePatientAsync(string? sourceReference, CancellationToken cancellationToken)
    {
        var sourceId = PatientId(sourceReference);
        if (sourceId is null)
        {
            return new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, "patient-reference-invalid");
        }

        if (_patients.TryGetValue(sourceId, out var cached))
        {
            return cached;
        }

        var resolution = await ResolveUncachedAsync(sourceId, cancellationToken);
        _patients[sourceId] = resolution;
        return resolution;
    }

    /// <summary>The writer created the patient in this run: every later record of theirs resolves to the new id
    /// without another lookup.</summary>
    public void RecordCreatedPatient(string sourcePatientId, string targetPatientId) =>
        _patients[sourcePatientId] = new EhrPatientResolution(EhrPatientResolutionKind.Resolved, targetPatientId, null, "created");

    /// <summary>The writer tried to create the patient and could not: stop every later record of theirs, with the
    /// reason, instead of trying again for each.</summary>
    public void RecordPatientNotCreated(string sourcePatientId, string reason) =>
        _patients[sourcePatientId] = new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, reason);

    /// <summary>
    /// An encounter of the target patient the record can be filed to. <paramref name="openOnly"/> (vital signs) needs an
    /// encounter that is not finished or cancelled; clinical notes may also go to a finished one. Never reuses a source
    /// encounter id: the writer never sends records whose source is the target environment itself.
    /// </summary>
    public async Task<string?> ResolveEncounterAsync(
        string targetPatientId,
        string? sourceEncounterReference,
        bool openOnly,
        CancellationToken cancellationToken)
    {
        var key = (targetPatientId, openOnly);
        if (_encounters.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var outcome = await _channel.SearchForPatientAsync("Encounter", targetPatientId, cancellationToken);
        string? chosen = null;
        if (outcome.Succeeded)
        {
            var encounters = outcome.Resources.Select(Parse).OfType<JsonObject>().ToList();
            chosen = PickEncounter(encounters, openOnly);
        }

        _encounters[key] = chosen;
        return chosen;
    }

    private async Task<EhrPatientResolution> ResolveUncachedAsync(string sourceId, CancellationToken cancellationToken)
    {
        var sourceKey = EhrWriteKeys.SourceKey(_sourceBaseUrl, "Patient", sourceId, _cloneMode);
        var ledgerRows = await _ledger.FindAsync(_targetKey, "Patient", [sourceKey], cancellationToken);
        if (ledgerRows.TryGetValue(sourceKey, out var row)
            && row.State is EhrWriteLedgerState.Written or EhrWriteLedgerState.AlreadyAtTarget
            && row.TargetResourceId is { Length: > 0 } knownId)
        {
            return new EhrPatientResolution(EhrPatientResolutionKind.Resolved, knownId, null, "ledger");
        }

        var sourcePatient = await LoadSourcePatientAsync(sourceId, cancellationToken);
        if (sourcePatient is null)
        {
            return new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, "source-patient-unavailable");
        }

        if (_cloneMode)
        {
            return ResolveClone(sourcePatient, sourceId);
        }

        var byIdentifier = await SearchByIdentifiersAsync(sourcePatient, cancellationToken);
        if (byIdentifier is not null)
        {
            return byIdentifier;
        }

        if (_patientProfile is null)
        {
            return new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, "patient-not-matched");
        }

        var shaped = _patientProfile.Shape(sourcePatient, _channel.Options);
        if (shaped.Outcome != EhrShapeOutcome.Shaped || shaped.Resource is null)
        {
            return new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, "patient-" + shaped.Reason);
        }

        EhrPatientMatchOutcome match;
        if (_testRun)
        {
            match = new EhrPatientMatchOutcome(EhrPatientMatchKind.None, null, null, []);
        }
        else if (_vendor.SupportsPatientMatch)
        {
            match = await _channel.MatchPatientAsync(shaped.Resource.ToJsonString(), cancellationToken);
        }
        else
        {
            var indexed = _patientMatcher is null
                ? null
                : await _patientMatcher.MatchAsync(
                    new EhrTargetPatientMatchRequest(
                        _channel.TargetConnectionId, _vendor.Vendor, _channel.TargetBaseUrl, _sourceBaseUrl, sourceId,
                        sourcePatient.ToJsonString()),
                    cancellationToken);
            if (indexed is null)
            {
                return new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, "patient-awaiting-mpi");
            }

            match = indexed;
        }

        return match.Kind switch
        {
            EhrPatientMatchKind.Certain when match.PatientId is { Length: > 0 } id =>
                new EhrPatientResolution(EhrPatientResolutionKind.Resolved, id, null, "match"),
            EhrPatientMatchKind.None when _channel.Options.CreatePatientIfMissing =>
                new EhrPatientResolution(EhrPatientResolutionKind.WouldCreate, null, null, "match", shaped.Resource),
            EhrPatientMatchKind.None =>
                new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, "patient-not-in-ehr"),
            EhrPatientMatchKind.Ambiguous =>
                new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, "patient-match-needs-review"),
            _ => new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, "patient-match-failed"),
        };
    }

    private EhrPatientResolution ResolveClone(JsonObject sourcePatient, string sourceId)
    {
        if (_patientProfile is null)
        {
            return new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, "patient-not-writable");
        }

        var shaped = _patientProfile.Shape(EhrClonePatient.Build(sourcePatient, sourceId), _channel.Options);
        return shaped.Outcome == EhrShapeOutcome.Shaped && shaped.Resource is not null
            ? new EhrPatientResolution(EhrPatientResolutionKind.WouldCreate, null, null, "clone", shaped.Resource)
            : new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, "patient-" + shaped.Reason);
    }

    private async Task<EhrPatientResolution?> SearchByIdentifiersAsync(JsonObject sourcePatient, CancellationToken cancellationToken)
    {
        var searched = 0;
        foreach (var identifier in Objects(sourcePatient, "identifier"))
        {
            var system = String(identifier, "system");
            var value = String(identifier, "value");
            if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(value) || SourceOnlyIdentifierSystems.Contains(system))
            {
                continue;
            }

            if (searched++ >= MaxIdentifierSearches)
            {
                break;
            }

            var outcome = await _channel.SearchByIdentifierAsync("Patient", system, value, cancellationToken);
            if (!outcome.Succeeded)
            {
                continue;
            }

            // Only patients that really carry the searched identifier count: a server that ignores an unknown
            // search parameter returns everyone.
            var ids = outcome.Resources.Select(Parse)
                .Where(p => Objects(p, "identifier").Any(i => String(i, "system") == system && String(i, "value") == value))
                .Select(p => String(p, "id")).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count == 1)
            {
                return new EhrPatientResolution(EhrPatientResolutionKind.Resolved, ids[0], null, "identifier");
            }

            if (ids.Count > 1)
            {
                // Two EHR patients share the identifier: a person has to decide, so stop rather than fall through
                // to demographic matching.
                return new EhrPatientResolution(EhrPatientResolutionKind.Unresolved, null, "patient-identifier-ambiguous");
            }
        }

        return null;
    }

    private async Task<JsonObject?> LoadSourcePatientAsync(string sourceId, CancellationToken cancellationToken)
    {
        if (_batchPatients.TryGetValue(sourceId, out var inBatch))
        {
            return inBatch;
        }

        if (_fetchFromSource is null)
        {
            return null;
        }

        try
        {
            return Parse(await _fetchFromSource("Patient", sourceId, cancellationToken)) as JsonObject;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Open encounters first (in-progress, then arrived/triaged/onleave), most recent first; for notes, a
    /// finished encounter is the fallback. Planned (future appointments) and cancelled encounters never qualify.</summary>
    private static string? PickEncounter(IReadOnlyList<JsonObject> encounters, bool openOnly)
    {
        static int Rank(string? status) => status switch
        {
            "in-progress" => 0,
            "arrived" or "triaged" or "onleave" => 1,
            "finished" => 2,
            _ => 99,
        };

        var maxRank = openOnly ? 1 : 2;
        return encounters
            .Select(e => (Id: String(e, "id"), Rank: Rank(String(e, "status")), Start: String(Object(e, "period"), "start")))
            .Where(e => e.Id is not null && e.Rank <= maxRank)
            .OrderBy(e => e.Rank)
            .ThenByDescending(e => e.Start, StringComparer.Ordinal)
            .Select(e => e.Id)
            .FirstOrDefault();
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
