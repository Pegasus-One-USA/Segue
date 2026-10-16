using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// The public HL7 CDS Hooks 2.0 endpoints end to end: discovery, an invoke signed by a registered trusted issuer
/// (pasted JWKS), rejection of unsigned/badly signed calls, the browser-sandbox CORS policy, feedback, the admin
/// permissions, and the Activity rows, which must never hold patient data.
/// </summary>
[Collection("ApiTests")]
public sealed class CdsHooksTests : IDisposable
{
    private const string SandboxOrigin = "https://sandbox.cds-hooks.org";

    private readonly ApiFixture _fixture;
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
    private readonly string _kid = Guid.NewGuid().ToString("N");

    /// <summary>Unique per test so the shared in-memory host never sees two tests' issuers collide.</summary>
    private readonly string _issuer = $"https://ehr-{Guid.NewGuid():N}.example.org";

    public CdsHooksTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose() => _signingKey.Dispose();

    // ---- Discovery ----

    [Fact]
    public async Task Discovery_is_anonymous_and_lists_only_enabled_services_with_their_prefetch()
    {
        var enabled = await CreateServiceAsync("drug-allergy", "order-sign");
        var disabled = await CreateServiceAsync("drug-allergy", "order-sign", isEnabled: false);

        var resp = await _fixture.AnonClient.GetAsync("/cds-services");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var services = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("services").EnumerateArray().ToArray();
        var listed = Assert.Single(services, s => s.GetProperty("id").GetString() == enabled.ServiceId);
        Assert.Equal("order-sign", listed.GetProperty("hook").GetString());
        Assert.Contains("{{context.patientId}}", listed.GetProperty("prefetch").GetProperty("allergies").GetString());
        Assert.DoesNotContain(services, s => s.GetProperty("id").GetString() == disabled.ServiceId);
    }

    // ---- Invoke ----

    [Fact]
    public async Task A_call_signed_by_a_trusted_issuer_returns_cards()
    {
        await TrustIssuerAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign");

        var resp = await InvokeAsync(service, await SampleRequestAsync(service), Token(service.ServiceUrl));

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        var card = Assert.Single(JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("cards").EnumerateArray());
        Assert.Equal("critical", card.GetProperty("indicator").GetString());
        Assert.True(card.GetProperty("summary").GetString()!.Length <= 140);
        Assert.False(string.IsNullOrEmpty(card.GetProperty("uuid").GetString()));
        Assert.Equal("CDS integration test", card.GetProperty("source").GetProperty("label").GetString());
    }

