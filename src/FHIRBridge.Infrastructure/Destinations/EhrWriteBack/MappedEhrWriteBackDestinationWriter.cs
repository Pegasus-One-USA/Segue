using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Tabular;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>
/// Writes FHIR resources back into an EHR. For each record:
/// <list type="number">
/// <item>is its resource type selected, and does the vendor accept it (<see cref="EhrWriteCapabilities"/>)?</item>
/// <item>shape it to the vendor API's accepted subset (<see cref="IEhrWriteProfile"/>);</item>
/// <item>resolve its patient, and its encounter when the API needs one, to records that exist in the EHR
/// (<see cref="EhrReferenceResolver"/>);</item>
/// <item>check the ledger, the only duplicate guard there is, because the EHR files a replayed create again. A
/// record the ledger already holds as written or awaiting review stops here, before its note text is fetched or its
/// patient and encounter are looked up; the ledger is read again, row by row, right before each send;</item>
/// <item>send it once, or in a dry run count it as what a live run would send.</item>
/// </list>
///
/// <para><b>Live only where supported.</b> A record is sent only when the destination is not a dry run AND the code
/// supports live writes for its type (<see cref="FHIRBridge.Domain.Fhir.EhrWriteCapability.LiveWriteSupported"/>) AND,
/// for a vendor API that is contracted or proprietary (eClinicalWorks, athenaOne), the write connection says the
/// practice has it activated. Who may switch a destination off dry run and run it is decided by the EHR Write-Back
/// permissions. Every other record is counted as what a live run would send, with the reason
/// <c>vendor-activation-required</c> or <c>live-write-not-supported</c> when the destination asked for live writes.
/// Reads (identifier search, <c>$match</c>, encounter search) always run, because resolving references is what a dry
/// run is for.</para>
///
/// <para><b>Variants.</b> A vendor can file one resource type through several APIs (eClinicalWorks: problems,
/// encounter diagnoses, medical history). Each record goes to the first variant whose profile does not skip it as
/// another variant. A variant that needs enabling (<see cref="EhrWriteCapability.RequiresVariantOptIn"/>) is used only
/// when the destination enabled it.</para>
///
/// <para><b>Test runs.</b> With <see cref="EhrWriteBackRunOptions.TestAsVendor"/> the connection is a Generic FHIR test
/// server standing in for the vendor: the vendor's capabilities, profiles and channel are used as for a real write, the
/// contract switch counts as on, patients resolve by identifier only (a test server has no <c>$match</c> or MPI), and
/// the ledger keys the writes to the test server and the tested vendor, never to the vendor itself.</para>
///
/// <para><b>PHI.</b> The report and every record error hold resource types, positions and reason codes only. Nothing
/// read from a resource, a search result or an OperationOutcome's diagnostics is logged.</para>
/// </summary>
public sealed class MappedEhrWriteBackDestinationWriter : IConfiguredDestinationWriter
{
    private readonly EhrWriteProfileRegistry _profiles;
    private readonly IEhrWriteLedgerRepository _ledger;
    private readonly IEhrCloneModePolicy _cloneModePolicy;
    private readonly ILogger<MappedEhrWriteBackDestinationWriter> _logger;
    private readonly IEhrTargetPatientMatcher? _patientMatcher;

    public MappedEhrWriteBackDestinationWriter(
        EhrWriteProfileRegistry profiles,
        IEhrWriteLedgerRepository ledger,
        IEhrCloneModePolicy cloneModePolicy,
        ILogger<MappedEhrWriteBackDestinationWriter> logger,
        IEhrTargetPatientMatcher? patientMatcher = null)
    {
        _profiles = profiles;
        _ledger = ledger;
        _cloneModePolicy = cloneModePolicy;
        _logger = logger;
        _patientMatcher = patientMatcher;
    }

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        var channel = context.EhrWriteChannel ?? throw new InvalidOperationException(
            $"Destination '{destination.Name}' writes to an EHR and can only run inside a workflow, where the EHR " +
            "connection is resolved for it. It cannot be used by a configured pipeline route.");

        var vendor = EhrWriteCapabilities.VendorProfile(channel.TargetVendor) ?? throw new InvalidOperationException(
            $"Destination '{destination.Name}' points at a {channel.TargetVendor} connection, which does not accept writes.");

