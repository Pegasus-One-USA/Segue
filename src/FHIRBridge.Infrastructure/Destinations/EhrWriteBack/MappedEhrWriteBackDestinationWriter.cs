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
/// <para><b>Dry run only, for now.</b> Phase 1 of write-back ships the whole path except the send: every run is
/// treated as a dry run whatever the destination says, so no request that changes an EHR can leave this writer until
/// <see cref="LiveWritesReleased"/> is turned on in Phase 2. Reads (identifier search, <c>$match</c>, encounter
/// search) do run, because resolving references is what a dry run is for.</para>
///
/// <para><b>PHI.</b> The report and every record error hold resource types, positions and reason codes only. Nothing
/// read from a resource, a search result or an OperationOutcome's diagnostics is logged.</para>
/// </summary>
public sealed class MappedEhrWriteBackDestinationWriter : IConfiguredDestinationWriter
{
    /// <summary>Off until Phase 2 verifies the send path against the sandbox. See the class remarks.</summary>
    internal static readonly bool LiveWritesReleased = false;

    private readonly EhrWriteProfileRegistry _profiles;
    private readonly IEhrWriteLedgerRepository _ledger;
    private readonly ILogger<MappedEhrWriteBackDestinationWriter> _logger;

    public MappedEhrWriteBackDestinationWriter(
        EhrWriteProfileRegistry profiles,
        IEhrWriteLedgerRepository ledger,
        ILogger<MappedEhrWriteBackDestinationWriter> logger)
    {
        _profiles = profiles;
        _ledger = ledger;
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

        var dryRun = channel.Options.DryRun || !LiveWritesReleased;
        var targetKey = EhrWriteKeys.TargetKey(channel.TargetBaseUrl);
        // Reading from and writing to the same EHR: every record is already there. Epic does not deduplicate
        // allergies, problems or notes, so sending them back would file second copies in the same chart.
        var sameEnvironment = EhrWriteKeys.SameEnvironment(context.SourceBaseUrl, channel.TargetBaseUrl);
        var parsed = Parse(records);
        var selected = new HashSet<string>(channel.Options.ResourceTypes, StringComparer.OrdinalIgnoreCase);
        var tallies = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var recordErrors = new List<string>();
        var writtenIds = new List<string?>();

        var resolver = new EhrReferenceResolver(
            channel,
            _ledger,
            _profiles.Find(vendor.Vendor, "Patient"),
            vendor,
            targetKey,
            context.SourceBaseUrl,
            parsed.Where(r => r.ResourceType == "Patient" && r.SourceId is not null)
                .GroupBy(r => r.SourceId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Resource, StringComparer.Ordinal),
            context.FetchMissingReferenceAsync);

        var grantedScope = await TryGetGrantedScopeAsync(channel, cancellationToken);
        var plannedWrites = 0;

        // Patients first, so a record's patient is already resolved, and cached, when the record is reached.
        foreach (var record in parsed.OrderBy(r => r.ResourceType == "Patient" ? 0 : 1).ThenBy(r => r.Index))
        {
            var tally = TallyFor(tallies, record.ResourceType);
            tally.Received++;

            if (sameEnvironment)
            {
                tally.AlreadyWritten++;
                tally.Reason("already-in-ehr");
                continue;
            }

            var decision = await DecideAsync(record, channel, vendor, selected, resolver, cancellationToken);
            if (decision.Outcome != EhrShapeOutcome.Shaped || decision.Resource is null)
            {
                tally.Count(decision.Outcome, decision.Reason!);
                if (decision.Outcome == EhrShapeOutcome.Rejected)
                {
                    recordErrors.Add($"{record.ResourceType} #{record.Index + 1}: {decision.Reason}");
                }

                continue;
            }

            var sourceKey = EhrWriteKeys.SourceKey(context.SourceBaseUrl, record.ResourceType, record.SourceId!);
            var contentHash = EhrWriteKeys.ContentHash(decision.Resource);
            var existing = (await _ledger.FindAsync(targetKey, record.ResourceType, [sourceKey], cancellationToken))
                .GetValueOrDefault(sourceKey);
            if (LedgerBlocks(existing, contentHash) is { } ledgerReason)
            {
                tally.AlreadyWritten++;
                tally.Reason(ledgerReason);
                continue;
            }

            if (plannedWrites >= channel.Options.MaxWritesPerRun)
            {
                tally.Count(EhrShapeOutcome.Skipped, "write-cap-reached");
                continue;
            }

            plannedWrites++;
            if (dryRun)
            {
                tally.WouldWrite++;
                continue;
            }

            await SendAsync(record, decision.Resource, existing, sourceKey, contentHash, targetKey, channel, vendor, context, tally, recordErrors, writtenIds, cancellationToken);
        }

        var report = new EhrWriteReport(
            dryRun,
            channel.TargetVendor.ToString(),
            parsed.Count,
            ScopeStatus(grantedScope, tallies),
            tallies.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Value.ToSummary(t.Key)).ToList());

