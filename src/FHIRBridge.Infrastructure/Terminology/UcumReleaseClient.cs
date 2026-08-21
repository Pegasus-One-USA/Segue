using System.Net.Http.Headers;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Terminology;

namespace FHIRBridge.Infrastructure.Terminology;

/// <inheritdoc cref="IUcumReleaseClient"/>
public sealed class UcumReleaseClient : IUcumReleaseClient
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/ucum-org/ucum/releases/latest";
    private const string MainBranchFallbackUrl = "https://raw.githubusercontent.com/ucum-org/ucum/main/ucum-essence.xml";

    private readonly IHttpClientFactory _httpClientFactory;
    public UcumReleaseClient(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<UcumReleaseMetadata> GetCurrentReleaseAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(LatestReleaseUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // No tagged release, or GitHub API unavailable — fall back to the live main-branch file.
            return new UcumReleaseMetadata(new Uri(MainBranchFallbackUrl), null, null);
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        var tagName = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        var publishedAt = root.TryGetProperty("published_at", out var published) && DateTime.TryParse(published.GetString(), out var parsed)
            ? parsed.ToUniversalTime()
            : (DateTime?)null;

        var xmlAssetUrl = root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
            ? assets.EnumerateArray()
                .Select(a => a.TryGetProperty("browser_download_url", out var url) ? url.GetString() : null)
                .FirstOrDefault(url => url is not null && url.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            : null;

        var downloadUrl = xmlAssetUrl is not null && Uri.TryCreate(xmlAssetUrl, UriKind.Absolute, out var parsedUrl)
            ? parsedUrl
            : new Uri(MainBranchFallbackUrl);
        return new UcumReleaseMetadata(downloadUrl, tagName, publishedAt);
    }

    public async Task<string> DownloadReleaseAsync(UcumReleaseMetadata release, CancellationToken cancellationToken)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", "Ucum");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ucum-essence.xml");

        using var client = CreateClient();
        using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(path))
            await source.CopyToAsync(destination, cancellationToken);

        return path;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(nameof(UcumReleaseClient));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FHIRBridge", "1.0"));
        return client;
    }
}
