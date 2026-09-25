namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Per-run information threaded into every <see cref="IConfiguredDestinationWriter.WriteAsync"/> call. Serves two
/// purposes: <see cref="AllowInlineDelivery"/> tells a Download-mode delivery strategy whether the caller of this
/// run actually has an HTTP response to carry bytes back through (only <c>PipelineRunsController.Start</c>'s
/// synchronous, non-bulk branch sets this true — schedules, webhooks, and queued/bulk runs never do), and the
/// remaining fields supply the placeholder values ({{RouteName}}, {{RunDate}}) for email delivery templates.
/// </summary>
/// <param name="FetchMissingReferenceAsync">
/// Optional, source-agnostic hook a writer can call to fetch one specific resource by type/id directly from
/// whatever EHR source fed this run — e.g. <c>MappedFhirRepositoryDestinationWriter</c> uses it to resolve a
/// reference confirmed missing from both the batch and the destination, instead of only blocking the referencing
/// record (see its opt-in <c>dest_autoFetchMissingReferences</c> flag). Returns the raw FHIR JSON for the requested
/// resource, or null if it couldn't be fetched (not found, unauthorized, transient failure, or no such hook wired
/// up for this run — e.g. more than one source node feeds this destination). Deliberately typed in plain
/// primitives (not a Runtime-layer resource type) so this stays constructible without <c>FHIRBridge.Application</c>
/// ever depending on the separate Runtime.* layering stack — the Runtime.Infrastructure caller that populates this
/// closes over its own source client/connection internally. Null by default: every destination/writer that never
/// opts into this behaves exactly as before.
/// </param>
/// <param name="SourceBaseUrl">
/// Optional FHIR base URL of the single source feeding this run (e.g. Epic's <c>.../api/FHIR/R4</c>), when exactly
/// one source node does. Lets a writer recognize an ABSOLUTE reference that points back at that same source
/// (<c>https://fhir.epic.com/.../R4/Observation/abc</c>) as the relative <c>Observation/abc</c> it is, so it takes
/// part in reference resolution/auto-fetch instead of being skipped as external. Null when the source is ambiguous
/// or unknown, in which case every absolute reference keeps being treated as external, exactly as before.
/// </param>
/// <param name="PipelineRunId">
/// This run's own id. Exists specifically for <c>MappedApiEndpointDestinationWriter</c>'s multi-resource
/// accumulator, which needs to identify the run even when <c>records</c> is empty (a resource type this run
/// genuinely produced zero records for still has to register that fact with the accumulator, or that resource
/// type's slot never completes — see that writer's own remarks) — every other writer instead reads it off
/// <c>MappedDestinationRecord.PipelineRunId</c>, which only exists when there's at least one record. Defaults to
/// <see cref="Guid.Empty"/> for every caller that hasn't been updated to pass the real one; only
/// <c>ConfiguredPipelineService</c> and the Runtime plane's destination executors currently do.
/// </param>
/// <param name="ReportStageAsync">
/// Optional hook a writer calls to report a stage of its own write — currently only opening/authenticating the
/// connection, which the caller cannot observe because <see cref="IConfiguredDestinationWriter.WriteAsync"/> is a
/// single opaque call. The completion/failure of the write as a whole is reported by the caller
/// (<c>LoggingConfiguredDestinationWriter</c>), so a writer never reports that itself.
/// <para>A callback rather than a constructor dependency on purpose: writers are resolved from dependency
/// injection in several places (the writer factory, health-check providers, tests), so widening ~21 constructors
/// to thread a logger through would ripple far past the writers that actually have a connection to report.</para>
/// <para>Null by default, exactly like <paramref name="FetchMissingReferenceAsync"/> — every caller that doesn't
/// supply it and every writer that ignores it behaves exactly as before. A writer must never let a failure from
/// this hook change its own outcome; see <c>DestinationStageReporting.ReportAsync</c>, which swallows for it.</para>
/// </param>
public sealed record PipelineWriteContext(
    bool AllowInlineDelivery,
    string RouteName,
    DateTimeOffset RunStartedAtUtc,
    string? CorrelationId = null,
    Func<string, string, CancellationToken, Task<string?>>? FetchMissingReferenceAsync = null,
    string? SourceBaseUrl = null,
    Guid PipelineRunId = default,
    Func<DestinationStageReport, CancellationToken, Task>? ReportStageAsync = null);

/// <summary>
/// One stage a writer reports through <see cref="PipelineWriteContext.ReportStageAsync"/>. Carries only what the
/// writer itself knows — the caller stamps destination identity, resource type and correlation id on top, so a
/// writer never has to restate them.
/// <para><b>PHI-free:</b> <paramref name="Detail"/> is for short technical context (which half of a two-part
/// connect, a qualified table name), never record data.</para>
/// </summary>
public sealed record DestinationStageReport(
    string Stage,
    string Status,
    long DurationMs = 0,
    string? Detail = null,
    string? Error = null);
