namespace HealthAppBackend;

/// <summary>
/// Landing table for every sample test API under <see cref="ApiEndpointTestEndpoints"/> — deliberately ONE shared
/// table rather than one per API (matching what <see cref="DataLakeBatchEntity"/> does with three purpose-built
/// tables): the point of these endpoints is to let someone point FHIRBridge's <c>ApiEndpoint</c> destination at
/// each one and see, from a single place, exactly what arrived, whether it passed that API's own validation, and
/// why not when it didn't. Created on startup by <c>DatabaseSchemaReconciler</c> from this model — no migration
/// needed.
/// </summary>
public sealed class ApiTestCallEntity
{
    public int Id { get; set; }

    /// <summary>Which sample API received this call — see <see cref="ApiEndpointTestEndpoints"/>'s
    /// <c>SingleRecord</c>/<c>RecordsBatch</c>/<c>RecordsEnvelope</c>/<c>CustomEvent</c> constants, or, for a call
    /// landing on one of <see cref="ApiAuthTestEndpoints"/>'s routes, "Auth:&lt;mode&gt;".</summary>
    public string ApiName { get; set; } = string.Empty;

    /// <summary>The exact request body as received, verbatim — no reformatting, no re-serialization.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Which auth mode this call was checked against — null for the four payload-shape endpoints (always
    /// anonymous), one of <see cref="ApiAuthTestEndpoints"/>'s mode names for a call to <c>/api/apitest/auth/*</c>.</summary>
    public string? AuthMode { get; set; }

    /// <summary>The HTTP method the caller actually used — the four payload-shape endpoints and every
    /// <c>/api/apitest/auth/*</c> route accept POST/PUT/PATCH/DELETE, matching the methods FHIRBridge's
    /// ApiEndpoint destination itself supports.</summary>
    public string HttpMethod { get; set; } = "POST";

    public bool IsValid { get; set; }

    /// <summary>Why validation failed, or null when <see cref="IsValid"/> is true.</summary>
    public string? ErrorMessage { get; set; }

    public DateTime ReceivedOnUtc { get; set; }
}
