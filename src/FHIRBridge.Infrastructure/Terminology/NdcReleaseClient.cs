using System.Text.Json;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Reads openFDA's public bulk-download manifest (<c>api.fda.gov/download.json</c>) to find the current NDC
/// Directory export. No API key is required — openFDA is fully public; an optional key (if configured) is
/// only appended to raise the caller's rate limit, never to gate access.
/// </summary>
public sealed class NdcReleaseClient : INdcReleaseClient
{
    private const string ManifestUrl = "https://api.fda.gov/download.json";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretProvider _secrets;

    public NdcReleaseClient(IHttpClientFactory httpClientFactory, ISecretProvider secrets)
        => (_httpClientFactory, _secrets) = (httpClientFactory, secrets);

    public async Task<NdcReleaseMetadata> GetCurrentReleaseAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(nameof(NdcReleaseClient));
        using var response = await client.GetAsync(ManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("results", out var results)
            || !results.TryGetProperty("drug", out var drug)
            || !drug.TryGetProperty("ndc", out var ndc)
            || !ndc.TryGetProperty("partitions", out var partitions)
            || partitions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("openFDA's download manifest did not have the expected results.drug.ndc.partitions shape.");
        }

        var firstPartition = partitions.EnumerateArray().FirstOrDefault();
        var fileUrl = firstPartition.ValueKind == JsonValueKind.Object && firstPartition.TryGetProperty("file", out var fileProperty)
            ? fileProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(fileUrl) || !Uri.TryCreate(fileUrl, UriKind.Absolute, out var downloadUrl))
            throw new InvalidOperationException("openFDA's download manifest did not include a usable NDC export file URL.");

        var exportDate = ndc.TryGetProperty("export_date", out var dateProperty) && DateTime.TryParse(dateProperty.GetString(), out var parsed)
            ? parsed.ToUniversalTime()
            : (DateTime?)null;
        return new NdcReleaseMetadata(downloadUrl, exportDate);
    }

    public async Task<string> DownloadReleaseAsync(NdcReleaseMetadata release, CancellationToken cancellationToken)
    {
        var apiKey = await TryGetApiKeyAsync(cancellationToken);
        var downloadUri = string.IsNullOrWhiteSpace(apiKey)
            ? release.DownloadUrl
            : new Uri(release.DownloadUrl + (release.DownloadUrl.Query.Length > 0 ? "&" : "?") + "api_key=" + Uri.EscapeDataString(apiKey));

        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", "Ndc");
        Directory.CreateDirectory(root);
        var fileName = Path.GetFileName(release.DownloadUrl.LocalPath) is { Length: > 0 } name ? name : $"ndc-{Guid.NewGuid():N}.zip";
        var path = Path.Combine(root, fileName);

        using var client = _httpClientFactory.CreateClient(nameof(NdcReleaseClient));
        using var response = await client.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(path))
            await source.CopyToAsync(destination, cancellationToken);

        return path;
    }

    private async Task<string?> TryGetApiKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var apiKey = await _secrets.GetSecretAsync(new SecretReference(NdcConfigurationService.VaultName, NdcConfigurationService.ApiKeySecretName), cancellationToken);
            return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        }
        catch
        {
            return null; // Optional credential — absence is expected and fine for openFDA.
        }
    }
}
