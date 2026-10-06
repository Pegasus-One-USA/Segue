using System.Text;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Governance;

namespace FHIRBridge.Application.Services.Governance;

/// <summary>One line of an error's run timeline.</summary>
public sealed record ErrorCorrelationEventDto(
    DateTime OccurredUtc,
    string Source,
    string Title,
    string? Status,
    string? Detail);

/// <summary>Everything that happened around one error's correlation id, flattened into a time-ordered timeline.</summary>
public sealed record ErrorCorrelationDto(
    string CorrelationId,
    string? RunStatus,
    DateTime? RunStartedUtc,
    DateTime? RunCompletedUtc,
    int? ExtractedCount,
    int? MappedCount,
    int? WrittenCount,
    IReadOnlyList<ErrorCorrelationEventDto> Events,
    bool Truncated);

/// <summary>
/// Turns the product's Correlation Search result into a compact, summary-only timeline. Never copies payloads:
/// no audit old/new JSON, no validation warning text, no user e-mails, IPs, patient ids or notification
/// recipients. <paramref name="includeSecurityLogs"/> adds audit / authentication / security / authorization /
/// data-access / SMART-launch lines (titles and outcomes only) and is used for the client's own screen; the
/// exported (shared) report never includes them. All free text passes through <see cref="IErrorScrubber"/>.
/// </summary>
public static class ErrorCorrelationBuilder
{
    public const int MaxEvents = 200;
    private const int MaxDetail = 400;

    public static ErrorCorrelationDto Build(
        CorrelationSearchResultDto result, IErrorScrubber scrubber, bool includeSecurityLogs)
    {
        var events = new List<ErrorCorrelationEventDto>();

        string? Clean(string? text) =>
            string.IsNullOrWhiteSpace(text) ? null : Limit(scrubber.ScrubText(text));

        void Add(DateTime at, string source, string title, string? status, string? detail) =>
            events.Add(new ErrorCorrelationEventDto(
                DateTime.SpecifyKind(at, DateTimeKind.Utc), source, Limit(scrubber.ScrubText(title)), status, Clean(detail)));

        foreach (var w in result.WorkflowRuns)
            Add(w.StartedAt.UtcDateTime, "Workflow", "Workflow run started", w.Status,
                Join(w.TriggerType is null ? null : $"Trigger: {w.TriggerType}", w.ErrorMessage));

        foreach (var s in result.SchedulerHistory)
            Add(s.RunTimeUtc, "Scheduler", $"Scheduler dispatch ({s.SchedulerId})", s.Status, $"{s.RouteCount} route(s)");

        foreach (var r in result.RetryHistory)
            Add(r.OccurredOnUtc, "Retry", $"Retry #{r.RetryNumber}: {r.Context}", null, $"Waited {r.DelayMilliseconds} ms. {r.Reason}");

        foreach (var e in result.Errors)
        {
            if (e.Severity == "WorkflowDebug")
            {
                // The step-by-step trace: the message already reads "Step 2/5 [Source] ... started / FAILED ...".
                Add(e.OccurredOnUtc, "Workflow trace", e.Message, null, null);
                continue;
            }

            Add(e.OccurredOnUtc, "Error", $"{e.ExceptionType}", e.Severity,
                Join(e.ErrorReferenceId is null ? null : $"Ref {e.ErrorReferenceId}", e.Module, e.Message));
        }

        foreach (var a in result.ApiRequests)
            Add(a.OccurredOnUtc, a.Direction == "Inbound" ? "API (inbound)" : "API (outbound)",
                $"{a.Method} {a.Url}", a.StatusCode?.ToString(), Join($"{a.DurationMs} ms", a.Error));

        foreach (var x in result.Exports)
            Add(x.OccurredOnUtc, "Export", $"Export to {x.DestinationName} ({x.Format})", x.Status, $"{x.RowCount} row(s)");

        foreach (var d in result.DestinationActivity)
            Add(d.OccurredOnUtc, "Destination", $"{d.DestinationName}: {d.Stage}", d.Status,
                Join(d.ResourceType, d.WrittenCount is null ? null : $"{d.WrittenCount}/{d.RecordCount} written", $"{d.DurationMs} ms", d.Detail, d.Error));

        foreach (var v in result.ValidationFailures)
            Add(v.OccurredOnUtc, "Validation", $"Validation warnings: {v.ResourceType}", null,
                v.DataQualityScore is null ? null : $"Data quality score {v.DataQualityScore:0.##}");

        foreach (var n in result.Notifications)
            Add(n.OccurredOnUtc, "Notification", $"Notification ({n.NotificationType})", n.Status, n.Error);

        if (includeSecurityLogs)
        {
            foreach (var a in result.AuditLogs)
                Add(a.OccurredOnUtc, "Audit", $"{a.Action} {a.EntityType} {a.EntityName}".Trim(), a.Status, a.Module);
            foreach (var a in result.AuthenticationLogs)
                Add(a.OccurredOnUtc, "Authentication", a.AuthenticationType, a.Success ? "Success" : "Failed", a.FailureReason);
            foreach (var s in result.SecurityEvents)
                Add(s.OccurredOnUtc, "Security", s.EventType, s.Severity, s.Details);
            foreach (var a in result.AuthorizationLogs)
                Add(a.OccurredOnUtc, "Authorization", $"{a.PermissionCode} on {a.RequestPath}", a.Result, null);
            foreach (var d in result.DataAccessLogs)
                Add(d.OccurredOnUtc, "Data access", $"{d.Action} {d.ResourceType}", null, d.Purpose);
            foreach (var l in result.SmartLaunchLogs)
                Add(l.OccurredOnUtc, "SMART launch", $"{l.LaunchType} launch ({l.SourceName})", l.Success ? "Success" : "Failed", l.FailureReason);
        }

        var ordered = events.OrderBy(x => x.OccurredUtc).ToList();
        var truncated = ordered.Count > MaxEvents;
        if (truncated)
        {
            // Never lose the part that matters: keep everything that is not a step-by-step trace line (errors, API
            // calls, exports...), the first few trace lines (how the run began) and the LAST trace lines (where it
            // stopped), and drop the middle of the trace first.
            const string Trace = "Workflow trace";
            var others = ordered.Count(e => e.Source != Trace);
            var traceBudget = Math.Max(20, MaxEvents - others);
            var trace = ordered.Where(e => e.Source == Trace).ToList();
            var head = Math.Min(10, traceBudget / 4);
            var keepTrace = trace.Take(head).Concat(trace.Skip(Math.Max(head, trace.Count - (traceBudget - head)))).ToHashSet();
            ordered = ordered.Where(e => e.Source != Trace || keepTrace.Contains(e)).ToList();
            if (ordered.Count > MaxEvents)
            {
                // Still too many non-trace events: keep the earliest and the latest halves.
                ordered = ordered.Take(MaxEvents / 2).Concat(ordered.Skip(ordered.Count - MaxEvents / 2)).ToList();
            }
        }

        var run = result.PipelineRun;
        return new ErrorCorrelationDto(
            result.CorrelationId,
            run?.Status,
            run?.StartedOnUtc,
            run?.CompletedOnUtc,
            run?.ExtractedResourceCount,
            run?.MappedRecordCount,
            run?.WrittenRecordCount,
            ordered,
            truncated);
    }

