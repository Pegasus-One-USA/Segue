using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Aggregates;

public sealed class Tenant : AuditableEntity<Guid>
{

    private Tenant()
    {
    }

    public Tenant(string name, string code)
    {
        Id = Guid.NewGuid();
        Name = name;
        Code = code;
        Status = TenantStatus.Active;
        IsEnabled = true;
    }

    public string Name { get; private set; } = default!;
    public string Code { get; private set; } = default!;
    public TenantStatus Status { get; private set; }

    /// <summary>Hard enable/disable switch. When false, login and all pipeline activity for the tenant is blocked.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>HIPAA retention window (days) for purgeable operational data; null = platform default.</summary>
    public int? RetentionDays { get; private set; }

    /// <summary>IANA time zone used to interpret this tenant's cron schedules; null = UTC.</summary>
    public string? TimeZone { get; private set; }

    /// <summary>Primary administrative contact email for the tenant.</summary>
    public string? ContactEmail { get; private set; }

    /// <summary>Data-residency region hint for the tenant.</summary>
    public string? Region { get; private set; }

}
