namespace FHIRBridge.Application.Abstractions.Cds;

/// <summary>
/// The public origin EHRs reach Segue's /cds-services endpoints at. Configured Cds:PublicBaseUrl (System
/// Settings override, then appsettings) wins; otherwise the request's own origin, which is for display on the
/// admin screens only. The JWT audience check uses the configured value alone and fails closed without it
/// (a Host header must never decide the expected aud), so Cds:PublicBaseUrl is required to accept CDS calls.
/// </summary>
public interface ICdsPublicBaseUrlResolver
{
    /// <param name="requestOrigin">"{scheme}://{host}" of the current request, used when nothing is configured.</param>
    Task<CdsPublicBaseUrl> ResolveAsync(string requestOrigin, CancellationToken cancellationToken);
}

/// <param name="BaseUrl">Origin (and optional path prefix) with no trailing slash.</param>
/// <param name="IsConfigured">True when it came from Cds:PublicBaseUrl rather than the request.</param>
public sealed record CdsPublicBaseUrl(string BaseUrl, bool IsConfigured)
{
    public const string ConfigurationKey = "Cds:PublicBaseUrl";

    public string DiscoveryUrl => $"{BaseUrl}/cds-services";

    /// <summary>The exact URL of one service — also the JWT <c>aud</c> a call to it must carry by default.</summary>
    public string ServiceUrl(string serviceId) => $"{DiscoveryUrl}/{serviceId}";

    public string FeedbackUrl(string serviceId) => $"{ServiceUrl(serviceId)}/feedback";
}
