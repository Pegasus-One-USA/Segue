using System.Net;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Cds;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// The CDS Hooks 2.0 client-JWT check: iss must be an enabled trusted issuer, the signature must verify against its
/// pasted JWKS or JWKS URL (RS256/RS384/ES256/ES384 only), aud must be the exact service URL (or the issuer's
/// override), exp is required, and each jti is accepted once.
/// </summary>
public sealed class CdsJwtValidatorTests : IDisposable
{
    private const string Issuer = "https://sandbox.cds-hooks.org";
    private const string ServiceUrl = "https://segue.example.org/cds-services/drug-allergy";

    private readonly InMemoryCdsTrustedIssuerRepository _issuers = new();
    private readonly MemoryDistributedCache _replayCache = new(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
    private readonly MemoryCache _jwksCache = new(new MemoryCacheOptions());
    private readonly CdsTestSigningKey _key = CdsTestSigningKey.Ec();
    private readonly StubJwksHandler _jwksEndpoint = new();

    public void Dispose()
    {
        _key.Dispose();
        _jwksCache.Dispose();
    }

    private CdsJwtValidator CreateValidator(CdsOptions? options = null)
    {
        var monitor = CdsTestSupport.Options(options);
        return new CdsJwtValidator(_issuers, CreateJwksProvider(options), new CdsReplayGuard(_replayCache), monitor);
    }

    private CdsJwksProvider CreateJwksProvider(CdsOptions? options = null)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(CdsJwksProvider.HttpClientName)).Returns(() => new HttpClient(_jwksEndpoint, disposeHandler: false));
        return new CdsJwksProvider(httpClientFactory.Object, _jwksCache, CdsTestSupport.Options(options), NullLogger<CdsJwksProvider>.Instance);
    }

    private async Task<CdsTrustedIssuer> RegisterAsync(
        string? jwksJson = null,
        string? jwksUrl = null,
        string? audience = null,
        bool isEnabled = true,
        string issuer = Issuer,
        string? expectedSubject = null)
    {
        var trusted = new CdsTrustedIssuer(
            "CDS Hooks sandbox", "Other", issuer, jwksUrl, jwksUrl is null ? jwksJson ?? _key.Jwks : jwksJson, audience, isEnabled,
            expectedSubject);
        await _issuers.AddAsync(trusted, CancellationToken.None);
        return trusted;
    }

    private Task<CdsJwtValidationResult> ValidateAsync(string? token, CdsJwtValidator? validator = null, string audience = ServiceUrl) =>
        (validator ?? CreateValidator()).ValidateAsync(token is null ? null : $"Bearer {token}", [audience], CancellationToken.None);

    [Fact]
    public async Task A_token_signed_with_the_pasted_jwks_is_accepted()
    {
        var trusted = await RegisterAsync();

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl));

        result.IsValid.Should().BeTrue(result.Failure);
        result.TrustedIssuer!.Id.Should().Be(trusted.Id);
        result.Issuer.Should().Be(Issuer);
    }

    [Theory]
    [InlineData(SecurityAlgorithms.RsaSha256)]
    [InlineData(SecurityAlgorithms.RsaSha384)]
    [InlineData(SecurityAlgorithms.EcdsaSha256)]
    [InlineData(SecurityAlgorithms.EcdsaSha384)]
    public async Task Each_allowed_algorithm_verifies(string algorithm)
    {
        using var key = algorithm.StartsWith("RS", StringComparison.Ordinal)
            ? CdsTestSigningKey.Rsa(algorithm)
            : CdsTestSigningKey.Ec(algorithm);
        await RegisterAsync(jwksJson: key.Jwks);

        var result = await ValidateAsync(key.CreateToken(Issuer, ServiceUrl));

        result.IsValid.Should().BeTrue(result.Failure);
    }

    [Fact]
    public async Task An_HMAC_signed_token_is_refused_before_any_key_lookup()
    {
        await RegisterAsync();
        var hmacKey = new SymmetricSecurityKey(new byte[32]);
        var token = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ServiceUrl,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["jti"] = Guid.NewGuid().ToString() },
            SigningCredentials = new SigningCredentials(hmacKey, SecurityAlgorithms.HmacSha256),
        });

        var result = await ValidateAsync(token);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("not allowed");
    }

    [Fact]
    public async Task A_missing_token_is_refused()
    {
        await RegisterAsync();

        var result = await ValidateAsync(token: null);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("No bearer token");
    }

    [Fact]
    public async Task A_garbage_token_is_refused()
    {
        var result = await ValidateAsync("not-a-jwt");

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("well-formed");
    }

    [Fact]
    public async Task An_unregistered_issuer_is_refused()
    {
        await RegisterAsync();

        var result = await ValidateAsync(_key.CreateToken("https://someone-else.example.org", ServiceUrl));

        result.IsValid.Should().BeFalse();
        result.TrustedIssuer.Should().BeNull();
        result.Failure.Should().Contain("not an enabled trusted");
    }

    [Fact]
    public async Task The_issuer_match_is_exact()
    {
        await RegisterAsync();

        var result = await ValidateAsync(_key.CreateToken(Issuer + "/", ServiceUrl));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_disabled_issuer_is_refused()
    {
        await RegisterAsync(isEnabled: false);

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl));

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("not an enabled trusted");
    }

    [Fact]
    public async Task A_wrong_audience_is_refused_and_the_reason_names_both_values()
    {
        await RegisterAsync();

        var result = await ValidateAsync(_key.CreateToken(Issuer, "https://segue.example.org/cds-services/other-service"));

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("aud").And.Contain("other-service").And.Contain(ServiceUrl);
    }

    [Fact]
    public async Task The_audience_match_ignores_a_trailing_slash_and_case()
    {
        await RegisterAsync();

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl.ToUpperInvariant() + "/"));

        result.IsValid.Should().BeTrue(result.Failure);
    }

    [Fact]
    public async Task The_issuer_audience_override_replaces_the_service_url()
    {
        await RegisterAsync(audience: "urn:segue:cds");

        (await ValidateAsync(_key.CreateToken(Issuer, "urn:segue:cds"))).IsValid.Should().BeTrue();
        (await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        await RegisterAsync();

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, expiresUtc: DateTime.UtcNow.AddMinutes(-10)));

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("expired");
    }

    [Fact]
    public async Task A_token_expiring_further_out_than_the_maximum_lifetime_is_refused()
    {
        await RegisterAsync();

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, expiresUtc: DateTime.UtcNow.AddHours(3)));

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("exp");
    }

    [Fact]
    public async Task A_token_without_jti_is_refused()
    {
        await RegisterAsync();

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, includeJti: false));

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("jti");
    }

    [Fact]
    public async Task A_replayed_jti_is_refused_the_second_time()
    {
        await RegisterAsync();
        var validator = CreateValidator();
        var jti = Guid.NewGuid().ToString();

        var first = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, jti: jti), validator);
        var replay = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, jti: jti), validator);

        first.IsValid.Should().BeTrue(first.Failure);
        replay.IsValid.Should().BeFalse();
        replay.Failure.Should().Contain("replay");
    }

    [Fact]
    public async Task A_token_signed_by_a_different_key_is_refused()
    {
        await RegisterAsync();
        using var attacker = CdsTestSigningKey.Ec(kid: "attacker");

        var result = await ValidateAsync(attacker.CreateToken(Issuer, ServiceUrl));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_tampered_payload_fails_the_signature_check()
    {
        await RegisterAsync();
        var parts = _key.CreateToken(Issuer, ServiceUrl).Split('.');
        var payload = Base64UrlEncoder.Decode(parts[1]).Replace(ServiceUrl, ServiceUrl + "x", StringComparison.Ordinal);
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";

        var result = await ValidateAsync(tampered, audience: ServiceUrl + "x");

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_rejected_token_does_not_burn_its_jti()
    {
        await RegisterAsync();
        var validator = CreateValidator();
        var jti = Guid.NewGuid().ToString();

        var wrongAudience = await ValidateAsync(_key.CreateToken(Issuer, "https://elsewhere.example.org", jti: jti), validator);
        var good = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, jti: jti), validator);

        wrongAudience.IsValid.Should().BeFalse();
        good.IsValid.Should().BeTrue(good.Failure);
    }

    [Fact]
    public async Task Keys_are_fetched_from_the_jwks_url()
    {
        _jwksEndpoint.Body = _key.Jwks;
        await RegisterAsync(jwksUrl: "https://sandbox.cds-hooks.org/.well-known/jwks.json");

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl));

        result.IsValid.Should().BeTrue(result.Failure);
        _jwksEndpoint.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_jwks_url_is_cached_between_calls()
    {
        _jwksEndpoint.Body = _key.Jwks;
        await RegisterAsync(jwksUrl: "https://sandbox.cds-hooks.org/.well-known/jwks.json");
        var validator = CreateValidator();

        (await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl), validator)).IsValid.Should().BeTrue();
        (await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl), validator)).IsValid.Should().BeTrue();

        _jwksEndpoint.Calls.Should().Be(1);
    }

    [Fact]
    public async Task An_unreachable_jwks_url_refuses_the_call_without_throwing()
    {
        _jwksEndpoint.Status = HttpStatusCode.InternalServerError;
        await RegisterAsync(jwksUrl: "https://sandbox.cds-hooks.org/.well-known/jwks.json");

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl));

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("No usable signing keys");
    }

    [Fact]
    public async Task A_failed_jwks_fetch_is_remembered_so_bogus_kids_do_not_refetch_it()
    {
        _jwksEndpoint.Status = HttpStatusCode.InternalServerError;
        await RegisterAsync(jwksUrl: "https://sandbox.cds-hooks.org/.well-known/jwks.json");
        var validator = CreateValidator();

        // Each token names a kid the (empty) set does not hold, which would normally ask for a refresh too.
        for (var i = 0; i < 5; i++)
        {
            using var key = CdsTestSigningKey.Ec(kid: $"bogus-{i}");
            (await ValidateAsync(key.CreateToken(Issuer, ServiceUrl), validator)).IsValid.Should().BeFalse();
        }

        _jwksEndpoint.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_kids_within_the_cooldown_neither_refetch_nor_lose_the_good_keys()
    {
        _jwksEndpoint.Body = _key.Jwks;
        await RegisterAsync(jwksUrl: "https://sandbox.cds-hooks.org/.well-known/jwks.json");
        var validator = CreateValidator();
        (await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl), validator)).IsValid.Should().BeTrue();

        _jwksEndpoint.Status = HttpStatusCode.InternalServerError;
        using var rotated = CdsTestSigningKey.Ec(kid: "rotated");
        (await ValidateAsync(rotated.CreateToken(Issuer, ServiceUrl), validator)).IsValid.Should().BeFalse();
        (await ValidateAsync(rotated.CreateToken(Issuer, ServiceUrl), validator)).IsValid.Should().BeFalse();
        (await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl), validator)).IsValid.Should().BeTrue();

        _jwksEndpoint.Calls.Should().Be(1, "the unknown kids arrived within the cooldown of the last fetch attempt");
    }

    [Fact]
    public async Task Concurrent_cold_cache_lookups_share_one_fetch()
    {
        _jwksEndpoint.Body = _key.Jwks;
        _jwksEndpoint.Delay = TimeSpan.FromMilliseconds(200);
        var provider = CreateJwksProvider();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            provider.GetKeysAsync("https://sandbox.cds-hooks.org/.well-known/jwks.json", refresh: false, CancellationToken.None)));

        results.Should().AllSatisfy(keys => keys.Should().NotBeEmpty());
        _jwksEndpoint.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_plain_http_jwks_url_is_never_fetched()
    {
        _jwksEndpoint.Body = _key.Jwks;

        var keys = await CreateJwksProvider().GetKeysAsync("http://ehr.example.org/jwks.json", refresh: false, CancellationToken.None);

        keys.Should().BeEmpty();
        _jwksEndpoint.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_requests_with_one_token_accept_it_only_once()
    {
        await RegisterAsync();
        var validator = CreateValidator();
        var token = _key.CreateToken(Issuer, ServiceUrl);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => ValidateAsync(token, validator))));

        results.Count(r => r.IsValid).Should().Be(1);
    }

    [Fact]
    public async Task No_expected_audience_and_no_override_is_refused()
    {
        await RegisterAsync();

        var result = await CreateValidator().ValidateAsync($"Bearer {_key.CreateToken(Issuer, ServiceUrl)}", [], CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("Cds:PublicBaseUrl");
    }

    // ---- Epic: jku header and sub ----

    private const string EpicJwksUrl = "https://sandbox.cds-hooks.org/.well-known/jwks.json";

    [Fact]
    public async Task A_jku_naming_the_configured_jwks_url_is_accepted_after_normalization()
    {
        _jwksEndpoint.Body = _key.Jwks;
        await RegisterAsync(jwksUrl: EpicJwksUrl);

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, jku: "https://SANDBOX.cds-hooks.org:443/.well-known/jwks.json/"));

        result.IsValid.Should().BeTrue(result.Failure);
    }

    [Fact]
    public async Task A_jku_naming_another_url_is_refused_and_never_fetched()
    {
        _jwksEndpoint.Body = _key.Jwks;
        await RegisterAsync(jwksUrl: EpicJwksUrl);

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, jku: "https://attacker.example.org/jwks.json"));

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("jku").And.NotContain("attacker", "the failure text does not echo the token's values");
        _jwksEndpoint.Urls.Should().NotContain(url => url.Contains("attacker", StringComparison.Ordinal));
        _jwksEndpoint.Calls.Should().Be(0, "the jku is checked before any key lookup");
    }

    [Fact]
    public async Task A_jku_is_refused_when_the_issuer_has_only_a_pasted_jwks()
    {
        await RegisterAsync();

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, jku: EpicJwksUrl));

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("no JWKS URL");
        _jwksEndpoint.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_refused_jku_does_not_burn_the_jti()
    {
        _jwksEndpoint.Body = _key.Jwks;
        await RegisterAsync(jwksUrl: EpicJwksUrl);
        var validator = CreateValidator();
        var jti = Guid.NewGuid().ToString();

        var refused = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, jti: jti, jku: "https://other.example.org/jwks"), validator);
        var good = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, jti: jti, jku: EpicJwksUrl), validator);

        refused.IsValid.Should().BeFalse();
        good.IsValid.Should().BeTrue(good.Failure);
    }

    [Fact]
    public async Task The_expected_sub_must_match_exactly_when_set()
    {
        await RegisterAsync(expectedSubject: "epic-cds-client-1");

        (await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, subject: "epic-cds-client-1"))).IsValid.Should().BeTrue();

        var wrong = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, subject: "EPIC-CDS-CLIENT-1"));
        var missing = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl));

        wrong.IsValid.Should().BeFalse();
        wrong.Failure.Should().Contain("sub").And.NotContain("EPIC-CDS-CLIENT-1");
        missing.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Without_an_expected_sub_any_sub_is_accepted()
    {
        await RegisterAsync();

        var result = await ValidateAsync(_key.CreateToken(Issuer, ServiceUrl, subject: "whatever-client"));

        result.IsValid.Should().BeTrue(result.Failure);
    }

    [Fact]
    public async Task An_epic_shaped_rs384_token_with_jku_and_sub_is_accepted()
    {
        using var epicKey = CdsTestSigningKey.Rsa(SecurityAlgorithms.RsaSha384);
        _jwksEndpoint.Body = epicKey.Jwks;
        const string epicIssuer = "https://ehr.example.org/api/FHIR/R4";
        await RegisterAsync(jwksUrl: EpicJwksUrl, issuer: epicIssuer, expectedSubject: "cds-client-id");

        var result = await ValidateAsync(epicKey.CreateToken(epicIssuer, ServiceUrl, subject: "cds-client-id", jku: EpicJwksUrl));

        result.IsValid.Should().BeTrue(result.Failure);
        _jwksEndpoint.Urls.Should().OnlyContain(url => url == EpicJwksUrl);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("64:ff9b::a9fe:a9fe", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("64:ff9b:1::a9fe:a9fe", false)]
    [InlineData("2002:a9fe:a9fe::", false)]
    [InlineData("2002:c0a8:0101::1", false)]
    [InlineData("::a9fe:a9fe", false)]
    [InlineData("::", false)]
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("2002:808:808::1", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("104.18.0.1", true)]
    [InlineData("2606:4700::1111", true)]
    public void Only_public_addresses_may_serve_a_jwks(string address, bool isPublic)
    {
        CdsJwksHttpHandler.IsPublicAddress(IPAddress.Parse(address)).Should().Be(isPublic);
    }

    [Fact]
    public void The_guarded_handler_never_uses_a_proxy()
    {
        using var handler = CdsJwksHttpHandler.CreateGuarded(() => false);

        handler.UseProxy.Should().BeFalse();
        handler.Proxy.Should().BeNull();
        handler.AllowAutoRedirect.Should().BeFalse();
    }

    /// <summary>Answers every request with a configurable JWKS body and counts the calls.</summary>
    private sealed class StubJwksHandler : HttpMessageHandler
    {
        private int _calls;

        public string Body { get; set; } = "{\"keys\":[]}";

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public int Calls => _calls;

        public System.Collections.Concurrent.ConcurrentBag<string> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Urls.Add(request.RequestUri!.ToString());
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
        }
    }
}
