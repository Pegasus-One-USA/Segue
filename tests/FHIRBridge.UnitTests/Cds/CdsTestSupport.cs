using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Infrastructure.Cds;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>Shared builders for the CDS Hooks unit tests: handler contexts, options and signed test JWTs.</summary>
internal static class CdsTestSupport
{
    public static CdsHandlerContext Context(ICdsServiceHandler handler, CdsRequest request, string? settingsJson = null) =>
        new(
            new CdsInstanceSnapshot(Guid.NewGuid(), "test-service", "Test service", handler.Key, request.Hook, "Segue test", null),
            request,
            CdsSettingsValues.Resolve(handler.Settings, settingsJson));

    /// <summary>A handler context for a configured instance (its fixes, dismiss reasons and links).</summary>
    public static CdsHandlerContext Context(ICdsServiceHandler handler, CdsRequest request, FHIRBridge.Domain.Entities.CdsServiceInstance instance) =>
        new(CdsInstanceSnapshot.From(instance), request, CdsSettingsValues.Resolve(handler.Settings, instance.SettingsJson));

    public static IOptionsMonitor<CdsOptions> Options(CdsOptions? options = null)
    {
        var monitor = new Mock<IOptionsMonitor<CdsOptions>>();
        monitor.Setup(m => m.CurrentValue).Returns(options ?? new CdsOptions());
        return monitor.Object;
    }

    /// <summary>
    /// The evaluator with live settings answering their defaults (or <paramref name="settingOverrides"/> by key).
    /// </summary>
    public static CdsEvaluator Evaluator(
        ICdsFhirCallbackClient? callbackClient = null,
        CdsOptions? options = null,
        Microsoft.Extensions.Logging.ILogger<CdsEvaluator>? logger = null,
        IReadOnlyDictionary<string, int>? settingOverrides = null)
    {
        var settings = new Mock<FHIRBridge.Application.Abstractions.Caching.ISystemSettingsCache>();
        settings.Setup(s => s.GetIntAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string key, int fallback, CancellationToken _) =>
                Task.FromResult(settingOverrides is not null && settingOverrides.TryGetValue(key, out var value) ? value : fallback));
        return new CdsEvaluator(
            settings.Object,
            Options(options),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CdsEvaluator>.Instance,
            callbackClient);
    }

    /// <summary>A minimal FHIR searchset Bundle around <paramref name="resources"/>.</summary>
    public static JsonObject SearchSet(params JsonObject[] resources) => new()
    {
        ["resourceType"] = "Bundle",
        ["type"] = "searchset",
        ["entry"] = new JsonArray(resources.Select(resource => (JsonNode?)new JsonObject { ["resource"] = resource }).ToArray()),
    };
}

/// <summary>A scripted <see cref="ICdsFhirCallbackClient"/> that records every callback request.</summary>
internal sealed class FakeCallbackClient : ICdsFhirCallbackClient
{
    private readonly Func<CdsCallbackRequest, CancellationToken, Task<CdsCallbackResult>> _fetch;
    private readonly System.Collections.Concurrent.ConcurrentQueue<CdsCallbackRequest> _requests = new();

    public FakeCallbackClient(Func<CdsCallbackRequest, CancellationToken, Task<CdsCallbackResult>> fetch)
    {
        _fetch = fetch;
    }

    public IReadOnlyList<CdsCallbackRequest> Requests => _requests.ToArray();

    /// <summary>Answers every query key with <paramref name="resourceForKey"/> (a fresh node per call).</summary>
    public static FakeCallbackClient Answering(Func<string, JsonNode?> resourceForKey) =>
        new((request, _) =>
        {
            var resources = request.Queries.Keys.ToDictionary(key => key, resourceForKey, StringComparer.Ordinal);
            return Task.FromResult(new CdsCallbackResult(resources, resources.Count(r => r.Value is not null), 7, null));
        });

    public static FakeCallbackClient Returning(CdsCallbackResult result) => new((_, _) => Task.FromResult(result));

    public Task<CdsCallbackResult> FetchAsync(CdsCallbackRequest request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        return _fetch(request, cancellationToken);
    }

    public Task<CdsCallbackProbeResult> ProbeMetadataAsync(string fhirBaseUrl, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>Keeps every formatted log line (and exception text), to assert that nothing sensitive is ever logged.</summary>
internal sealed class CdsCapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => _lines.ToArray();

    public string All => string.Join("\n", _lines);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}

/// <summary>
/// A scriptable HTTP endpoint for the callback / JWKS clients: answers by a function of the request and keeps
/// every request (URL, Authorization header, options) it saw.
/// </summary>
internal sealed class CdsFakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly System.Collections.Concurrent.ConcurrentQueue<SeenRequest> _seen = new();

