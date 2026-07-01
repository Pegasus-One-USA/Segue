namespace FHIRBridge.Runtime.Application.Workflows.Payloads;

public sealed record AuditResult(string AuditId, DateTimeOffset RecordedAt);
