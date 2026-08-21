using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Authenticated client for the official LOINC Download API. It downloads only to an instance-local temporary directory.</summary>
public sealed class LoincReleaseClient : ILoincReleaseClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretProvider _secrets;
    private readonly ISystemSettingsCache _settings;

    public LoincReleaseClient(IHttpClientFactory httpClientFactory, ISecretProvider secrets, ISystemSettingsCache settings)
        => (_httpClientFactory, _secrets, _settings) = (httpClientFactory, secrets, settings);

    public async Task<LoincReleaseMetadata> GetCurrentReleaseAsync(CancellationToken cancellationToken)
    {
        var endpoint = await _settings.GetStringAsync("Terminology:Loinc:DownloadApiUrl", "https://loinc.regenstrief.org/api/v1", cancellationToken);
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/Loinc", UriKind.Absolute, out var uri)) throw new InvalidOperationException("LOINC Download API URL is not configured.");
        using var response = await CreateClient(cancellationToken).GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        var version = root.GetProperty("version").GetString();
        var url = root.GetProperty("downloadUrl").GetString();
        var md5 = root.GetProperty("downloadMD5Hash").GetString();
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(md5) || !Uri.TryCreate(url, UriKind.Absolute, out var downloadUrl)) throw new InvalidOperationException("LOINC release metadata was incomplete.");
        return new LoincReleaseMetadata(version, root.TryGetProperty("releaseDate", out var date) && date.TryGetDateTime(out var parsed) ? parsed.ToUniversalTime() : null,
            root.TryGetProperty("numberOfLoincs", out var count) && count.TryGetInt32(out var number) ? number : 0, downloadUrl, md5);
    }

    public async Task<string> DownloadReleaseAsync(LoincReleaseMetadata release, CancellationToken cancellationToken)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", "Loinc", release.Version);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Loinc_" + release.Version + ".zip");
        using var response = await CreateClient(cancellationToken).GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(path)) await source.CopyToAsync(destination, cancellationToken);
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(await MD5.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(release.Md5Hash.Trim().ToLowerInvariant())))
        {
            File.Delete(path);
            throw new InvalidDataException("LOINC release checksum verification failed.");
        }
        return path;
    }

    private HttpClient CreateClient(CancellationToken cancellationToken)
    {
        var username = _secrets.GetSecretAsync(new SecretReference(LoincConfigurationService.VaultName, LoincConfigurationService.UsernameSecretName), cancellationToken).GetAwaiter().GetResult();
        var password = _secrets.GetSecretAsync(new SecretReference(LoincConfigurationService.VaultName, LoincConfigurationService.PasswordSecretName), cancellationToken).GetAwaiter().GetResult();
        var client = _httpClientFactory.CreateClient(nameof(LoincReleaseClient));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
        return client;
    }
}
