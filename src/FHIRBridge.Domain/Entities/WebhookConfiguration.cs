using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class WebhookConfiguration : AuditableChildEntity<Guid>
{
    private WebhookConfiguration()
    {
    }

    public WebhookConfiguration(
        Guid tenantId,
        Guid sourceConnectionId,
        string resourceType,
        string name,
        string path,
        bool isEnabled)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        SourceConnectionId = sourceConnectionId;
        ResourceType = resourceType;
        Name = name;
        Path = path;
        IsEnabled = isEnabled;
    }

    public Guid TenantId { get; private set; }
    public Guid SourceConnectionId { get; private set; }
    public string ResourceType { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string Path { get; private set; } = default!;
    public bool IsEnabled { get; private set; }

    public void Update(string resourceType, string name, string path, bool isEnabled)
    {
        ResourceType = resourceType;
        Name = name;
        Path = path;
        IsEnabled = isEnabled;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }
}
