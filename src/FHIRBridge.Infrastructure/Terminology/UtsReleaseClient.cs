using System.Text.Json;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Infrastructure.Terminology;

/// <inheritdoc cref="IUtsReleaseClient"/>
public sealed class UtsReleaseClient : IUtsReleaseClient
{
    private const string BaseUrl = "https://uts-ws.nlm.nih.gov";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretProvider _secrets;

    public UtsReleaseClient(IHttpClientFactory httpClientFactory, ISecretProvider secrets)
        => (_httpClientFactory, _secrets) = (httpClientFactory, secrets);

    public async Task<UtsReleaseMetadata> GetCurrentReleaseAsync(string releaseType, CancellationToken cancellationToken)
    {
        var apiKey = await GetApiKeyAsync(cancellationToken);
        var uri = new Uri($"{BaseUrl}/releases?releaseType={Uri.EscapeDataString(releaseType)}&current=true&apiKey={Uri.EscapeDataString(apiKey)}");

        using var client = _httpClientFactory.CreateClient(nameof(UtsReleaseClient));
        using var response = await client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var release = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().FirstOrDefault()
            : document.RootElement;

        var downloadUrlText = TryGetString(release, "downloadUrl") ?? TryGetString(release, "url")
            ?? throw new InvalidOperationException($"UTS did not return a download URL for releaseType '{releaseType}'.");
        if (!Uri.TryCreate(downloadUrlText, UriKind.Absolute, out var downloadUrl))
            throw new InvalidOperationException($"UTS returned an invalid download URL for releaseType '{releaseType}': {downloadUrlText}");

        var releaseName = TryGetString(release, "name") ?? TryGetString(release, "releaseVersion");
        var releaseDate = TryGetDate(release, "releaseDate");
        return new UtsReleaseMetadata(downloadUrl, releaseName, releaseDate);
    }

    public async Task<string> DownloadReleaseAsync(UtsReleaseMetadata release, string vocabularyFolderName, CancellationToken cancellationToken)
    {
        var apiKey = await GetApiKeyAsync(cancellationToken);
        var downloadUri = new Uri($"{BaseUrl}/download?url={Uri.EscapeDataString(release.DownloadUrl.ToString())}&apiKey={Uri.EscapeDataString(apiKey)}");

        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", vocabularyFolderName);
        Directory.CreateDirectory(root);
        var fileName = Path.GetFileName(release.DownloadUrl.LocalPath) is { Length: > 0 } name ? name : $"{vocabularyFolderName}-{Guid.NewGuid():N}.zip";
        var path = Path.Combine(root, fileName);

        using var client = _httpClientFactory.CreateClient(nameof(UtsReleaseClient));
        using var response = await client.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(path))
            await source.CopyToAsync(destination, cancellationToken);

        return path;
    }

    private async Task<string> GetApiKeyAsync(CancellationToken cancellationToken)
    {
        var apiKey = await _secrets.GetSecretAsync(new SecretReference(UtsCredentialNames.VaultName, UtsCredentialNames.ApiKeySecretName), cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("A UTS API key must be configured before RxNorm/SNOMED synchronization can run.");
        return apiKey;
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? TryGetDate(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(value.GetString(), out var parsed)
            ? parsed.ToUniversalTime()
            : null;
}
