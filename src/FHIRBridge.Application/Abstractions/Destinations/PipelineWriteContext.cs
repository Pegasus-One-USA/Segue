namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Per-run information threaded into every <see cref="IConfiguredDestinationWriter.WriteAsync"/> call. Serves two
/// purposes: <see cref="AllowInlineDelivery"/> tells a Download-mode delivery strategy whether the caller of this
/// run actually has an HTTP response to carry bytes back through (only <c>PipelineRunsController.Start</c>'s
/// synchronous, non-bulk branch sets this true — schedules, webhooks, and queued/bulk runs never do), and the
/// remaining fields supply the placeholder values ({{RouteName}}, {{RunDate}}) for email delivery templates.
/// </summary>
public sealed record PipelineWriteContext(
    bool AllowInlineDelivery,
    string RouteName,
    DateTimeOffset RunStartedAtUtc);
