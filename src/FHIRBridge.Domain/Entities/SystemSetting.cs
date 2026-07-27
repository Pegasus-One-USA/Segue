using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A single runtime-editable configuration value, keyed the same way as its appsettings.json
/// counterpart (e.g. "GeneratedFileDownload:PublicBaseUrl"). Global — not tenant-scoped. Values are
/// always stored as strings; consumers parse them via the typed helpers on ISystemSettingsCache and
/// fall back to their compiled-in appsettings default when no row exists for the key.
/// </summary>
public sealed class SystemSetting : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private SystemSetting()
    {
    }

    public SystemSetting(string key, string value, string? description)
    {
        Id = Guid.NewGuid();
        Key = key;
        Value = value;
        Description = description;
    }

    /// <summary>Dotted config-section key, e.g. "RuntimeWorker:IntervalSeconds".</summary>
    public string Key { get; private set; } = default!;

    public string Value { get; private set; } = default!;

    /// <summary>Optional operator-facing note describing what this key controls.</summary>
    public string? Description { get; private set; }

    public void UpdateValue(string value, string? description)
    {
        Value = value;
        Description = description;
    }

    string? IHasAuditDisplayName.AuditDisplayName => Key;
}
