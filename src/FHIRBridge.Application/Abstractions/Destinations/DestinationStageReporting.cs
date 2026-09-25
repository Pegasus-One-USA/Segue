using System.Diagnostics;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Helpers a writer uses to report its connect stage through <see cref="PipelineWriteContext.ReportStageAsync"/>.
/// <para>Exists so the swallow contract lives in exactly one place. These run <em>inside</em> a writer's own try
/// blocks, where an exception would replace the real outcome of the write — so a governance-write failure must
/// never surface. That is the same reasoning <c>ApiRequestLoggingHandler</c> documents for the outbound HTTP
/// logger, and the same rule <c>LoggingConfiguredDestinationWriter</c> states: this observes, it never changes
/// the pipeline's failure behaviour.</para>
/// </summary>
public static class DestinationStageReporting
{
    /// <summary>
    /// Runs <paramref name="connectAsync"/>, reporting a Connect stage that succeeded or failed. The original
    /// exception always propagates unchanged — the report is pure observation.
    /// </summary>
    public static async Task<T> ReportConnectAsync<T>(
        this PipelineWriteContext context,
        Func<Task<T>> connectAsync,
        CancellationToken cancellationToken,
        string? detail = null)
    {
        // No hook supplied (the default context, and every caller that hasn't opted in): skip the timing work
        // entirely and just do the connect, so an un-instrumented path costs exactly what it did before.
        if (context.ReportStageAsync is null)
        {
            return await connectAsync();
        }

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var result = await connectAsync();
            await SwallowAsync(
                context.ReportStageAsync,
                new DestinationStageReport(
                    DestinationStageNames.Connect,
                    DestinationStageStatuses.Succeeded,
                    ElapsedMs(startedAt),
                    detail),
                cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            await SwallowAsync(
                context.ReportStageAsync,
                new DestinationStageReport(
                    DestinationStageNames.Connect,
                    DestinationStageStatuses.Failed,
                    ElapsedMs(startedAt),
                    detail,
                    exception.Message),
                cancellationToken);
            throw;
        }
    }

    /// <summary>Void-returning overload for a connect that produces no handle of its own (e.g. SFTP's connect).</summary>
    public static Task ReportConnectAsync(
        this PipelineWriteContext context,
        Func<Task> connectAsync,
        CancellationToken cancellationToken,
        string? detail = null)
        => context.ReportConnectAsync<object?>(
            async () =>
            {
                await connectAsync();
                return null;
            },
            cancellationToken,
            detail);

    /// <summary>
    /// Reports a connect that has already succeeded, timed from <paramref name="startedAt"/> (a
    /// <see cref="Stopwatch.GetTimestamp"/> value). For clients whose connect cannot be wrapped as a single
    /// awaited call — because it happens inside a shared helper, or lazily on the first authenticated request —
    /// so the stage is still attributed to the connection rather than to the work that follows it.
    /// <para>Never throws: a write must not fail because its governance row could not be written.</para>
    /// </summary>
    public static async Task ReportConnectedAsync(
        this PipelineWriteContext context,
        long startedAt,
        CancellationToken cancellationToken,
        string? detail = null)
    {
        if (context.ReportStageAsync is null)
        {
            return;
        }

        await SwallowAsync(
            context.ReportStageAsync,
            new DestinationStageReport(
                DestinationStageNames.Connect,
                DestinationStageStatuses.Succeeded,
                ElapsedMs(startedAt),
                detail),
            cancellationToken);
    }

    /// <summary>
    /// Invokes a reporting hook, discarding any failure. Deliberately catches everything: a schema drift, a
    /// transient database outage or a full disk must not convert a successful destination write into a failure,
    /// and nothing is rethrown or re-logged through a second channel that could fail the same way.
    /// <para>The hook is <em>invoked</em> inside the try, not merely awaited there: a delegate that throws
    /// synchronously — before it ever returns a Task — would otherwise escape this entirely, which is precisely
    /// the case where the caller is already handling a failure of its own and must not be handed a second one.</para>
    /// </summary>
    private static async Task SwallowAsync(
        Func<DestinationStageReport, CancellationToken, Task> reportStageAsync,
        DestinationStageReport report,
        CancellationToken cancellationToken)
    {
        try
        {
            await reportStageAsync(report, cancellationToken);
        }
        catch
        {
            // Intentionally ignored — see the summary above.
        }
    }

    private static long ElapsedMs(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}

/// <summary>
/// Stage names a writer reports. Mirrors <c>DestinationActivityStage</c> in the Domain layer — duplicated rather
/// than referenced because these are the values an Application-layer contract passes, and the Domain entity owns
/// its own persisted vocabulary.
/// </summary>
public static class DestinationStageNames
{
    public const string Connect = "Connect";
    public const string Complete = "Complete";
}

/// <summary>Status values a writer reports. Mirrors <c>DestinationActivityStatus</c>.</summary>
public static class DestinationStageStatuses
{
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string PartialSuccess = "PartialSuccess";
    public const string NoData = "NoData";
}
