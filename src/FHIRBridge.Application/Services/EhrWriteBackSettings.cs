namespace FHIRBridge.Application.Services;

/// <summary>System settings that govern EHR write-back.</summary>
public static class EhrWriteBackSettings
{
    /// <summary>QA only. When true, an EHR Write-Back destination may run in clone mode: it creates a new test patient
    /// from a source patient's data with an altered name, birth date and identifiers, and writes that patient's
    /// records against the clone. Must stay false in production.</summary>
    public const string CloneModeEnabledKey = "EhrWriteBack:CloneModeEnabled";

    public const bool CloneModeEnabledDefault = false;
}
