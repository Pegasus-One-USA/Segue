using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;

namespace FHIRBridge.Application.Services;

/// <summary>System settings that govern EHR write-back.</summary>
public static class EhrWriteBackSettings
{
    /// <summary>QA only. When true, an EHR Write-Back destination may run in clone mode: it creates a new test patient
    /// from a source patient's data with an altered name, birth date and identifiers, and writes that patient's
    /// records against the clone. Must stay false in production.</summary>
    public const string CloneModeEnabledKey = "EhrWriteBack:CloneModeEnabled";

    public const bool CloneModeEnabledDefault = false;

    /// <summary>Which vendor resource types an administrator has released for LIVE writes, as a comma list of
    /// <c>Vendor:ResourceType</c> (e.g. <c>Epic:AllergyIntolerance,Epic:Condition</c>). Empty, the default, keeps
    /// every write-back run a dry run. Only a type whose capability is
    /// <see cref="EhrWriteCapability.LiveWriteSupported"/> can be released; any other entry is ignored.</summary>
    public const string LiveWriteTypesKey = "EhrWriteBack:LiveWriteTypes";

    public const string LiveWriteTypesDefault = "";

    /// <summary>The resource types of <paramref name="vendor"/> that are released for live writes: listed in the
    /// setting AND live-capable in code. Entries for other vendors, unknown types, typos and blanks are ignored, so a
    /// malformed value can only release less, never more.</summary>
    public static IReadOnlySet<string> ReleasedResourceTypes(string? settingValue, SourceSystemType vendor)
    {
        var released = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(settingValue))
        {
            return released;
        }

        foreach (var entry in settingValue.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf(':');
            if (separator <= 0
                || !EhrWriteCapabilities.TryParseVendor(entry[..separator], out var entryVendor)
                || entryVendor != vendor)
            {
                continue;
            }

            var capability = EhrWriteCapabilities.Find(vendor, entry[(separator + 1)..].Trim());
            if (capability is { LiveWriteSupported: true })
            {
                released.Add(capability.ResourceType);
            }
        }

        return released;
    }
}
