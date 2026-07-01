using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class SourceConnection : AuditableChildEntity<Guid>
{
    private SourceConnection()
    {
    }

    public SourceConnection(
        Guid tenantId,
        string name,
        SourceSystemType sourceSystemType,
        string baseUrl,
        SourceAuthenticationConfiguration authentication)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Name = name;
        SourceSystemType = sourceSystemType;
        BaseUrl = baseUrl;
        Authentication = authentication;
        IsEnabled = true;
    }

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public SourceSystemType SourceSystemType { get; private set; }
    public string BaseUrl { get; private set; } = default!;
    public SourceAuthenticationConfiguration Authentication { get; private set; } = default!;
    public bool IsEnabled { get; private set; }

    public void Update(
        string name,
        SourceSystemType sourceSystemType,
        string baseUrl,
        SourceAuthenticationConfiguration authentication)
    {
        Name = name;
        SourceSystemType = sourceSystemType;
        BaseUrl = baseUrl;
        Authentication = authentication;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }
}
