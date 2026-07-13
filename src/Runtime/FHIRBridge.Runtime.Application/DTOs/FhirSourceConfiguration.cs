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
    // Disambiguates which stored interactive OAuth session (SmartAuthorizationCodeTokenProvider keys its token
    // cache per source connection + patient) this run should use, when more than one patient has ever launched
    // against the same interactive (Standalone/EhrLaunch/Patient) source connection. Null means "whichever session
    // logged in most recently" (the pre-existing, single-slot behavior) — every caller that doesn't set this
    // behaves exactly as before.
    string? TargetPatientId = null);
