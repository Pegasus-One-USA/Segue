using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class ConfiguredPipelineRunRecord : Entity<Guid>
{
    private ConfiguredPipelineRunRecord()
    {
    }

    public ConfiguredPipelineRunRecord(
        Guid id,
        string status,
        string resourceTypes,
        int extractedResourceCount,
        int mappedRecordCount,
        int writtenRecordCount,
        string errors,
        DateTime startedOnUtc,
        DateTime completedOnUtc,
        string? triggeredBy = null,
        string? triggerType = null)
    {
        Id = id;
        Status = status;
        ResourceTypes = resourceTypes;
        ExtractedResourceCount = extractedResourceCount;
        MappedRecordCount = mappedRecordCount;
        WrittenRecordCount = writtenRecordCount;
        Errors = errors;
        StartedOnUtc = startedOnUtc;
        CompletedOnUtc = completedOnUtc;
        TriggeredBy = triggeredBy;
        TriggerType = triggerType;
        IsEnabled = true;
    }

    public string Status { get; private set; } = default!;
    public string ResourceTypes { get; private set; } = default!;
    public int ExtractedResourceCount { get; private set; }
    public int MappedRecordCount { get; private set; }
    public int WrittenRecordCount { get; private set; }
    public string Errors { get; private set; } = default!;
    public DateTime StartedOnUtc { get; private set; }
    public DateTime CompletedOnUtc { get; private set; }
    public bool IsEnabled { get; private set; }

    /// <summary>Who/what launched the run (user audit name, scheduler, or webhook source).</summary>
    public string? TriggeredBy { get; private set; }

    /// <summary>How the run was launched: Manual, Scheduled, Webhook, or Bulk.</summary>
    public string? TriggerType { get; private set; }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    public void SetTrigger(string? triggeredBy, string? triggerType)
    {
        TriggeredBy = triggeredBy;
        TriggerType = triggerType;
    }
}