        _logger.LogInformation(
            "EHR write-back to {Vendor} for destination {DestinationName}: dry run {DryRun}, {Received} records, " +
            "{WouldWrite} would write, {Written} written, {AlreadyWritten} already written, {Skipped} skipped, " +
            "{Rejected} rejected, {Unknown} unknown, scope {ScopeStatus}.",
            report.TargetVendor, destination.Name, report.DryRun, report.RecordsReceived,
            report.Resources.Sum(r => r.WouldWrite), report.Resources.Sum(r => r.Written),
            report.Resources.Sum(r => r.AlreadyWritten), report.Resources.Sum(r => r.Skipped),
            report.Resources.Sum(r => r.Rejected), report.Resources.Sum(r => r.Unknown), report.ScopeStatus);

        // An empty list, not null: a null WrittenResourceIds makes the configured plane treat the whole batch as
        // stored, and a dry run stored nothing.
        return new DestinationWriteResult(
            Count: writtenIds.Count,
            RecordErrors: recordErrors.Count > 0 ? recordErrors : null,
            WrittenResourceIds: writtenIds,
            EhrWrite: report);
    }

    private async Task<Decision> DecideAsync(
        ParsedRecord record,
        IEhrWriteChannel channel,
        EhrWriteVendorProfile vendor,
        HashSet<string> selected,
        EhrReferenceResolver resolver,
        CancellationToken cancellationToken)
    {
        if (record.ParseProblem is not null)
        {
            return Decision.Reject(record.ParseProblem);
        }

        // Deny by default: a destination that selected nothing writes nothing.
        if (!selected.Contains(record.ResourceType))
        {
            return Decision.Skip("not-selected");
        }

        // Without a source id the ledger cannot recognise the record next time, so it would be filed again.
        if (string.IsNullOrWhiteSpace(record.SourceId))
        {
            return Decision.Reject("missing-id");
        }

        var capability = vendor.Capabilities.FirstOrDefault(c => c.ResourceType == record.ResourceType);
        if (capability is null || !capability.Supports(EhrWriteOperation.Create))
        {
            return Decision.Skip("not-writable");
        }

        if (record.ResourceType == "Patient")
        {
            return await DecidePatientAsync(record, resolver, cancellationToken);
        }

        var profile = _profiles.Find(vendor.Vendor, record.ResourceType);
        if (profile is null)
        {
            return Decision.Skip("not-writable");
        }

        var shaped = profile.Shape(record.Resource, channel.Options);
        if (shaped.Outcome != EhrShapeOutcome.Shaped || shaped.Resource is null)
        {
            return new Decision(shaped.Outcome, null, shaped.Reason);
        }

        var patient = await resolver.ResolvePatientAsync(shaped.SourcePatientReference, cancellationToken);
        if (patient.Kind == EhrPatientResolutionKind.Unresolved)
        {
            return Decision.Skip(patient.Reason ?? "patient-unresolved");
        }

        if (patient.Kind == EhrPatientResolutionKind.WouldCreate)
        {
            // The patient does not exist yet, so neither does any encounter of theirs.
            return capability.RequiresEncounter
                ? Decision.Skip("no-eligible-encounter")
                : Decision.Skip("patient-not-yet-created");
        }

        string? encounterId = null;
        if (capability.RequiresEncounter)
        {
            var openOnly = capability.Variant == EhrWriteVariants.VitalSigns;
            encounterId = await resolver.ResolveEncounterAsync(
                patient.TargetPatientId!, shaped.SourceEncounterReference, openOnly, cancellationToken);
            if (encounterId is null)
            {
                return Decision.Skip("no-eligible-encounter");
            }
        }

        profile.BindReferences(shaped.Resource, patient.TargetPatientId!, encounterId);
        return new Decision(EhrShapeOutcome.Shaped, shaped.Resource, null);
    }

    /// <summary>A source patient is written only when the EHR has no such patient and the destination opted in;
    /// a patient the EHR already has is "already written", which is what the destination wanted.</summary>
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
            EhrPatientResolutionKind.Resolved => Decision.Skip("patient-already-in-ehr"),
            EhrPatientResolutionKind.WouldCreate when resolution.ShapedPatient is not null =>
                new Decision(EhrShapeOutcome.Shaped, resolution.ShapedPatient, null),
            _ => Decision.Skip(resolution.Reason ?? "patient-unresolved"),
        };
    }

    /// <summary>Why the ledger stops this record being sent again, or null when it may be sent.</summary>
    private static string? LedgerBlocks(EhrWriteLedgerEntry? existing, string contentHash)
    {
        if (existing is null)
        {
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

    private async Task SendAsync(
        ParsedRecord record,
        JsonObject shaped,
        EhrWriteLedgerEntry? existing,
        string sourceKey,
        string contentHash,
        string targetKey,
        IEhrWriteChannel channel,
        EhrWriteVendorProfile vendor,
        PipelineWriteContext context,
        Tally tally,
        List<string> recordErrors,
        List<string?> writtenIds,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var entry = existing;
        if (entry is null)
        {
            entry = new EhrWriteLedgerEntry(
                targetKey, channel.TargetConnectionId, record.ResourceType, sourceKey, contentHash,
                EhrWriteOperation.Create, channel.DestinationId, context.PipelineRunId == Guid.Empty ? null : context.PipelineRunId, now);
            if (!await _ledger.TryAddAsync(entry, cancellationToken))
            {
                // Another run claimed this record first.
                tally.AlreadyWritten++;
                tally.Reason("awaiting-review");
                return;
            }
        }

        // The claim is conditional on the row not having changed since it was read (AttemptCount is a concurrency
        // token): two runs retrying the same rejected record cannot both send it.
        entry.MarkSending(contentHash, now);
        if (!await _ledger.TryClaimAsync(entry, cancellationToken))
        {
            tally.AlreadyWritten++;
            tally.Reason("awaiting-review");
            return;
        }

        EhrCreateOutcome outcome;
        try
        {
            outcome = await channel.CreateAsync(record.ResourceType, shaped.ToJsonString(), cancellationToken);
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
            return;
        }

        var codes = string.Join(",", outcome.Issues.Select(i => i.VendorCode ?? i.Code).Where(c => c is not null).Distinct());
        var alreadyThere = outcome.Issues.Any(i => vendor.IsAlreadyAtTarget(i.VendorCode, i.Expression));
        switch (outcome.Kind)
        {
            case EhrCreateKind.Created when outcome.ResourceId is { Length: > 0 } id:
                entry.MarkWritten(id, outcome.HttpStatus ?? 201, DateTime.UtcNow);
                tally.Written++;
                writtenIds.Add(record.SourceId);
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
                recordErrors.Add($"{record.ResourceType} #{record.Index + 1}: rejected by the EHR ({outcome.HttpStatus}{(codes.Length > 0 ? ", " + codes : string.Empty)})");
                break;
            default:
                entry.MarkUnknown(outcome.HttpStatus, codes, DateTime.UtcNow);
                tally.Unknown++;
                tally.Reason("outcome-unknown");
                break;
        }

        await _ledger.SaveChangesAsync(CancellationToken.None);
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

    private static Tally TallyFor(Dictionary<string, Tally> tallies, string resourceType)
    {
        if (!tallies.TryGetValue(resourceType, out var tally))
        {
            tally = new Tally();
            tallies[resourceType] = tally;
        }

        return tally;
    }

    private sealed record ParsedRecord(int Index, string ResourceType, string? SourceId, JsonObject Resource, string? ParseProblem);

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
