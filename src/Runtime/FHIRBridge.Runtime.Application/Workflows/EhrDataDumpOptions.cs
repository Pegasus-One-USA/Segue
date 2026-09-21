namespace FHIRBridge.Runtime.Application.Workflows;

/// <summary>
/// Local-only inspection aid: when enabled, a source node writes everything it fetched from the EHR to
/// <c>&lt;Directory&gt;/&lt;workflowDefinitionId&gt;.txt</c>, one file per workflow definition, rewritten on each run.
/// </summary>
/// <remarks>
/// The dump contains raw, unmasked FHIR resources — precisely what the PHI-masking Serilog enricher exists to keep
/// out of the log pipeline. It is therefore off by default and enabled only in <c>appsettings.Development.json</c>,
/// against sandbox/sample sources. Do not enable it in any environment carrying real patient data.
/// </remarks>
public sealed class EhrDataDumpOptions
{
    public const string SectionName = "EhrDataDump";

    public bool Enabled { get; set; }

    /// <summary>Absolute or content-root-relative directory the per-workflow files are written to.</summary>
    public string Directory { get; set; } = "EHRData";
}
