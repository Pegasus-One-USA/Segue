namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Where archive-before-purge artifacts go. A local/mounted directory path by default; an absolute http(s) URL
/// (a pre-signed blob/S3 PUT target — the same convention <c>MappedDestinationSerialization</c> destination
/// writers already use) works too, with no code change.
/// </summary>
public sealed class GovernanceArchiveOptions
{
    public string RootPath { get; set; } = "governance-archives";
}
