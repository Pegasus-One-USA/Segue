namespace FHIRBridge.Application.Services;

/// <summary>System settings that govern EHR write-back.</summary>
/// <remarks>
/// The former <c>EhrWriteBack:LiveWriteTypes</c> release list is gone (migration RemoveEhrWriteBackLiveWriteTypes
/// deletes its row). Going live is now a destination's own choice, controlled by the EHR Write-Back permissions.
/// </remarks>
public static class EhrWriteBackSettings
{
    /// <summary>QA only. When true, an EHR Write-Back destination may run in clone mode: it creates a new test patient
    /// from a source patient's data with an altered name, birth date and identifiers, and writes that patient's
    /// records against the clone. Must stay false in production.</summary>
    public const string CloneModeEnabledKey = "EhrWriteBack:CloneModeEnabled";

    public const bool CloneModeEnabledDefault = false;

    /// <summary>Off by default, like clone mode. When true, the EHR Write-Back destination form offers Dry run as a Run mode. When false,
    /// new destinations are not offered Dry run (only Live, plus Test on a FHIR server for Epic, eClinicalWorks and
    /// athenahealth); where only Live is left, the user must choose it. The setting only governs what the portal
    /// offers: a saved destination with <c>dest_dryRun</c> true keeps running as a dry run whatever it says, because
    /// the Runtime executor and the writer never read it.</summary>
    public const string DryRunEnabledKey = "EhrWriteBack:DryRunEnabled";

    public const bool DryRunEnabledDefault = false;
}
