namespace FHIRBridge.Domain.Enums;

/// <summary>
/// How a SMART EHR-launched app is displayed within the EHR, as registered in the EHR's own app-launch
/// configuration (e.g. Epic Hyperspace/Hyperdrive's client record). FHIRBridge does not control this behavior —
/// the EHR decides how to open the launch URL — this is captured purely as configuration metadata so admins can
/// record and audit how a given source was registered, and so FHIRBridge can flag known display-mode-specific
/// issues (e.g. an embedded/iframe launch requires the launch page to allow framing; an external-browser launch
/// does not). Only meaningful for the EHR-launch audience.
/// </summary>
public enum LaunchDisplayMode
{
    /// <summary>Opens embedded within the EHR's own window, typically in an iframe (e.g. Epic's default Workspace/Activity tab).</summary>
    Embedded = 0,

    /// <summary>Opens in the user's default external web browser, outside the EHR's window.</summary>
    ExternalBrowser = 1,

    /// <summary>Opens docked alongside the patient chart (e.g. Epic's side-by-side/sidebar activity).</summary>
    Sidebar = 2
}