    public CdsFakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public CdsFakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public IReadOnlyList<SeenRequest> Seen => _seen.ToArray();

    public static HttpResponseMessage Json(JsonNode body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/fhir+json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.TryGetValue(FHIRBridge.Observability.FhirBridgeCdsMetrics.UntracedRequest, out var untraced);
        _seen.Enqueue(new SeenRequest(request.RequestUri!, request.Headers.Authorization?.ToString(), untraced));
        return _respond(request, cancellationToken);
    }

    public sealed record SeenRequest(Uri Uri, string? Authorization, bool Untraced);
}

/// <summary>
/// A signing key of a test CDS client plus its public JWKS, and a token minter. The JWKS is built by hand from the
/// public parameters only, exactly as an admin would paste it.
/// </summary>
internal sealed class CdsTestSigningKey : IDisposable
{
    private readonly IDisposable _algorithm;

    private CdsTestSigningKey(SecurityKey privateKey, string algorithm, string publicJwk, IDisposable owner)
    {
        PrivateKey = privateKey;
        Algorithm = algorithm;
        PublicJwk = publicJwk;
        _algorithm = owner;
    }

    public SecurityKey PrivateKey { get; }

    public string Algorithm { get; }

    /// <summary>One JWK (public part only) as JSON.</summary>
    public string PublicJwk { get; }

    public string Jwks => $"{{\"keys\":[{PublicJwk}]}}";

    public static CdsTestSigningKey Rsa(string algorithm = SecurityAlgorithms.RsaSha384, string? kid = null)
    {
        kid ??= Guid.NewGuid().ToString("N");
        var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);
        var jwk = new JsonObject
        {
            ["kty"] = "RSA",
            ["kid"] = kid,
            ["use"] = "sig",
            ["alg"] = algorithm,
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
        };
        return new CdsTestSigningKey(new RsaSecurityKey(rsa) { KeyId = kid }, algorithm, jwk.ToJsonString(), rsa);
    }

    public static CdsTestSigningKey Ec(string algorithm = SecurityAlgorithms.EcdsaSha384, string? kid = null)
    {
        kid ??= Guid.NewGuid().ToString("N");
        var (curve, crv) = algorithm == SecurityAlgorithms.EcdsaSha256
            ? (ECCurve.NamedCurves.nistP256, "P-256")
            : (ECCurve.NamedCurves.nistP384, "P-384");
        var ecdsa = ECDsa.Create(curve);
        var parameters = ecdsa.ExportParameters(false);
        var jwk = new JsonObject
        {
            ["kty"] = "EC",
            ["kid"] = kid,
            ["use"] = "sig",
            ["alg"] = algorithm,
            ["crv"] = crv,
            ["x"] = Base64UrlEncoder.Encode(parameters.Q.X),
            ["y"] = Base64UrlEncoder.Encode(parameters.Q.Y),
        };
        return new CdsTestSigningKey(new ECDsaSecurityKey(ecdsa) { KeyId = kid }, algorithm, jwk.ToJsonString(), ecdsa);
    }

    /// <summary>
    /// A CDS Hooks client JWT (iss, aud, exp, iat, jti), signed with this key. Epic's shape adds a
    /// <paramref name="subject"/> (its CDS client id) and a <paramref name="jku"/> header (its JWK Set URL).
    /// </summary>
    public string CreateToken(
        string issuer,
        string audience,
        DateTime? expiresUtc = null,
        string? jti = null,
        bool includeJti = true,
        string? subject = null,
        string? jku = null)
    {
        var now = DateTime.UtcNow;
        var expires = expiresUtc ?? now.AddMinutes(5);
        var issuedAt = expires < now ? expires.AddMinutes(-5) : now;
        var claims = new Dictionary<string, object>();
        if (includeJti)
        {
            claims[JwtRegisteredClaimNames.Jti] = jti ?? Guid.NewGuid().ToString();
        }

        if (subject is not null)
        {
            claims[JwtRegisteredClaimNames.Sub] = subject;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = expires,
            Claims = claims,
            Subject = new ClaimsIdentity(),
            SigningCredentials = new SigningCredentials(PrivateKey, Algorithm),
            AdditionalHeaderClaims = jku is null ? null : new Dictionary<string, object> { ["jku"] = jku },
        });
    }

    public void Dispose() => _algorithm.Dispose();
}
