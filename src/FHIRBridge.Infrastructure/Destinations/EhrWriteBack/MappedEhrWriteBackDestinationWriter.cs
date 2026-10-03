using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
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
/// <item>check the ledger, the only duplicate guard there is, because the EHR files a replayed create again;</item>
/// <item>send it once, or in a dry run count it as what a live run would send.</item>
/// </list>
///
/// <para><b>Live only where supported.</b> A record is sent only when the destination is not a dry run AND the code
/// supports live writes for its type (<see cref="FHIRBridge.Domain.Fhir.EhrWriteCapability.LiveWriteSupported"/>).
/// Who may switch a destination off dry run and run it is decided by the EHR Write-Back permissions. Every other
/// record is counted as what a live run would send, with the reason <c>live-write-not-supported</c> when the
/// destination asked for live writes (a dry-run-only vendor such as eClinicalWorks or athenahealth). Reads
/// (identifier search, <c>$match</c>, encounter search) always run, because resolving references is what a dry run
/// is for.</para>
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

    public MappedEhrWriteBackDestinationWriter(
        EhrWriteProfileRegistry profiles,
        IEhrWriteLedgerRepository ledger,
        IEhrCloneModePolicy cloneModePolicy,
        ILogger<MappedEhrWriteBackDestinationWriter> logger)
    {
        _profiles = profiles;
        _ledger = ledger;
        _cloneModePolicy = cloneModePolicy;
        _logger = logger;
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
        var selected = new HashSet<string>(channel.Options.ResourceTypes, StringComparer.OrdinalIgnoreCase);
        var live = channel.Options.DryRun
            ? new HashSet<string>(StringComparer.Ordinal)
            : vendor.Capabilities.Where(c => c.LiveWriteSupported).Select(c => c.ResourceType).ToHashSet(StringComparer.Ordinal);
        var targetKey = EhrWriteKeys.TargetKey(channel.TargetBaseUrl);

        var run = new RunState(channel, vendor, context, targetKey, cloneMode, selected, live)
        {
            // Reading from and writing to the same EHR: every record is already there. Epic does not deduplicate
            // allergies, problems or notes, so sending them back would file second copies in the same chart. Clone
            // mode is the exception: it writes to a new test patient, never the original.
            SameEnvironment = !cloneMode && EhrWriteKeys.SameEnvironment(context.SourceBaseUrl, channel.TargetBaseUrl),
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
            cloneMode);

        var grantedScope = await TryGetGrantedScopeAsync(channel, cancellationToken);

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
            if (decision.Outcome != EhrShapeOutcome.Shaped || decision.Resource is null)
            {
                tally.Count(decision.Outcome, decision.Reason!);
                if (decision.Outcome == EhrShapeOutcome.Rejected)
                {
                    run.RecordErrors.Add($"{record.ResourceType} #{record.Index + 1}: {decision.Reason}");
                }

                continue;
            }

            var sent = await PlanAndSendAsync(run, record.ResourceType, record.SourceId!, record.Index, decision.Resource, tally, cancellationToken);
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
            ScopeStatus(grantedScope, run.Tallies),
            run.Tallies.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Value.ToSummary(t.Key)).ToList(),
            cloneMode);

        _logger.LogInformation(
            "EHR write-back to {Vendor} for destination {DestinationName}: dry run {DryRun}, clone mode {CloneMode}, " +
            "{Received} records, {WouldWrite} would write, {Written} written, {AlreadyWritten} already written, " +
            "{Skipped} skipped, {Rejected} rejected, {Unknown} unknown, scope {ScopeStatus}.",
            report.TargetVendor, destination.Name, report.DryRun, cloneMode, report.RecordsReceived,
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

        var capability = run.Vendor.Capabilities.FirstOrDefault(c => c.ResourceType == record.ResourceType);
        if (capability is null || !capability.Supports(EhrWriteOperation.Create))
        {
            return Decision.Skip("not-writable");
        }

        if (record.ResourceType == "Patient")
        {
            return await DecidePatientAsync(record, run.Resolver, cancellationToken);
        }

        var profile = _profiles.Find(run.Vendor.Vendor, record.ResourceType);
        if (profile is null)
        {
            return Decision.Skip("not-writable");
        }

        var shaped = profile.Shape(record.Resource, run.Channel.Options);
        if (shaped.Outcome != EhrShapeOutcome.Shaped || shaped.Resource is null)
        {
            return new Decision(shaped.Outcome, null, shaped.Reason);
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
            var openOnly = capability.Variant == EhrWriteVariants.VitalSigns;
            encounterId = await run.Resolver.ResolveEncounterAsync(
                patient.TargetPatientId!, shaped.SourceEncounterReference, openOnly, cancellationToken);
            if (encounterId is null)
            {
                return Decision.Skip("no-eligible-encounter");
            }
        }

        profile.BindReferences(shaped.Resource, patient.TargetPatientId!, encounterId);
        return new Decision(EhrShapeOutcome.Shaped, shaped.Resource, null);
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
        CancellationToken cancellationToken)
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
                tally.Reason("live-write-not-supported");
            }

            return new SendResult(SendKind.WouldWrite, null);
        }

        var createdId = await SendAsync(run, resourceType, sourceId, recordIndex, shaped, existing, sourceKey, contentHash, tally, cancellationToken);
        return new SendResult(createdId is null ? SendKind.NotWritten : SendKind.Written, createdId);
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
    private static string ScopeStatus(string? grantedScope, IReadOnlyDictionary<string, Tally> tallies)
    {
        if (grantedScope is null)
        {
            return "unknown";
        }

        var missing = tallies
            .Where(t => t.Value.WouldWrite + t.Value.Written > 0 && !EhrWriteScopeMatcher.AllowsCreate(grantedScope, t.Key))
            .Select(t => t.Key)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        return missing.Count == 0 ? "verified" : "missing:" + string.Join(",", missing);
    }

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
        public EhrReferenceResolver Resolver { get; set; } = default!;
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

    private sealed record Decision(EhrShapeOutcome Outcome, JsonObject? Resource, string? Reason)
    {
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
