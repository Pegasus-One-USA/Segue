using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;
using FHIRBridge.SharedKernel.Enums;

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
        SourceAuthenticationConfiguration authentication,
        ApplicationType? applicationType = null,
        SourceInteractiveConfiguration? interactive = null)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Name = name;
        SourceSystemType = sourceSystemType;
        BaseUrl = baseUrl;
        Authentication = authentication;
        ApplicationType = applicationType;
        Interactive = interactive;
        IsEnabled = true;
    }

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public SourceSystemType SourceSystemType { get; private set; }
    public string BaseUrl { get; private set; } = default!;
    public SourceAuthenticationConfiguration Authentication { get; private set; } = default!;

    /// <summary>
    /// The SMART application type (composition axis) this source is connected under. Null means legacy behaviour:
    /// the access-token grant is inferred from the vendor and the credentials present.
    /// </summary>
    public ApplicationType? ApplicationType { get; private set; }

    /// <summary>Interactive (authorization-code) settings; null for non-interactive (Backend) sources.</summary>
    public SourceInteractiveConfiguration? Interactive { get; private set; }

    public bool IsEnabled { get; private set; }

    public void Update(
        string name,
        SourceSystemType sourceSystemType,
        string baseUrl,
        SourceAuthenticationConfiguration authentication,
        ApplicationType? applicationType = null,
        SourceInteractiveConfiguration? interactive = null)
    {
        Name = name;
        SourceSystemType = sourceSystemType;
        BaseUrl = baseUrl;
        Authentication = authentication;
        ApplicationType = applicationType;
        Interactive = interactive;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }
}