    [Fact]
    public async Task A_call_without_a_token_is_401()
    {
        await TrustIssuerAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign");

        var resp = await InvokeAsync(service, await SampleRequestAsync(service), token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("Bearer", resp.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task A_call_signed_with_an_unregistered_key_is_401()
    {
        await TrustIssuerAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign");
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        var resp = await InvokeAsync(service, await SampleRequestAsync(service), Token(service.ServiceUrl, otherKey));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task A_replayed_token_is_401_the_second_time()
    {
        await TrustIssuerAsync();
        var service = await CreateServiceAsync("prior-auth", "order-sign");
        var token = Token(service.ServiceUrl);
        var body = await SampleRequestAsync(service);

        var first = await InvokeAsync(service, body, token);
        var second = await InvokeAsync(service, body, token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Missing_prefetch_without_a_fhir_server_is_412()
    {
        await TrustIssuerAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign");
        var body = await SampleRequestAsync(service);
        body.Remove("prefetch");

        var resp = await InvokeAsync(service, body, Token(service.ServiceUrl));

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
    }

    [Fact]
    public async Task A_body_without_a_json_content_type_reaches_the_service_and_is_logged()
    {
        await TrustIssuerAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/cds-services/{service.ServiceId}")
        {
            Content = new StringContent("not json at all", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(service.ServiceUrl));

        var resp = await _fixture.AnonClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("not valid JSON", await resp.Content.ReadAsStringAsync());
        var row = Assert.Single(JsonDocument.Parse(await ActivityAsync(service.ServiceId)).RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(400, row.GetProperty("httpStatus").GetInt32());
    }

    [Fact]
    public async Task An_odd_value_in_an_unused_optional_member_does_not_reject_the_call()
    {
        await TrustIssuerAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign");
        var body = await SampleRequestAsync(service);
        // expires_in as a string and no subject: v1 never reads fhirAuthorization, so the call is still evaluated.
        body["fhirAuthorization"] = new JsonObject { ["access_token"] = "t", ["token_type"] = "Bearer", ["expires_in"] = "300" };

        var resp = await InvokeAsync(service, body, Token(service.ServiceUrl));

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task A_service_id_in_mixed_case_is_found_by_the_lower_case_activity_filter()
    {
        await TrustIssuerAsync();
        var service = await CreateServiceAsync("prior-auth", "order-sign");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/cds-services/{service.ServiceId.ToUpperInvariant()}")
        {
            Content = new StringContent((await SampleRequestAsync(service)).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(service.ServiceUrl));

        await _fixture.AnonClient.SendAsync(request);

        Assert.Single(JsonDocument.Parse(await ActivityAsync(service.ServiceId)).RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task An_unknown_service_is_404()
    {
        var resp = await _fixture.AnonClient.PostAsJsonAsync("/cds-services/no-such-service-here", new { hook = "order-sign" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task A_portal_cookie_on_the_call_does_not_trip_the_csrf_check()
    {
        await TrustIssuerAsync();
        var service = await CreateServiceAsync("prior-auth", "order-sign");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/cds-services/{service.ServiceId}")
        {
            Content = JsonContent.Create(await SampleRequestAsync(service)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(service.ServiceUrl));
        request.Headers.Add("Cookie", "fhirbridge_access_token=stale-portal-session");

        var resp = await _fixture.AnonClient.SendAsync(request);

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    // ---- Feedback ----

    [Fact]
    public async Task Feedback_signed_for_the_feedback_url_returns_200()
    {
        await TrustIssuerAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign");
        var body = new
        {
            feedback = new[]
            {
                new
                {
                    card = Guid.NewGuid().ToString(),
                    outcome = "overridden",
                    overrideReason = new { reason = new { code = "patient-tolerated" }, userComment = "free text" },
                    outcomeTimestamp = DateTime.UtcNow.ToString("O"),
                },
            },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/cds-services/{service.ServiceId}/feedback") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(service.FeedbackUrl));

        var resp = await _fixture.AnonClient.SendAsync(request);

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        var activity = await ActivityAsync(service.ServiceId);
        Assert.DoesNotContain("free text", activity);
    }

    // ---- CORS (the browser-based CDS Hooks sandbox) ----

    [Fact]
    public async Task Preflight_on_an_invoke_url_is_allowed_for_any_origin_without_credentials()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/cds-services/any-service");
        request.Headers.Add("Origin", SandboxOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var resp = await _fixture.AnonClient.SendAsync(request);

        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode}");
        Assert.Equal("*", Assert.Single(resp.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(resp.Headers.Contains("Access-Control-Allow-Credentials"));
        var allowedHeaders = string.Join(",", resp.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant();
        Assert.Contains("authorization", allowedHeaders);
        Assert.Contains("content-type", allowedHeaders);
        Assert.Contains("POST", string.Join(",", resp.Headers.GetValues("Access-Control-Allow-Methods")));
    }

    [Fact]
    public async Task Discovery_answers_a_cross_origin_get_with_a_wildcard_origin()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/cds-services");
        request.Headers.Add("Origin", $"https://{Guid.NewGuid():N}.example.net");

        var resp = await _fixture.AnonClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("*", Assert.Single(resp.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(resp.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task The_admin_api_keeps_the_portal_cors_policy()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/cds/services");
        request.Headers.Add("Origin", SandboxOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var resp = await _fixture.AnonClient.SendAsync(request);

        Assert.False(resp.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ---- Activity (no PHI) ----

    [Fact]
    public async Task The_activity_row_for_an_invoke_holds_no_patient_id_or_prefetch_content()
    {
        await TrustIssuerAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign");
        var patientId = $"patient-{Guid.NewGuid():N}";
        var body = await SampleRequestAsync(service);
        body["context"]!["patientId"] = patientId;
        body["fhirAuthorization"] = new JsonObject
        {
            ["access_token"] = "ehr-token-must-not-be-stored",
            ["token_type"] = "Bearer",
            ["expires_in"] = 300,
            ["scope"] = "patient/*.read",
            ["subject"] = "cds-client",
            ["patient"] = patientId,
        };

        var resp = await InvokeAsync(service, body, Token(service.ServiceUrl));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var activity = await ActivityAsync(service.ServiceId);
        var row = Assert.Single(JsonDocument.Parse(activity).RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Success", row.GetProperty("outcome").GetString());
        Assert.Equal(1, row.GetProperty("cardCount").GetInt32());
        Assert.Equal(_issuer, row.GetProperty("issuer").GetString());
        Assert.Equal(body["hookInstance"]!.GetValue<string>(), row.GetProperty("hookInstance").GetString());
        Assert.DoesNotContain(patientId, activity);
        Assert.DoesNotContain("ehr-token-must-not-be-stored", activity);
        Assert.DoesNotContain("penicillin", activity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amoxicillin", activity, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Admin permissions ----

    [Theory]
    [InlineData("/api/v1/cds/services")]
    [InlineData("/api/v1/cds/trusted-issuers")]
    [InlineData("/api/v1/cds/handlers")]
    [InlineData("/api/v1/cds/discovery-info")]
    [InlineData("/api/v1/cds/activity")]
    public async Task Admin_reads_need_a_session(string url)
    {
        var resp = await _fixture.AnonClient.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_reads_need_cdshooks_view()
    {
        var (_, _, jwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("workflow.view");
        using var client = _fixture.CreateAuthenticatedClient(jwt);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/cds/services")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/cds/trusted-issuers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/cds/activity")).StatusCode);
    }

    [Fact]
    public async Task Cdshooks_view_reads_but_cannot_create_edit_delete_or_test()
    {
        var service = await CreateServiceAsync("prior-auth", "order-sign");
        var (_, _, jwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("cdshooks.view");
        using var client = _fixture.CreateAuthenticatedClient(jwt);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/cds/services")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/cds/handlers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/cds/services", ServiceBody("prior-auth", "order-sign", true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/v1/cds/services/{service.Id}", ServiceBody("prior-auth", "order-sign", true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/v1/cds/services/{service.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/cds/services/{service.Id}/test", await SampleRequestAsync(service))).StatusCode);
    }

    [Fact]
    public async Task Cdshooks_create_may_add_a_service_and_cdshooks_test_may_run_it()
    {
        var (_, _, jwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("cdshooks.view", "cdshooks.create", "cdshooks.test");
        using var client = _fixture.CreateAuthenticatedClient(jwt);

        var created = await client.PostAsJsonAsync("/api/v1/cds/services", ServiceBody("renal-dose", "order-sign", true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var sample = await (await client.GetAsync($"/api/v1/cds/services/{id}/sample-request")).Content.ReadFromJsonAsync<JsonObject>();

        var test = await client.PostAsJsonAsync($"/api/v1/cds/services/{id}/test", sample);

        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var result = JsonDocument.Parse(await test.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(200, result.GetProperty("httpStatus").GetInt32());
        Assert.NotEmpty(result.GetProperty("response").GetProperty("cards").EnumerateArray());
    }

    [Fact]
    public async Task An_invalid_service_id_is_a_400_with_the_reason()
    {
        var resp = await _fixture.AdminClient.PostAsJsonAsync(
            "/api/v1/cds/services",
            new
            {
                ServiceId = "Not A Slug!",
                Title = "Bad",
                HandlerKey = "prior-auth",
                Hook = "order-sign",
                IsEnabled = true,
            });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Service id", await resp.Content.ReadAsStringAsync());
    }

    // ---- Helpers ----

    private sealed record ServiceInfo(Guid Id, string ServiceId, string ServiceUrl, string FeedbackUrl);

    private static object ServiceBody(string handlerKey, string hook, bool isEnabled, string? serviceId = null) => new
    {
        ServiceId = serviceId ?? $"it-{handlerKey}-{Guid.NewGuid():N}",
        Title = $"IT {handlerKey}",
        HandlerKey = handlerKey,
        Hook = hook,
        IsEnabled = isEnabled,
        CardSourceLabel = "CDS integration test",
    };

    private async Task<ServiceInfo> CreateServiceAsync(string handlerKey, string hook, bool isEnabled = true)
    {
        var resp = await _fixture.AdminClient.PostAsJsonAsync("/api/v1/cds/services", ServiceBody(handlerKey, hook, isEnabled));
        await ApiFixture.EnsureOkAsync(resp);
        var created = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("id").GetGuid();

        // The URLs to register with an EHR: also the exact aud each call must be signed for.
        var infoResp = await _fixture.AdminClient.GetAsync("/api/v1/cds/discovery-info");
        await ApiFixture.EnsureOkAsync(infoResp);
        var entry = JsonDocument.Parse(await infoResp.Content.ReadAsStringAsync()).RootElement.GetProperty("services")
            .EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);
        return new ServiceInfo(
            id,
            entry.GetProperty("serviceId").GetString()!,
            entry.GetProperty("serviceUrl").GetString()!,
            entry.GetProperty("feedbackUrl").GetString()!);
    }

    private async Task TrustIssuerAsync()
    {
        var parameters = _signingKey.ExportParameters(false);
        var jwks = new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-384",
                ["kid"] = _kid,
                ["use"] = "sig",
                ["alg"] = "ES384",
                ["x"] = Base64UrlEncoder.Encode(parameters.Q.X),
                ["y"] = Base64UrlEncoder.Encode(parameters.Q.Y),
            }),
        };

        var resp = await _fixture.AdminClient.PostAsJsonAsync("/api/v1/cds/trusted-issuers", new
        {
            Name = "Integration test EHR",
            Vendor = "Other",
            Issuer = _issuer,
            JwksJson = jwks.ToJsonString(),
            IsEnabled = true,
        });
        await ApiFixture.EnsureOkAsync(resp);
    }

    private string Token(string audience, ECDsa? key = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString() },
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key ?? _signingKey) { KeyId = _kid }, SecurityAlgorithms.EcdsaSha384),
        });

    private async Task<JsonObject> SampleRequestAsync(ServiceInfo service)
    {
        var resp = await _fixture.AdminClient.GetAsync($"/api/v1/cds/services/{service.Id}/sample-request");
        await ApiFixture.EnsureOkAsync(resp);
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
    }

    private async Task<HttpResponseMessage> InvokeAsync(ServiceInfo service, JsonObject body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/cds-services/{service.ServiceId}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _fixture.AnonClient.SendAsync(request);
    }

    private async Task<string> ActivityAsync(string serviceId)
    {
        var resp = await _fixture.AdminClient.GetAsync($"/api/v1/cds/activity?serviceId={Uri.EscapeDataString(serviceId)}");
        await ApiFixture.EnsureOkAsync(resp);
        return await resp.Content.ReadAsStringAsync();
    }
}
