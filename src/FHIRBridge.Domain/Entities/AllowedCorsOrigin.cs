using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A portal origin a SuperAdmin has explicitly allowed through the API's CORS policy, in addition to
/// the permanent floor configured in Portal:AllowedOrigins. Global — not scoped to a Tenant, since CORS
/// is decided by the API host before any tenant is known from the request.
/// </summary>
public sealed class AllowedCorsOrigin : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private AllowedCorsOrigin()
    {
    }

    public AllowedCorsOrigin(string originUrl, string? label)
    {
        Id = Guid.NewGuid();
        OriginUrl = originUrl;
        Label = label;
    }

    /// <summary>Scheme+host+port only, e.g. "https://portal.example.com" — no path/query/fragment.</summary>
    public string OriginUrl { get; private set; } = default!;

    /// <summary>Optional operator-facing note, e.g. "Prod VM".</summary>
    public string? Label { get; private set; }
    string? IHasAuditDisplayName.AuditDisplayName => Label ?? OriginUrl;

    public void Update(string originUrl, string? label)
    {
        OriginUrl = originUrl;
        Label = label;
    }
}
