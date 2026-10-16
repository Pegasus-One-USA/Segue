using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Observability;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Infrastructure.Cds.Coverage;

/// <summary>
/// Calls a payer's own CRD service on behalf of the EHR (Segue as CRD intermediary). Every call carries a JWT Segue
/// signs itself: RS384, header <c>jku</c> = Segue's JWKS URL ({Cds:PublicBaseUrl}/cds-services/.well-known/jwks.json),
/// <c>kid</c>, <c>typ: JWT</c>; claims <c>iss</c> = Cds:PublicBaseUrl, <c>aud</c> = the payer's service URL,
/// <c>sub</c> = the payer's client id for Segue (when set), <c>iat</c>, <c>exp</c> (60 s), <c>jti</c>.
/// <para>
/// SSRF rules: https only; the primary handler (<see cref="CdsJwksHttpHandler.CreateGuarded"/>) refuses private,
/// loopback and link-local addresses (unless Cds:AllowPrivateRelayAddresses), follows no redirects and uses no
/// proxy; responses are capped at Cds:RelayMaxResponseBytes; the timeout is the caller's (within the call budget).
/// No loggers and no tracing on this client (the body holds PHI). Never throws for a remote failure.
/// </para>
/// </summary>
public sealed class CdsPayerRelayClient : ICdsPayerRelayClient
{
    public const string HttpClientName = "CdsPayerRelay";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly CdsRelaySigningKeyProvider _keys;
    private readonly ICdsPublicBaseUrlResolver _baseUrlResolver;
    private readonly IOptionsMonitor<CdsOptions> _options;

    public CdsPayerRelayClient(
        HttpClient httpClient,
        CdsRelaySigningKeyProvider keys,
        ICdsPublicBaseUrlResolver baseUrlResolver,
        IOptionsMonitor<CdsOptions> options)
    {
        _httpClient = httpClient;
        _keys = keys;
        _baseUrlResolver = baseUrlResolver;
        _options = options;
    }

    public async Task<CdsRelayResult> SendAsync(CdsPayer payer, CdsRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        CdsRelayResult Fail(string outcome, int? status = null) => new(false, outcome, status, stopwatch.ElapsedMilliseconds, null);

        if (!CdsPayer.IsHttpsUrl(payer.RelayServiceUrl))
        {
            return Fail("not https");
        }

        var baseUrl = await _baseUrlResolver.ResolveAsync(string.Empty, cancellationToken);
        if (!baseUrl.IsConfigured)
        {
            return Fail("Cds:PublicBaseUrl is not set");
        }

        string token;
        try
        {
            token = await CreateTokenAsync(payer, baseUrl, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Fail("signing key unavailable");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, payer.RelayServiceUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(request, WriteOptions), Encoding.UTF8, "application/json"),
            };
            message.Options.Set(FhirBridgeCdsMetrics.UntracedRequest, true);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Fail($"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
            }

            var json = await ReadBoundedAsync(response, budget.Token);
            if (json is null)
            {
                return Fail("response too large", (int)response.StatusCode);
            }

            var parsed = Parse(json);
            return parsed is null
                ? Fail("invalid response", (int)response.StatusCode)
                : new CdsRelayResult(true, "ok", (int)response.StatusCode, stopwatch.ElapsedMilliseconds, parsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("timed out");
        }
        catch (HttpRequestException)
        {
            return Fail("unreachable");
        }
    }

    /// <summary>The JWT Segue signs for one relayed call.</summary>
    private async Task<string> CreateTokenAsync(CdsPayer payer, CdsPublicBaseUrl baseUrl, CancellationToken cancellationToken)
    {
        var key = await _keys.GetSigningKeyAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString() };
        if (payer.RelayClientId is { } clientId)
        {
            claims[JwtRegisteredClaimNames.Sub] = clientId;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = baseUrl.BaseUrl,
            Audience = payer.RelayServiceUrl,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddSeconds(Math.Clamp(_options.CurrentValue.RelayTokenLifetimeSeconds, 10, 300)),
            Claims = claims,
            AdditionalHeaderClaims = new Dictionary<string, object> { ["jku"] = baseUrl.JwksUrl },
            SigningCredentials = new SigningCredentials(key, CdsRelaySigningKeyProvider.Algorithm),
        });
    }

    /// <summary>A CDS Hooks response, or null when the body is not one (no cards array, or not JSON).</summary>
    private static CdsResponse? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("cards", out var cards)
                || cards.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var response = document.RootElement.Deserialize<CdsResponse>(ReadOptions);
            if (response is null)
            {
                return null;
            }

            response.Cards = (response.Cards ?? []).Where(card => card is not null).ToArray();
            response.SystemActions = response.SystemActions?.Where(action => action is not null).ToArray();
            return response;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var max = Math.Max(1024, _options.CurrentValue.RelayMaxResponseBytes);
        if (response.Content.Headers.ContentLength > max)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