        // The ledger keys a source record by its source server and id. Without one known source a bare id
        // ("Patient/123") could belong to anyone, and a ledger hit could file a record to the wrong patient.
        if (string.IsNullOrWhiteSpace(context.SourceBaseUrl))
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}' needs exactly one upstream FHIR source node, so every record " +
                "can be traced to the server it came from.");
        }

        // Clone mode writes synthetic copies of real patients. It is QA-only and gated by a system setting; a
        // destination asking for it while the setting is off is refused outright rather than run as a normal write.
        var cloneMode = channel.Options.CloneMode;
        if (cloneMode && !await _cloneModePolicy.IsCloneModeEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}' is set to clone mode, which is turned off in system settings " +
                "(EhrWriteBack:CloneModeEnabled). Clone mode is for QA environments only.");
        }

        var parsed = Parse(records);

        // Binaries a bulk export delivered with the notes: the text of notes that link to them, never records to
        // write (no EHR API files a Binary), so they are neither counted nor reported.
        var exportedBinaries = parsed
            .Where(r => r.ResourceType == "Binary" && r.SourceId is not null && r.ParseProblem is null)
            .GroupBy(r => r.SourceId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Resource, StringComparer.Ordinal);
        parsed = parsed.Where(r => r.ResourceType != "Binary").ToList();

        var selected = new HashSet<string>(channel.Options.ResourceTypes, StringComparer.OrdinalIgnoreCase);
        var testRun = channel.Options.IsTestRun;
        // A test run sends every type, those the code keeps dry-run-only for the real vendor included: nothing reaches
        // the vendor, and the rehearsal is how such a type is checked before it is allowed live.
        var live = channel.Options.DryRun
            ? new HashSet<string>(StringComparer.Ordinal)
            : vendor.Capabilities.Where(c => testRun || c.IsLive(ActivatedFor(channel))).Select(c => c.ResourceType).ToHashSet(StringComparer.Ordinal);
        var targetKey = testRun
            ? EhrWriteKeys.TestTargetKey(channel.TargetBaseUrl, channel.TargetVendor.ToString())
            : EhrWriteKeys.TargetKey(channel.TargetBaseUrl);

        var run = new RunState(channel, vendor, context, targetKey, cloneMode, selected, live)
        {
            KnownLedgerRows = await PrefetchLedgerAsync(parsed, targetKey, context.SourceBaseUrl, cloneMode, cancellationToken),
            // Reading from and writing to the same EHR: every record is already there. Epic does not deduplicate
            // allergies, problems or notes, so sending them back would file second copies in the same chart. Clone
            // mode is the exception: it writes to a new test patient, never the original.
            SameEnvironment = !cloneMode && EhrWriteKeys.SameEnvironment(context.SourceBaseUrl, channel.TargetBaseUrl),
            ExportedBinaries = exportedBinaries,
        };
        run.Resolver = new EhrReferenceResolver(
            channel,
            _ledger,
            _profiles.Find(vendor.Vendor, "Patient"),
            vendor,
            targetKey,
            context.SourceBaseUrl,
            parsed.Where(r => r.ResourceType == "Patient" && r.SourceId is not null)
                .GroupBy(r => r.SourceId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Resource, StringComparer.Ordinal),
            context.FetchMissingReferenceAsync,
            cloneMode,
            _patientMatcher,
            testRun);

        var grantedScope = testRun ? null : await TryGetGrantedScopeAsync(channel, cancellationToken);

        // Patients first, so a record's patient is already resolved (or created), and cached, when it is reached.
        foreach (var record in parsed.OrderBy(r => r.ResourceType == "Patient" ? 0 : 1).ThenBy(r => r.Index))
        {
            var tally = run.TallyFor(record.ResourceType);
            tally.Received++;

            if (run.SameEnvironment)
            {
                tally.AlreadyWritten++;
                tally.Reason("already-in-ehr");
                continue;
            }

            var decision = await DecideAsync(record, run, cancellationToken);
            if (decision.AlreadyInLedger)
            {
                tally.AlreadyWritten++;
                tally.Reason(decision.Reason!);
                continue;
            }

            if (decision.Outcome != EhrShapeOutcome.Shaped || decision.Resource is null)
            {
                tally.Count(decision.Outcome, decision.Reason!);
                if (decision.Outcome == EhrShapeOutcome.Rejected)
                {
                    run.RecordErrors.Add($"{record.ResourceType} #{record.Index + 1}: {decision.Reason}");
                }

                continue;
            }

            var prepare = decision.Capability?.CreatesHolderEncounter == true
                ? ct => BindHolderEncounterAsync(run, decision.Profile!, decision.Resource, decision.TargetPatientId!, ct)
                : (Func<CancellationToken, Task<string?>>?)null;
            var sent = await PlanAndSendAsync(run, record.ResourceType, record.SourceId!, record.Index, decision.Resource, tally, cancellationToken, prepare);
            if (record.ResourceType == "Patient")
            {
                RecordPatientOutcome(run, record.SourceId!, sent);
            }
        }

        var dryRun = !live.Any(selected.Contains);
        var report = new EhrWriteReport(
            dryRun,
            channel.TargetVendor.ToString(),
            parsed.Count,
            // A test server grants no vendor scopes, so there is nothing to check.
            testRun ? "test-server" : ScopeStatus(grantedScope, run.Tallies, vendor),
            run.Tallies.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Value.ToSummary(t.Key)).ToList(),
            cloneMode,
            testRun);

        _logger.LogInformation(
            "EHR write-back to {Vendor} for destination {DestinationName}: dry run {DryRun}, clone mode {CloneMode}, " +
            "test run {TestRun}, {Received} records, {WouldWrite} would write, {Written} written, {AlreadyWritten} " +
            "already written, {Skipped} skipped, {Rejected} rejected, {Unknown} unknown, scope {ScopeStatus}.",
            report.TargetVendor, destination.Name, report.DryRun, cloneMode, testRun, report.RecordsReceived,
            report.Resources.Sum(r => r.WouldWrite), report.Resources.Sum(r => r.Written),
            report.Resources.Sum(r => r.AlreadyWritten), report.Resources.Sum(r => r.Skipped),
            report.Resources.Sum(r => r.Rejected), report.Resources.Sum(r => r.Unknown), report.ScopeStatus);

        // An empty list, not null: a null WrittenResourceIds makes the configured plane treat the whole batch as
        // stored, and a dry run stored nothing.
        return new DestinationWriteResult(
            Count: run.WrittenIds.Count,
            RecordErrors: run.RecordErrors.Count > 0 ? run.RecordErrors : null,
            WrittenResourceIds: run.WrittenIds,
            EhrWrite: report);
    }

    private async Task<Decision> DecideAsync(ParsedRecord record, RunState run, CancellationToken cancellationToken)
    {
        if (record.ParseProblem is not null)
        {
            return Decision.Reject(record.ParseProblem);
        }

        // Deny by default: a destination that selected nothing writes nothing.
        if (!run.Selected.Contains(record.ResourceType))
        {
            return Decision.Skip("not-selected");
        }

        // Without a source id the ledger cannot recognise the record next time, so it would be filed again.
        if (string.IsNullOrWhiteSpace(record.SourceId))
        {
            return Decision.Reject("missing-id");
        }

        var candidates = run.Vendor.Capabilities
            .Where(c => c.ResourceType == record.ResourceType && c.Supports(EhrWriteOperation.Create))
            .ToList();
        if (candidates.Count == 0)
        {
            return Decision.Skip("not-writable");
        }

        if (record.ResourceType == "Patient")
        {
            return await DecidePatientAsync(record, run.Resolver, cancellationToken);
        }

        var resource = record.Resource;
        var (capability, profile, firstShape) = PickVariant(run, candidates, resource);
        if (capability is null || profile is null)
        {
            return new Decision(firstShape.Outcome, null, firstShape.Reason);
        }

        // Written before, or waiting for review: nothing below would send it, so none of the source reads and EHR
        // searches below are made for it, and a source that cannot be read now cannot misreport it.
        if (LedgerShortCircuit(run, record) is { } ledgerReason)
        {
            return Decision.InLedger(ledgerReason);
        }

        if (capability.RequiresTargetReferences && !TabularSourceSettings.IsTabularSource(run.Context.SourceBaseUrl))
        {
            // The API files against records that must already exist in the target (a questionnaire, a referral, an
            // imaging report). A source system's ids name nothing there, or the wrong record; only a CSV / SQL Table
            // template, written for the target, can carry the target's ids.
            return Decision.Skip("target-references-unmappable");
        }

        if (capability.CreatesHolderEncounter && !run.Channel.Options.CreateHolderEncounter)
        {
            // The vendor files this only on an encounter the bridge would have to create; the destination did not
            // opt into that.
            return Decision.Skip("holder-encounter-not-enabled");
        }

        var shaped = firstShape;
        if (record.ResourceType == "DocumentReference" && capability.Variant is null or EhrWriteVariants.ClinicalNote)
        {
            // A note the profile skips whatever its text (excluded type, C-CDA summary, superseded) was already skipped
            // by PickVariant, before its text is fetched: no source read for a note that will not be sent, and its
            // real reason is reported even when the read would have failed. A note whose text sits in a Binary on the
            // source is read from there now; the EHR takes it inline.
            var (note, contentProblem) = await EhrNoteContent.InlineAsync(
                resource, run.Context.SourceBaseUrl, run.ExportedBinaries, run.Context.FetchMissingReferenceAsync, _logger, cancellationToken);
            if (contentProblem is not null)
            {
                return contentProblem == "note-content-fetch-failed"
                    ? Decision.Skip(contentProblem)
                    : Decision.Reject(contentProblem);
            }

            resource = note;
            shaped = profile.Shape(resource, run.Channel.Options);
        }

        if (shaped.Outcome != EhrShapeOutcome.Shaped || shaped.Resource is null)
        {
            return new Decision(shaped.Outcome, null, shaped.Reason);
        }

        if (!capability.RequiresPatient)
        {
            // Not filed against a patient (a non-patient document) or against one named some other way (an Epic
            // questionnaire answers its assignment): nothing to resolve or bind.
            return new Decision(EhrShapeOutcome.Shaped, shaped.Resource, null, capability, profile);
        }

        var patient = await run.Resolver.ResolvePatientAsync(shaped.SourcePatientReference, cancellationToken);
        if (patient.Kind == EhrPatientResolutionKind.WouldCreate)
        {
            // A live run can only create the patient when Patient is selected; without it no run ever will, so say
            // so rather than "not yet created", which suggests a later run would file the record.
            if (!run.Channel.Options.DryRun && !run.Selected.Contains("Patient"))
            {
                return Decision.Skip("patient-not-selected");
            }

            patient = await CreatePatientForRecordAsync(run, shaped.SourcePatientReference, patient, cancellationToken);
        }

        if (patient.Kind == EhrPatientResolutionKind.Unresolved)
        {
            return Decision.Skip(patient.Reason ?? "patient-unresolved");
        }

        if (patient.Kind == EhrPatientResolutionKind.WouldCreate)
        {
            // The patient does not exist yet (dry run, or patient creation not live), so neither does any
            // encounter of theirs.
            return capability.RequiresEncounter
                ? Decision.Skip("no-eligible-encounter")
                : Decision.Skip("patient-not-yet-created");
        }

        string? encounterId = null;
        if (capability.RequiresEncounter)
        {
            // Flowsheet rows (vitals, lines/drains/airways) cannot be filed on a closed encounter.
            var openOnly = capability.Variant is EhrWriteVariants.VitalSigns or EhrWriteVariants.LinesDrainsAirways;
            encounterId = await run.Resolver.ResolveEncounterAsync(
                patient.TargetPatientId!, shaped.SourceEncounterReference, openOnly, cancellationToken);
            if (encounterId is null)
            {
                return Decision.Skip("no-eligible-encounter");
            }
        }

        profile.BindReferences(shaped.Resource, patient.TargetPatientId!, encounterId);
        return new Decision(EhrShapeOutcome.Shaped, shaped.Resource, null, capability, profile, patient.TargetPatientId);
    }

    /// <summary>
    /// The capability (and profile) a record goes to: the first, in table order, whose profile does not skip it as
    /// another variant. A profile that rejects the record has recognised it as its own, so it is chosen and the
    /// rejection reported. When every variant skips it, the reason reported is the first that is not a variant
    /// mismatch (<c>not-a-...</c>, <c>not-an-...</c>), e.g. "entered-in-error", else the first variant's. A variant the
    /// destination has not enabled is never chosen; when it is the only one that recognised the record, the reason is
    /// <c>variant-not-enabled</c>.
    /// </summary>
    private (EhrWriteCapability? Capability, IEhrWriteProfile? Profile, EhrShapeResult FirstShape) PickVariant(
        RunState run,
        IReadOnlyList<EhrWriteCapability> candidates,
        JsonObject resource)
    {
        EhrShapeResult? firstSkip = null;
        EhrShapeResult? realSkip = null;
        var recognisedByDisabledVariant = false;
        foreach (var candidate in candidates)
        {
            var profile = _profiles.Find(run.Vendor.Vendor, candidate.ResourceType, candidate.Variant);
            if (profile is null)
            {
                continue;
            }

            var shaped = profile.Shape(resource, run.Channel.Options);
            if (shaped.Outcome != EhrShapeOutcome.Skipped
                && candidate.RequiresVariantOptIn
                && !run.Channel.Options.IsVariantEnabled(candidate.Variant))
            {
                recognisedByDisabledVariant = true;
                continue;
            }

            if (shaped.Outcome != EhrShapeOutcome.Skipped)
            {
                return (candidate, profile, shaped);
            }

            firstSkip ??= shaped;
            if (realSkip is null && shaped.Reason is { } reason && !IsVariantMismatch(reason))
            {
                realSkip = shaped;
            }
        }

        return recognisedByDisabledVariant
            ? (null, null, EhrShapeResult.Skip("variant-not-enabled"))
            : (null, null, realSkip ?? firstSkip ?? EhrShapeResult.Skip("not-writable"));
    }

    /// <summary>A profile's "this record is another variant" skip: <c>not-a-...</c> or <c>not-an-...</c>.</summary>
    private static bool IsVariantMismatch(string reason) =>
        reason.StartsWith("not-a-", StringComparison.Ordinal) || reason.StartsWith("not-an-", StringComparison.Ordinal);

    /// <summary>
    /// Files a record that the vendor takes only on an encounter the bridge creates (eClinicalWorks medical and
    /// surgical history on a telephone encounter): one encounter per patient per run, created when the first such
    /// record is actually sent, so a run whose records are all already written creates none. Returns why the record
    /// cannot be sent, or null once it is bound to the encounter.
    /// </summary>
    private async Task<string?> BindHolderEncounterAsync(
        RunState run,
        IEhrWriteProfile profile,
        JsonObject shaped,
        string targetPatientId,
        CancellationToken cancellationToken)
    {
        if (!run.HolderEncounters.TryGetValue(targetPatientId, out var encounterId))
        {
            try
            {
                var outcome = await run.Channel.CreateHolderEncounterAsync(targetPatientId, cancellationToken);
                encounterId = outcome.Kind == EhrCreateKind.Created ? outcome.ResourceId : null;
                _logger.LogInformation(
                    "EHR write-back holder encounter for {Vendor}: {Outcome} ({StatusCode}).",
                    run.Vendor.Vendor, outcome.Kind, outcome.HttpStatus);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("EHR write-back holder encounter for {Vendor} failed ({ExceptionType}).", run.Vendor.Vendor, ex.GetType().Name);
                encounterId = null;
            }

            // Cached either way: a failed or uncertain create is not repeated for every record of the patient.
            run.HolderEncounters[targetPatientId] = encounterId;
        }

        if (encounterId is null)
        {
            return "holder-encounter-not-created";
        }

        profile.BindReferences(shaped, targetPatientId, encounterId);
        return null;
    }

    /// <summary>A source patient is written only when the EHR has no such patient and the destination opted in (or
    /// clone mode asks for a clone); a patient the EHR already has is "already written", which is what the
    /// destination wanted.</summary>
    private static async Task<Decision> DecidePatientAsync(
        ParsedRecord record,
        EhrReferenceResolver resolver,
        CancellationToken cancellationToken)
    {
        if (record.SourceId is null)
        {
            return Decision.Reject("missing-id");
        }

        var resolution = await resolver.ResolvePatientAsync($"Patient/{record.SourceId}", cancellationToken);
        return resolution.Kind switch
        {
            EhrPatientResolutionKind.Resolved => Decision.Skip(resolution.Method == "created" ? "patient-created" : "patient-already-in-ehr"),
            EhrPatientResolutionKind.WouldCreate when resolution.ShapedPatient is not null =>
                new Decision(EhrShapeOutcome.Shaped, resolution.ShapedPatient, null),
            _ => Decision.Skip(resolution.Reason ?? "patient-unresolved"),
        };
    }

    /// <summary>
    /// A record's patient is not in the EHR and the batch did not carry the patient itself: create it now, through
    /// the same ledger-guarded path as a Patient record, so the record can be filed in this run. Only when Patient is
    /// selected and live; otherwise the record waits, as <c>patient-not-yet-created</c>.
    /// </summary>
    private async Task<EhrPatientResolution> CreatePatientForRecordAsync(
        RunState run,
        string? sourcePatientReference,
        EhrPatientResolution resolution,
        CancellationToken cancellationToken)
    {
        var sourcePatientId = EhrReferenceResolver.PatientId(sourcePatientReference);
        if (sourcePatientId is null
            || resolution.ShapedPatient is null
            || !run.Selected.Contains("Patient")
            || !run.Live.Contains("Patient"))
        {
            return resolution;
        }

        var sent = await PlanAndSendAsync(run, "Patient", sourcePatientId, recordIndex: null, resolution.ShapedPatient, run.TallyFor("Patient"), cancellationToken);
        RecordPatientOutcome(run, sourcePatientId, sent);
        return await run.Resolver.ResolvePatientAsync(sourcePatientReference, cancellationToken);
    }

    private static void RecordPatientOutcome(RunState run, string sourcePatientId, SendResult sent)
    {
        if (sent.CreatedId is { } createdId)
        {
            run.Resolver.RecordCreatedPatient(sourcePatientId, createdId);
        }
        else if (sent.Kind == SendKind.NotWritten)
        {
            run.Resolver.RecordPatientNotCreated(sourcePatientId, "patient-not-created");
        }
        else if (sent.Kind == SendKind.Blocked)
        {
            run.Resolver.RecordPatientNotCreated(sourcePatientId, "patient-awaiting-review");
        }

        // WouldWrite (dry run / type not live-capable) and Capped leave the patient as "would create".
    }

    /// <summary>Ledger check, write cap and live-capability check for one shaped record, then the send itself when all
    /// allow it. <paramref name="recordIndex"/> is null for a patient created on behalf of another record.</summary>
    private async Task<SendResult> PlanAndSendAsync(
        RunState run,
        string resourceType,
        string sourceId,
        int? recordIndex,
        JsonObject shaped,
        Tally tally,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<string?>>? prepare = null)
    {
        var sourceKey = EhrWriteKeys.SourceKey(run.Context.SourceBaseUrl, resourceType, sourceId, run.CloneMode);
        var contentHash = EhrWriteKeys.ContentHash(shaped);
        var existing = (await _ledger.FindAsync(run.TargetKey, resourceType, [sourceKey], cancellationToken))
            .GetValueOrDefault(sourceKey);
        if (LedgerBlocks(existing, contentHash) is { } ledgerReason)
        {
            tally.AlreadyWritten++;
            tally.Reason(ledgerReason);
            return new SendResult(SendKind.Blocked, null);
        }

        if (run.PlannedWrites >= run.Channel.Options.MaxWritesPerRun)
        {
            tally.Count(EhrShapeOutcome.Skipped, "write-cap-reached");
            return new SendResult(SendKind.Capped, null);
        }

        run.PlannedWrites++;
        if (!run.Live.Contains(resourceType))
        {
            tally.WouldWrite++;
            if (!run.Channel.Options.DryRun)
            {
                tally.Reason(NotLiveReason(run, resourceType));
            }

            return new SendResult(SendKind.WouldWrite, null);
        }

        // Last step before the send, after every check that could stop it: anything it creates (a holder encounter)
        // exists only for a record that is really sent. The content hash above was taken before it, so it does not
        // change from run to run.
        if (prepare is not null && await prepare(cancellationToken) is { } prepareProblem)
        {
            tally.Count(EhrShapeOutcome.Skipped, prepareProblem);
            return new SendResult(SendKind.NotWritten, null);
        }

        var createdId = await SendAsync(run, resourceType, sourceId, recordIndex, shaped, existing, sourceKey, contentHash, tally, cancellationToken);
        return new SendResult(createdId is null ? SendKind.NotWritten : SendKind.Written, createdId);
    }

    /// <summary>Why a live run counts a type as "would write" instead of sending it: the vendor API is not activated on
    /// the connection, or the code does not send the type live at all.</summary>
    private static string NotLiveReason(RunState run, string resourceType) =>
        run.Vendor.Capabilities.Any(c => c.ResourceType == resourceType
                                         && c.LiveWriteSupported
                                         && c.RequiresVendorActivation
                                         && !ActivatedFor(run.Channel))
            ? "vendor-activation-required"
            : "live-write-not-supported";

    /// <summary>The contract switch, as the run sees it: a test run never reaches the vendor, so its contracted APIs
    /// count as activated.</summary>
    private static bool ActivatedFor(IEhrWriteChannel channel) =>
        channel.VendorWriteApisActivated || channel.Options.IsTestRun;

    /// <summary>Every ledger row for the batch's records, read once per resource type.</summary>
    private async Task<IReadOnlyDictionary<(string ResourceType, string SourceKey), EhrWriteLedgerEntry>> PrefetchLedgerAsync(
        IReadOnlyList<ParsedRecord> records,
        string targetKey,
        string? sourceBaseUrl,
        bool cloneMode,
        CancellationToken cancellationToken)
    {
        var known = new Dictionary<(string, string), EhrWriteLedgerEntry>();
        foreach (var group in records.Where(r => r.SourceId is not null && r.ParseProblem is null).GroupBy(r => r.ResourceType, StringComparer.Ordinal))
        {
            var keys = group.Select(r => EhrWriteKeys.SourceKey(sourceBaseUrl, group.Key, r.SourceId!, cloneMode)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var (key, entry) in await _ledger.FindAsync(targetKey, group.Key, keys, cancellationToken))
            {
                known[(group.Key, key)] = entry;
            }
        }

        return known;
    }

    /// <summary>
    /// The ledger already settles this record whatever its content: written or already in the EHR (no in-scope API
    /// updates, so it is never resent), or sent with an unknown outcome and awaiting review. Reported as
    /// "already-written" / "awaiting-review". A rejected or released row is not settled: the record goes on to be
    /// shaped and checked again at send time.
    /// </summary>
    private static string? LedgerShortCircuit(RunState run, ParsedRecord record)
    {
        var key = EhrWriteKeys.SourceKey(run.Context.SourceBaseUrl, record.ResourceType, record.SourceId!, run.CloneMode);
        return run.KnownLedgerRows.GetValueOrDefault((record.ResourceType, key))?.State switch
        {
            EhrWriteLedgerState.Written or EhrWriteLedgerState.AlreadyAtTarget => "already-written",
            EhrWriteLedgerState.Unknown or EhrWriteLedgerState.Pending => "awaiting-review",
            _ => null,
        };
    }

    /// <summary>Why the ledger stops this record being sent again, or null when it may be sent.</summary>
    private static string? LedgerBlocks(EhrWriteLedgerEntry? existing, string contentHash)
    {
        if (existing is null || existing.State == EhrWriteLedgerState.Released)
        {
            // Released: a reviewer confirmed the record is not in the EHR, so it is sent once more.
            return null;
        }

        if (existing.State == EhrWriteLedgerState.Rejected)
        {
            // A refusal made before any work (no token, 429, 503 with Retry-After) or caused by configuration
            // (401/403: API not on the client id; 59108: a required identifier the org build demands) is retried as
            // is, so fixing the configuration releases it. Any other rejection is retried only once the record has
            // changed; the same content would be refused again.
            var retryable = existing.HttpStatus is null or 401 or 403 or 429 or 503
                || (existing.OutcomeCodes?.Split(',').Contains("59108") ?? false);
            return retryable || existing.ContentHash != contentHash ? null : "previously-rejected";
        }

        if (existing.State is EhrWriteLedgerState.Unknown or EhrWriteLedgerState.Pending)
        {
            return "awaiting-review";
        }

        // Written or already at target. None of the in-scope EHR APIs supports update, so a changed record cannot
        // be sent again without filing a second copy.
        return existing.ContentHash == contentHash ? "already-written" : "changed-after-write";
    }

    /// <summary>Sends one create and settles its ledger row. Returns the EHR's id when the record was created.</summary>
    private async Task<string?> SendAsync(
        RunState run,
        string resourceType,
        string sourceId,
        int? recordIndex,
        JsonObject shaped,
        EhrWriteLedgerEntry? existing,
        string sourceKey,
        string contentHash,
        Tally tally,
        CancellationToken cancellationToken)
    {
        var channel = run.Channel;
        var label = recordIndex is { } index ? $"{resourceType} #{index + 1}" : $"{resourceType} (for a record)";
        var now = DateTime.UtcNow;
        var entry = existing;
        if (entry is null)
        {
            entry = new EhrWriteLedgerEntry(
                run.TargetKey, channel.TargetConnectionId, resourceType, sourceKey, contentHash,
                EhrWriteOperation.Create, channel.DestinationId,
                run.Context.PipelineRunId == Guid.Empty ? null : run.Context.PipelineRunId, now);
            if (!await _ledger.TryAddAsync(entry, cancellationToken))
            {
                // Another run claimed this record first.
                tally.AlreadyWritten++;
                tally.Reason("awaiting-review");
                return null;
            }
        }

        // The claim is conditional on the row not having changed since it was read (AttemptCount and State are
        // concurrency tokens): two runs, or a run and a reviewer, cannot both act on the same row.
        entry.MarkSending(contentHash, now);
        if (!await _ledger.TryClaimAsync(entry, cancellationToken))
        {
            tally.AlreadyWritten++;
            tally.Reason("awaiting-review");
            return null;
        }

        EhrCreateOutcome outcome;
        try
        {
            outcome = await channel.CreateAsync(resourceType, shaped.ToJsonString(), cancellationToken);
        }
        catch (Exception ex)
        {
            // Whatever happened, the request may have landed: record that before anything else, cancellation
            // included, and never retry it automatically.
            entry.MarkUnknown(null, ex is OperationCanceledException ? "cancelled" : "exception", DateTime.UtcNow);
            await _ledger.SaveChangesAsync(CancellationToken.None);
            tally.Unknown++;
            if (ex is OperationCanceledException)
            {
                throw;
            }

            tally.Reason("outcome-unknown");
            return null;
        }

        if (outcome.Kind == EhrCreateKind.Rejected
            && outcome.Issues.Any(i => i.VendorCode == EhrWriteOutcomeCodes.CancelledBeforeSend))
        {
            // The run was cancelled before the create left: nothing reached the EHR. Recorded as a refusal with no
            // HTTP status, which the next run retries as is, rather than an unknown outcome a person must review.
            entry.MarkRejected(null, EhrWriteOutcomeCodes.CancelledBeforeSend, DateTime.UtcNow);
            await _ledger.SaveChangesAsync(CancellationToken.None);
            throw new OperationCanceledException(cancellationToken);
        }

        string? createdId = null;
        var codes = string.Join(",", outcome.Issues.Select(i => i.VendorCode ?? i.Code).Where(c => c is not null).Distinct());
        var alreadyThere = outcome.Issues.Any(i => run.Vendor.IsAlreadyAtTarget(i.VendorCode, i.Expression));
        switch (outcome.Kind)
        {
            // Patient.Create is match-or-create. A clone that comes back as the original patient means the EHR
            // matched the clone to the real chart: nothing may be filed against it.
            case EhrCreateKind.Created when run.CloneMode && resourceType == "Patient"
                                            && string.Equals(outcome.ResourceId, sourceId, StringComparison.Ordinal):
                entry.MarkRejected(outcome.HttpStatus, CloneMatchedOriginalCode, DateTime.UtcNow);
                tally.Rejected++;
                tally.Reason(CloneMatchedOriginalCode);
                run.RecordErrors.Add($"{label}: {CloneMatchedOriginalCode}");
                break;
            case EhrCreateKind.Created when outcome.ResourceId is { Length: > 0 } id:
                entry.MarkWritten(id, outcome.HttpStatus ?? 201, DateTime.UtcNow);
                tally.Written++;
                run.WrittenIds.Add(sourceId);
                createdId = id;
                break;
            case EhrCreateKind.Rejected when alreadyThere:
                entry.MarkAlreadyAtTarget(null, outcome.HttpStatus, codes, DateTime.UtcNow);
                tally.AlreadyWritten++;
                tally.Reason("already-in-ehr");
                break;
            case EhrCreateKind.Rejected:
                entry.MarkRejected(outcome.HttpStatus, codes, DateTime.UtcNow);
                tally.Rejected++;
                tally.Reason("rejected-by-ehr");
                run.RecordErrors.Add($"{label}: rejected by the EHR ({outcome.HttpStatus}{(codes.Length > 0 ? ", " + codes : string.Empty)})");
                break;
            default:
                entry.MarkUnknown(outcome.HttpStatus, codes, DateTime.UtcNow);
                tally.Unknown++;
                tally.Reason("outcome-unknown");
                break;
        }

        await _ledger.SaveChangesAsync(CancellationToken.None);
        return createdId;
    }

    private async Task<string?> TryGetGrantedScopeAsync(IEhrWriteChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            return await channel.GetGrantedScopeAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not read the granted scope of the EHR write connection ({ExceptionType}).", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Checked against the types that would actually be written, so a scope gap is reported before the
    /// first live write fails on it.</summary>
    private static string ScopeStatus(string? grantedScope, IReadOnlyDictionary<string, Tally> tallies, EhrWriteVendorProfile vendor)
    {
        if (grantedScope is null)
        {
            return "unknown";
        }

        var missing = tallies
            .Where(t => t.Value.WouldWrite + t.Value.Written > 0 && !AllowsWrite(grantedScope, t.Key, vendor))
            .Select(t => t.Key)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        return missing.Count == 0 ? "verified" : "missing:" + string.Join(",", missing);
    }

    /// <summary>A FHIR create scope for the type, or, for a type the vendor writes through its proprietary API, that
    /// API's scope.</summary>
    private static bool AllowsWrite(string grantedScope, string resourceType, EhrWriteVendorProfile vendor) =>
        vendor.ProprietaryApiScope is { } proprietary
        && vendor.Capabilities.Any(c => c.ResourceType == resourceType && c.RequiresVendorActivation)
            ? grantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(proprietary, StringComparer.Ordinal)
            : EhrWriteScopeMatcher.AllowsCreate(grantedScope, resourceType);

    private static List<ParsedRecord> Parse(IReadOnlyCollection<MappedDestinationRecord> records)
    {
        var parsed = new List<ParsedRecord>(records.Count);
        var index = 0;
        foreach (var record in records)
        {
            JsonObject? resource = null;
            if (!string.IsNullOrWhiteSpace(record.SourceJson))
            {
                try
                {
                    resource = JsonNode.Parse(record.SourceJson) as JsonObject;
                }
                catch (JsonException)
                {
                    resource = null;
                }
            }

            // The resource's own type wins over the record's: the Runtime plane hands a mixed batch over under one
            // synthetic mapping profile.
            var resourceType = EhrFhirJson.String(resource, "resourceType") ?? record.ResourceType;
            var sourceId = record.SourceResourceId ?? EhrFhirJson.String(resource, "id");
            parsed.Add(new ParsedRecord(
                index++,
                resourceType,
                sourceId,
                resource ?? new JsonObject(),
                resource is null ? "not-a-fhir-resource" : null));
        }

        return parsed;
    }

    /// <summary>Recorded on a clone patient whose create came back as the original patient's id.</summary>
    internal const string CloneMatchedOriginalCode = "clone-matched-original";

    private sealed record ParsedRecord(int Index, string ResourceType, string? SourceId, JsonObject Resource, string? ParseProblem);

    private enum SendKind
    {
        /// <summary>The ledger already holds the record (written, awaiting review, previously rejected).</summary>
        Blocked,
        Capped,
        /// <summary>Counted, not sent: a dry run, or the code does not support live writes for the type.</summary>
        WouldWrite,
        Written,
        /// <summary>Sent, and refused or of unknown outcome.</summary>
        NotWritten,
    }

    private sealed record SendResult(SendKind Kind, string? CreatedId);

    /// <summary>Everything one write call shares across its records.</summary>
    private sealed class RunState
    {
        public RunState(
            IEhrWriteChannel channel,
            EhrWriteVendorProfile vendor,
            PipelineWriteContext context,
            string targetKey,
            bool cloneMode,
            IReadOnlySet<string> selected,
            IReadOnlySet<string> live)
        {
            Channel = channel;
            Vendor = vendor;
            Context = context;
            TargetKey = targetKey;
            CloneMode = cloneMode;
            Selected = selected;
            Live = live;
        }

        public IEhrWriteChannel Channel { get; }
        public EhrWriteVendorProfile Vendor { get; }
        public PipelineWriteContext Context { get; }
        public string TargetKey { get; }
        public bool CloneMode { get; }
        public IReadOnlySet<string> Selected { get; }
        /// <summary>Types sent for real: none in a dry run, else every type the vendor supports live.</summary>
        public IReadOnlySet<string> Live { get; }
        public bool SameEnvironment { get; init; }

        /// <summary>The batch's ledger rows as read before the first record, for <see cref="LedgerShortCircuit"/>.</summary>
        public IReadOnlyDictionary<(string ResourceType, string SourceKey), EhrWriteLedgerEntry> KnownLedgerRows { get; init; } =
            new Dictionary<(string, string), EhrWriteLedgerEntry>();

        /// <summary>Binaries delivered in the batch, by id; see <see cref="EhrNoteContent.InlineAsync"/>.</summary>
        public IReadOnlyDictionary<string, JsonObject> ExportedBinaries { get; init; } = new Dictionary<string, JsonObject>();
        public EhrReferenceResolver Resolver { get; set; } = default!;

        /// <summary>Encounters created in this run to file records on, by target patient; null when the create
        /// failed.</summary>
        public Dictionary<string, string?> HolderEncounters { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Tally> Tallies { get; } = new(StringComparer.Ordinal);
        public List<string> RecordErrors { get; } = [];
        public List<string?> WrittenIds { get; } = [];
        public int PlannedWrites { get; set; }

        public Tally TallyFor(string resourceType)
        {
            if (!Tallies.TryGetValue(resourceType, out var tally))
            {
                tally = new Tally();
                Tallies[resourceType] = tally;
            }

            return tally;
        }
    }

    private sealed record Decision(
        EhrShapeOutcome Outcome,
        JsonObject? Resource,
        string? Reason,
        EhrWriteCapability? Capability = null,
        IEhrWriteProfile? Profile = null,
        string? TargetPatientId = null,
        bool AlreadyInLedger = false)
    {
        public static Decision InLedger(string reason) => new(EhrShapeOutcome.Skipped, null, reason, AlreadyInLedger: true);

        public static Decision Skip(string reason) => new(EhrShapeOutcome.Skipped, null, reason);

        public static Decision Reject(string reason) => new(EhrShapeOutcome.Rejected, null, reason);
    }

    private sealed class Tally
    {
        private readonly Dictionary<string, int> _reasons = new(StringComparer.Ordinal);

        public int Received { get; set; }
        public int WouldWrite { get; set; }
        public int Written { get; set; }
        public int AlreadyWritten { get; set; }
        public int Skipped { get; set; }
        public int Rejected { get; set; }
        public int Unknown { get; set; }

        public void Count(EhrShapeOutcome outcome, string reason)
        {
            if (outcome == EhrShapeOutcome.Rejected)
            {
                Rejected++;
            }
            else
            {
                Skipped++;
            }

            Reason(reason);
        }

        public void Reason(string reason) => _reasons[reason] = _reasons.GetValueOrDefault(reason) + 1;

        public EhrWriteResourceSummary ToSummary(string resourceType) => new(
            resourceType, Received, WouldWrite, Written, AlreadyWritten, Skipped, Rejected, Unknown,
            new Dictionary<string, int>(_reasons, StringComparer.Ordinal));
    }
}
