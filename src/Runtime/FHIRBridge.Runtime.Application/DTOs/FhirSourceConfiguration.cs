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
    DateTimeOffset? Since = null,
    // Disambiguates which stored interactive OAuth session (SmartAuthorizationCodeTokenProvider keys its token
    // cache per source connection + patient) this run should use, when more than one patient has ever launched
    // against the same interactive (Standalone/EhrLaunch/Patient) source connection. Null means "whichever session
    // logged in most recently" (the pre-existing, single-slot behavior) — every caller that doesn't set this
    // behaves exactly as before.
    string? TargetPatientId = null,
    // Request-time raw Patient search criteria, threaded from WorkflowRunRequest through WorkflowExecutionContext —
    // lets a third-party app's own search UI filter which patients a Patient-resource search matches (e.g.
    // "active=true", "identifier=MRN12345", "family=Smith&given=John", "birthdate=1990-01-01", "_count=100"),
    // instead of relying on the EHR's own interactive patient picker. Passed through to the Patient resource type's
    // search as-is (FhirSourceConnectorBase.ApplyPatientScopeAsync); every other configured resource type is
    // unaffected. Null/blank (the default) preserves existing behavior for every caller that doesn't set it.
    string? PatientSearchCriteria = null,
    // Identifies the logged-in end user of the calling third-party app (e.g. HealthApp's Patient Standalone
    // session), independent of which SourceConnection/pipeline is invoked. For ApplicationType.Patient,
    // SmartAuthorizationCodeTokenProvider.BuildStoreKey keys the interactive OAuth token store on this instead of
    // SourceConnectionId, so every pipeline that shares the same logged-in user's session reuses the one token that
    // user's authorization already covers, rather than needing its own separate MyChart consent. Null preserves the
    // pre-existing per-SourceConnection keying for every other ApplicationType and for callers that don't supply it.
    string? CallerId = null,
    // Per-resource-type incremental sync watermark ("_lastUpdated" cursor), keyed by resource type — search REST
    // fetches each resource type via its own independent request, so each tracks its own cursor rather than sharing
    // one connection-wide value (contrast with Since above, bulk export's single job-level cursor). Null when
    // incremental sync isn't enabled, or the referenced SourceConnection has no retrieval config at all.
    IReadOnlyDictionary<string, DateTime>? LastUpdatedWatermarks = null,
    // Tenant-scoping identifier some vendors require on every request (e.g. athenahealth's numeric practice id).
    // Null for every vendor that doesn't need per-tenant request scoping — unchanged behavior for all of them.
    string? PracticeId = null,
    // Where OAuth2ClientCredentialsTokenProvider places client id/secret — "post" (default) or "basic". Null
    // behaves as "post", unchanged from before this field existed.
    string? AuthPlacement = null);
