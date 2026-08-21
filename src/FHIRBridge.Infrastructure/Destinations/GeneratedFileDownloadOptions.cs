namespace FHIRBridge.Infrastructure.Destinations;

public sealed class GeneratedFileDownloadOptions
{
    /// <summary>Local/shared root directory generated files are written under (date-partitioned beneath it).</summary>
    public string RootPath { get; set; } = "App_Data/generated-file-downloads";

    /// <summary>Base URL (scheme+host, no trailing slash) used to build the absolute link returned to callers.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5000";
}
