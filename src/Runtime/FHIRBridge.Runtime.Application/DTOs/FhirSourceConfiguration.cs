using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Runtime.Application.DTOs;

public sealed record FhirSourceConfiguration(
    RuntimeSourceType SourceType,
    string? Name,
    string? BaseUrl,
    string? TokenEndpoint,
    string? ClientId,
    string? KeyId,
    string? PrivateKeyPem,
    IReadOnlyCollection<string> Scopes,
    int SearchCount = 100,
    int MaxPages = 5,
    Guid? SourceConnectionId = null,
    string? SearchParameters = null,
    string? ClientSecret = null,
    string? AuthorizationEndpoint = null,
    // Application-type (composition) axis: when set it selects the SMART grant/launch flow independently of the
    // vendor. Left null for backward compatibility — the composite token provider then infers the grant from the
    // vendor and the credentials present.
    ApplicationType? ApplicationType = null,
    // Backend System Search REST retrieval config, when the referenced SourceConnection has one. Null means the
    // caller-supplied single resourceType/searchParameters (node config or route projection) drive extraction —
    // unchanged, pre-existing behavior for every source that doesn't set this.
    IReadOnlyCollection<string>? ResourceTypes = null,
    int? MaxRecords = null,
    // Retrieval config's Retry Policy ("none" | "fixed-3" | "exponential") and per-request Timeout (seconds),
    // enforced by SourceNodeExecutor around each SearchAsync call. Null means the connector's own global
    // retry/timeout defaults apply, unchanged — every source that doesn't set these behaves exactly as before.
    string? RetryPolicy = null,
    int? TimeoutSeconds = null,
    // Backend System bulk-export retrieval config, when the referenced SourceConnection selected the "bulk-export"
    // method. RetrievalMethod == "bulk-export" makes the source node executor extract via a FHIR Bulk Data $export
    // (kick off → poll → NDJSON) instead of a paged search. Null on every other source — unchanged search behavior.
    string? RetrievalMethod = null,
    string? ExportScope = null,
    string? GroupId = null,
    IReadOnlyCollection<string>? PatientIds = null,
    string? OutputFormat = null,
    DateTimeOffset? Since = null);
