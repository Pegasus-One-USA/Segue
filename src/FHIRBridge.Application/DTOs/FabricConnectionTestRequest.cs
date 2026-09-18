namespace FHIRBridge.Application.DTOs;

/// <summary>
/// An ad-hoc Microsoft Fabric connection to test from the destination form's Test Connection button, before
/// anything is saved. Mirrors <see cref="BlobConnectionTestRequest"/>'s shape, minus the auth modes OneLake does
/// not accept: there is no account key or SAS here, because OneLake takes Entra tokens only.
/// </summary>
public sealed record FabricConnectionTestRequest(
    /// <summary>"oneLakeFiles" or "warehouseTable". Decides which endpoints the test actually probes.</summary>
    string Mode,
    string AuthMode,
    string Workspace,
    string ItemName,
    string? ItemType,
    string? Secret,
    string? TenantId,
    string? ClientId,
    string? ManagedIdentityClientId,
    string? EndpointSuffix,
    string? AuthorityHost,
    string? AccountUrl,
    /// <summary>Warehouse mode only — the TDS endpoint, a different service from OneLake.</summary>
    string? WarehouseSqlEndpoint = null,
    /// <summary>Warehouse mode only — the Lakehouse a COPY INTO stages through.</summary>
    string? WarehouseStagingLakehouse = null,
    // When re-testing an already-saved destination without retyping its secret, Secret is blank and this carries
    // the destination's id so the test service can resolve its stored secret via ISecretProvider instead.
    Guid? DestinationId = null);

/// <summary>
/// Result of a Fabric connection test. Richer than <c>ConnectionTestResultDto</c> because Fabric has two
/// independent things that can fail — reaching OneLake, and reaching the Warehouse's TDS endpoint — and they use
/// different token audiences. Reporting them separately is the point: a single "failed" hides which half is
/// misconfigured, and in practice the two failures look identical to a user.
/// </summary>
public sealed record FabricConnectionTestResultDto(
    bool Connected,
    string? Error,
    /// <summary>Whether the OneLake workspace was reachable with the supplied identity.</summary>
    bool OneLakeReachable,
    /// <summary>Null when not applicable (OneLake Files mode does not touch the Warehouse endpoint).</summary>
    bool? WarehouseReachable,
    /// <summary>
    /// Set when the identity authenticated but was refused. Called out on its own because it is the expected
    /// first failure for a new Fabric setup: OneLake authorizes through Fabric's own workspace permissions, not
    /// Azure RBAC, so a storage-account role assignment does nothing and the user needs telling that plainly.
    /// </summary>
    string? PermissionHint);
