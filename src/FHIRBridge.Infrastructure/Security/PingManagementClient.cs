using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Talks to PingOne: worker-app token (client credentials, cached), user lookup and group membership from the
/// Management API. Knows nothing about FHIRBridge roles; <see cref="PingRoleResolver"/> maps what this returns.
/// Never logs tokens, emails or group contents.
/// </summary>
public sealed class PingManagementClient
{
    public const string HttpClientName = nameof(PingManagementClient);
    private const string TokenCacheKey = "ping:management-token";

    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PingManagementClient> _logger;

    public PingManagementClient(
        ISecretProvider secretProvider,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<PingManagementClient> logger)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    // Keyed by environment + worker app so a settings change on the admin screen never reuses a stale token.
    private static string TokenCacheKeyFor(PingSettings settings) =>
        $"{TokenCacheKey}:{settings.Authority}:{settings.WorkerClientId}";

    public async Task<string> GetManagementTokenAsync(PingSettings settings, CancellationToken cancellationToken, bool forceFresh = false)
    {
        var cacheKey = TokenCacheKeyFor(settings);
        if (forceFresh)
        {
            _cache.Remove(cacheKey);
        }

        if (_cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        if (string.IsNullOrWhiteSpace(settings.WorkerClientId) || settings.WorkerSecretReference is null)
        {
            throw new InvalidOperationException("PingOne worker application is not configured.");
        }

        var secret = (await _secretProvider.GetSecretAsync(settings.WorkerSecretReference, cancellationToken)).Trim();
        var workerClientId = settings.WorkerClientId.Trim();

        var tokenUrl = $"{settings.Authority}/token";

        // A PingOne worker app is configured for ONE token endpoint auth method (Client Secret Basic or Client Secret
        // Post). Try Basic first and, if PingOne rejects the credentials, the other style, so the screen works
        // whichever way the app was set up.
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var basic = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" })
        };
        basic.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{workerClientId}:{secret}")));
        var response = await client.SendAsync(basic, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            using var post = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = workerClientId,
                    ["client_secret"] = secret
                })
            };
            response = await client.SendAsync(post, cancellationToken);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await PingApiException.FromAsync("token", response, cancellationToken);
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var token = document.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("PingOne returned no access token.");
            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 300;

            // Refresh a minute early so a token never expires mid-request.
            _cache.Set(cacheKey, token, TimeSpan.FromSeconds(Math.Max(30, expiresIn - 60)));
            return token;
        }
    }

    public async Task<string?> FindUserIdByEmailAsync(
        PingSettings settings, string accessToken, string email, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var filter = Uri.EscapeDataString($"email eq \"{email.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
        using var document = await GetJsonAsync(
            $"{settings.ManagementApiBase}/users?filter={filter}&limit=2", accessToken, cancellationToken);

        if (document.RootElement.TryGetProperty("_embedded", out var embedded) &&
            embedded.TryGetProperty("users", out var users) &&
            users.GetArrayLength() == 1)
        {
            return users[0].GetProperty("id").GetString();
        }

        // Zero or ambiguous matches: do not guess which PingOne user this is.
        return null;
    }

    public async Task<IReadOnlyCollection<string>> GetGroupKeysAsync(
        PingSettings settings, string accessToken, string pingUserId, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        string? next = $"{settings.ManagementApiBase}/users/{Uri.EscapeDataString(pingUserId)}/memberOfGroups";

        // Bounded paging: a runaway or looping `next` link can never hold a login open.
        for (var page = 0; next is not null && page < 20; page++)
        {
            using var document = await GetJsonAsync(next, accessToken, cancellationToken);

            // PingOne nests the memberships under _embedded; take every array there rather than depend on its exact
            // name (the membership list is documented as "groupMemberships"), each element being a group with id/name.
            var found = 0;
            if (document.RootElement.TryGetProperty("_embedded", out var embedded) &&
                embedded.ValueKind == JsonValueKind.Object)
            {
                foreach (var list in embedded.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
                {
                    foreach (var group in list.Value.EnumerateArray())
                    {
                        found++;
                        if (group.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } groupId)
                        {
                            keys.Add(groupId);
                        }

                        if (group.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } groupName)
                        {
                            keys.Add(groupName);
                        }
                    }
                }
            }

            if (found == 0 && page == 0)
            {
                // Property NAMES only (never values): shows whether PingOne answered with an unexpected shape.
                var shape = string.Join(", ", document.RootElement.ValueKind == JsonValueKind.Object
                    ? document.RootElement.EnumerateObject().Select(p => p.Name)
                    : []);
                _logger.LogWarning("PingOne returned no group memberships for the user (response properties: {Shape}).", shape);
            }

            next = document.RootElement.TryGetProperty("_links", out var links) &&
                   links.TryGetProperty("next", out var nextLink) &&
                   nextLink.TryGetProperty("href", out var href)
                ? href.GetString()
                : null;

            // Never forward the bearer token to a host other than the configured Management API.
            if (next is not null &&
                !(Uri.TryCreate(next, UriKind.Absolute, out var nextUri) &&
                  string.Equals(nextUri.Host, new Uri(settings.ManagementApiBase).Host, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("PingOne returned an unexpected pagination link.");
            }
        }

        return keys;
    }

    public async Task<JsonDocument> GetJsonAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await PingApiException.FromAsync("api", response, cancellationToken);
        }

        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }
}
