namespace FHIRBridge.Runtime.Application.Workflows.Payloads;

public sealed record DestinationWriteResult(string DestinationId, int RecordsWritten, DateTimeOffset WrittenAt);
