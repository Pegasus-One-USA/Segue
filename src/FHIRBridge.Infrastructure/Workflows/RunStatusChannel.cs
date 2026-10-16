using System.Text.Json;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using StackExchange.Redis;

namespace FHIRBridge.Infrastructure.Workflows;

/// <summary>
/// The Redis pub/sub channel that carries a workflow run's status change from the Worker to the API, and the one
/// wire format both ends agree on. The Worker has no SignalR hub of its own, so a run it finishes (resumed after a
/// bulk <c>$export</c>, or scheduled) is published here by <see cref="RedisRunStatusPublisher"/>, and
/// <see cref="RedisRunStatusRelay"/> in the API hands it to the same <see cref="IRunStatusNotifier"/> that pushes the
/// API's own runs to the browser.
/// </summary>
public static class RunStatusChannel
{
    public static readonly RedisChannel Name = RedisChannel.Literal("fhirbridge:run-status-changed");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> KnownStatuses = new(Enum.GetNames<WorkflowRunStatus>(), StringComparer.Ordinal);

    public static string Serialize(RunStatusChangedEvent statusEvent)
        => JsonSerializer.Serialize(statusEvent, JsonOptions);

    /// <summary>Reads a message off the channel. False, with <paramref name="reason"/> set, for anything that is not
    /// a complete status event: unreadable JSON, a missing run or workflow id, or a status the run model does not
    /// have. Such a message is dropped, never forwarded to the browser.</summary>
    public static bool TryDeserialize(string? payload, out RunStatusChangedEvent? statusEvent, out string? reason)
    {
        statusEvent = null;
        reason = null;

        if (string.IsNullOrWhiteSpace(payload))
        {
            reason = "empty message";
            return false;
        }

        RunStatusChangedEvent? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RunStatusChangedEvent>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            reason = "not a run status event";
            return false;
        }

        if (parsed is null || parsed.WorkflowRunId == Guid.Empty || parsed.WorkflowDefinitionId == Guid.Empty)
        {
            reason = "missing run or workflow id";
            return false;
        }

        if (parsed.Status is null || !KnownStatuses.Contains(parsed.Status))
        {
            reason = "unknown status";
            return false;
        }

        statusEvent = parsed;
        return true;
    }
}
