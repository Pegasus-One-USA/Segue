namespace FHIRBridge.Application.Abstractions.Caching;

/// <summary>
/// The merged, live set of origins the API's "Portal" CORS policy currently allows: the permanent
/// Portal:AllowedOrigins config floor, unioned with rows in AllowedCorsOrigins. Backed by the shared
/// distributed cache, so a change made through the admin screen and <see cref="Invalidate"/> takes
/// effect on the very next request — no restart, and no per-replica staleness in a multi-instance
/// deployment.
/// </summary>
public interface IAllowedCorsOriginsCache
{
    Task<IReadOnlySet<string>> GetOriginsAsync(CancellationToken cancellationToken);

    void Invalidate();
}