    private static string? Join(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return kept.Length == 0 ? null : string.Join(" · ", kept);
    }

    private static string Limit(string text) => text.Length <= MaxDetail ? text : text[..MaxDetail] + "…";
}

/// <summary>Plain-text form of a run timeline (CSV cell, Application Insights property).</summary>
public static class ErrorCorrelationText
{
    // Spreadsheets cap a cell at 32,767 characters.
    public const int DefaultMaxLength = 30_000;

    /// <summary>
    /// A "RUN | status | started | completed | extracted/mapped/written" line, then one
    /// "time | source | title | status | detail" line per event. Fields never contain a pipe or a line break, so the
    /// License Server can read it back.
    /// </summary>
    public static string Format(ErrorCorrelationDto c, int maxLength = DefaultMaxLength)
    {
        static string F(string? v) => (v ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Trim();

        var sb = new StringBuilder();
        sb.Append("RUN | ").Append(F(c.RunStatus)).Append(" | ")
          .Append(c.RunStartedUtc?.ToString("O")).Append(" | ").Append(c.RunCompletedUtc?.ToString("O")).Append(" | ")
          .Append(c.ExtractedCount).Append('/').Append(c.MappedCount).Append('/').Append(c.WrittenCount).Append('\n');

        foreach (var e in c.Events)
        {
            var line = $"{e.OccurredUtc:O} | {F(e.Source)} | {F(e.Title)} | {F(e.Status)} | {F(e.Detail)}\n";
            if (sb.Length + line.Length > maxLength)
            {
                sb.Append("... more events omitted (size limit)");
                break;
            }

            sb.Append(line);
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Supplies the safe (no audit / authentication / security logs) run timeline as text to the central error capture,
/// so it can travel with the error to Application Insights. Scoped: it reads the governance tables.
/// </summary>
public sealed class ErrorCorrelationSource : IErrorCorrelationSource
{
    private const int MaxEventsInTelemetry = 100;
    private const int MaxLength = 24_000;

    private readonly IGovernanceQueryService _query;
    private readonly IErrorScrubber _scrubber;

    public ErrorCorrelationSource(IGovernanceQueryService query, IErrorScrubber scrubber)
    {
        _query = query;
        _scrubber = scrubber;
    }

    public async Task<string?> GetTimelineTextAsync(string correlationId, CancellationToken cancellationToken)
    {
        var result = await _query.GetCorrelationSearchResultAsync(correlationId, cancellationToken);
        var timeline = ErrorCorrelationBuilder.Build(result, _scrubber, includeSecurityLogs: false);
        if (timeline.Events.Count > MaxEventsInTelemetry)
        {
            timeline = timeline with { Events = timeline.Events.Take(MaxEventsInTelemetry).ToList(), Truncated = true };
        }

        return timeline.Events.Count == 0 && timeline.RunStatus is null ? null : ErrorCorrelationText.Format(timeline, MaxLength);
    }
}
