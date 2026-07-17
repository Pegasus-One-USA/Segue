namespace FHIRBridge.Application.Abstractions.Caching;

/// <summary>
/// The merged, live set of origins the API's "Portal" CORS policy currently allows: the permanent
/// Portal:AllowedOrigins config floor, unioned with rows in AllowedCorsOrigins. Backed by a single
/// in-process cache (correct for the current single-instance deployment) that <see cref="Invalidate"/>
/// clears so a change made through the admin screen takes effect on the very next request — no restart.
/// </summary>
public interface IAllowedCorsOriginsCache
{
    Task<IReadOnlySet<string>> GetOriginsAsync(CancellationToken cancellationToken);

    void Invalidate();
}
