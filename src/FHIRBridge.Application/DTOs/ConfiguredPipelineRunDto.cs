namespace FHIRBridge.Application.DTOs;

public sealed record ConfiguredPipelineRunDto(
    Guid Id,
    string Status,
    IReadOnlyList<string> ResourceTypes,
    int ExtractedResourceCount,
    int MappedRecordCount,
    int WrittenRecordCount,
    IReadOnlyList<string> Errors,
    DateTime StartedOnUtc,
    DateTime CompletedOnUtc,
    bool IsEnabled = true,
    string? TriggeredBy = null,
    string? TriggerType = null,
    GeneratedFileDto? InlineDownload = null,
    IReadOnlyList<string>? DownloadUrls = null);

public sealed record GeneratedFileDto(string FileName, string ContentType, byte[] Content);
