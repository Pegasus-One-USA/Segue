using System.Runtime.CompilerServices;

namespace FHIRBridge.Governance;

/// <summary>
/// "Where was the system when it failed": human-readable names (workflow, node, source, destination, FHIR resource
/// type) that the code doing the work sets as it goes, and the central error capture attaches to any error raised
/// beneath it. Names are admin-defined labels and FHIR type names - never patient data or record identifiers.
/// </summary>
public sealed class ErrorContext
{
    private static readonly AsyncLocal<ErrorContext?> CurrentScope = new();
    private static readonly ConditionalWeakTable<Exception, ErrorContext> Remembered = new();

    public string? WorkflowName { get; private set; }
    public string? NodeName { get; private set; }
    public string? NodeType { get; private set; }
    public string? SourceName { get; private set; }
    public string? DestinationName { get; private set; }
    public string? ResourceType { get; private set; }
    public string? WorkflowId { get; private set; }
    public string? ExecutionId { get; private set; }
    public string? CorrelationId { get; private set; }
    public int? StepNumber { get; private set; }
    public int? TotalSteps { get; private set; }
    public string? Stage { get; private set; }

    /// <summary>Shared by every scope of one run: how many resource-level trace lines it has written.</summary>
    internal RunCounter? ResourceLines { get; private set; }

    internal sealed class RunCounter
    {
        public int Written;
        public int Failures;
    }

    public static ErrorContext? Current => CurrentScope.Value;

    /// <summary>Starts a scope that inherits the parent's names and overrides the ones supplied. Dispose to leave it.</summary>
    public static IDisposable Push(
        string? workflowName = null, string? nodeName = null, string? nodeType = null,
        string? sourceName = null, string? destinationName = null, string? resourceType = null,
        string? workflowId = null, string? executionId = null, string? correlationId = null,
        int? stepNumber = null, int? totalSteps = null, string? stage = null)
    {
        var parent = CurrentScope.Value;
        var scope = new ErrorContext
        {
            WorkflowName = workflowName ?? parent?.WorkflowName,
            NodeName = nodeName ?? parent?.NodeName,
            NodeType = nodeType ?? parent?.NodeType,
            SourceName = sourceName ?? parent?.SourceName,
            DestinationName = destinationName ?? parent?.DestinationName,
            ResourceType = resourceType ?? parent?.ResourceType,
            WorkflowId = workflowId ?? parent?.WorkflowId,
            ExecutionId = executionId ?? parent?.ExecutionId,
            CorrelationId = correlationId ?? parent?.CorrelationId,
            StepNumber = stepNumber ?? parent?.StepNumber,
            TotalSteps = totalSteps ?? parent?.TotalSteps,
            Stage = stage ?? parent?.Stage,
            // A new workflow scope (one that supplies an execution id) starts a fresh budget; children share it.
            ResourceLines = executionId is not null ? new RunCounter() : parent?.ResourceLines,
        };
        CurrentScope.Value = scope;
        return new Restore(parent);
    }

    /// <summary>Adds names to the current scope (no-op outside a scope). Visible to the code that opened the scope,
    /// which is how a node executor tells the orchestrator which source / destination it was working with.</summary>
    public static void Set(
        string? sourceName = null, string? destinationName = null, string? resourceType = null,
        string? nodeName = null)
    {
        var scope = CurrentScope.Value;
        if (scope is null) return;

        // The scope object is shared with everything running under the same node, which may be concurrent: update it
        // atomically. (If two concurrent branches name different resource types, the last writer wins - the names are
        // diagnostic labels, never used for decisions.)
        lock (scope)
        {
            if (!string.IsNullOrWhiteSpace(sourceName)) scope.SourceName = sourceName;
            if (!string.IsNullOrWhiteSpace(destinationName)) scope.DestinationName = destinationName;
            if (!string.IsNullOrWhiteSpace(resourceType)) scope.ResourceType = resourceType;
            if (!string.IsNullOrWhiteSpace(nodeName)) scope.NodeName = nodeName;
        }
    }

    /// <summary>Pins the current names to an exception that is about to leave the scope (e.g. a failing node), so the
    /// capture that happens further up still knows where it came from. The deepest scope wins.</summary>
    public static void Remember(Exception exception)
    {
        var scope = CurrentScope.Value;
        if (scope is null) return;
        Remembered.TryAdd(exception, scope.Snapshot());
    }

    /// <summary>The names to attach to this exception: what was pinned to it, else the current scope.</summary>
    public static ErrorContext? For(Exception exception) =>
        Remembered.TryGetValue(exception, out var pinned) ? pinned : CurrentScope.Value;

    public ErrorContext Snapshot() => (ErrorContext)MemberwiseClone();

    /// <summary>The names / ids as a flat bag (used to carry a workflow trace line across threads).</summary>
    public Dictionary<string, string?> ToProperties() => new()
    {
        ["WorkflowName"] = WorkflowName,
        ["NodeName"] = NodeName,
        ["NodeType"] = NodeType,
        ["SourceName"] = SourceName,
        ["DestinationName"] = DestinationName,
        ["ResourceType"] = ResourceType,
        ["WorkflowId"] = WorkflowId,
        ["ExecutionId"] = ExecutionId,
        ["CorrelationId"] = CorrelationId,
        ["StepNumber"] = StepNumber?.ToString(),
        ["TotalSteps"] = TotalSteps?.ToString(),
        ["Stage"] = Stage,
    };

    private sealed class Restore : IDisposable
    {
        private readonly ErrorContext? _parent;

        public Restore(ErrorContext? parent) => _parent = parent;

        public void Dispose() => CurrentScope.Value = _parent;
    }
}
