using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Api.IntegrationTests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// CDS Hooks the way Epic calls it (Phase 1b / 2), end to end through the API host: an RS384 JWT whose header names
/// the JWK Set URL (jku) and whose sub is the health system's CDS client id; cards with a static topic code, the
/// service's dismiss reasons, links and one-click fixes; Epic's batched feedback; the FHIR callback for missing
/// prefetch; and the admin setup-sheet and test-callback endpoints with their permissions. The EHR's JWKS and FHIR
/// server are in-process stubs (<see cref="CdsTestHttpEndpoints"/>).
/// </summary>
[Collection("ApiTests")]
public sealed class CdsHooksEpicTests : IDisposable
{
    private readonly ApiFixture _fixture;
    private readonly RSA _signingKey = RSA.Create(2048);
    private readonly string _kid = Guid.NewGuid().ToString("N");

    /// <summary>Epic's iss is its FHIR base. Unique per test so stubs and issuers never collide on the shared host.</summary>
    private readonly string _fhirBase = $"https://ehr-{Guid.NewGuid():N}.example.org/api/FHIR/R4";

    private readonly string _clientId = $"epic-cds-client-{Guid.NewGuid():N}";

    public CdsHooksEpicTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose() => _signingKey.Dispose();

    /// <summary>The EHR's JWK Set URL, on the EHR's host but outside its FHIR base.</summary>
    private string JwksUrl => _fhirBase.Replace("/api/FHIR/R4", "/api/epic/2019/Security/Open/PublicKeys/jwks.json", StringComparison.Ordinal);

    // ---- Invoke: Epic's token and card shape ----

    [Fact]
    public async Task An_epic_rs384_token_with_jku_and_sub_gets_cards_with_topic_reasons_links_and_fixes()
    {
        var issuerId = await TrustEpicAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign", EpicServiceOptions(), issuerId);
        var body = await SampleRequestAsync(service);

        var resp = await InvokeAsync(service, body, Token(service.ServiceUrl));

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        var card = Assert.Single(JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["cards"]!.AsArray())!;
        Assert.Equal("DrugAllergyMatch", card["source"]!["topic"]!["code"]!.GetValue<string>());
        Assert.Equal(new[] { "pt_declined", "will-monitor" }, card["overrideReasons"]!.AsArray().Select(r => r!["code"]!.GetValue<string>()));
        Assert.Equal("any", card["selectionBehavior"]!.GetValue<string>());

        var suggestions = card["suggestions"]!.AsArray();
        Assert.Equal(2, suggestions.Count);
        Assert.All(suggestions, s =>
        {
            Assert.Single(s!["actions"]!.AsArray());
            Assert.True(Guid.TryParse(s["uuid"]!.GetValue<string>(), out _));
        });
        var delete = suggestions[0]!["actions"]![0]!;
        Assert.Equal("delete", delete["type"]!.GetValue<string>());
        Assert.Equal("MedicationRequest/draft-amoxicillin", delete["resourceId"]!.GetValue<string>());
        Assert.True(suggestions[0]!["isRecommended"]!.GetValue<bool>());
        var create = suggestions[1]!["actions"]![0]!;
        Assert.Equal("create", create["type"]!.GetValue<string>());
        Assert.Equal("draft", create["resource"]!["status"]!.GetValue<string>());
        Assert.Equal(
            "urn:com.epic.cdshooks.action.code.system.preference-list-item",
            create["resource"]!["medicationCodeableConcept"]!["coding"]![0]!["system"]!.GetValue<string>());

        var links = card["links"]!.AsArray();
        Assert.Equal(2, links.Count);
        Assert.Null(links[0]!["appContext"]);
        Assert.Equal("smart", links[1]!["type"]!.GetValue<string>());
        Assert.Equal("launch-ctx", links[1]!["appContext"]!.GetValue<string>());

        var row = Assert.Single(JsonNode.Parse(await ActivityAsync(service.ServiceId))!["items"]!.AsArray())!;
        Assert.Equal(2, row["suggestionsOffered"]!.GetValue<int>());
        Assert.Equal("18", row["triggerAction"]!.GetValue<string>());
        Assert.Equal("11.2", row["epicVersion"]!.GetValue<string>());
        Assert.Single(CdsTestHttpEndpoints.SeenUnder(JwksUrl));
    }

    [Fact]
    public async Task A_token_whose_jku_is_not_the_issuer_jwks_url_is_401_and_that_url_is_never_fetched()
    {
        await TrustEpicAsync();
        var service = await CreateServiceAsync("prior-auth", "order-sign");
        var foreignJku = $"https://attacker-{Guid.NewGuid():N}.example.org/jwks.json";

        var resp = await InvokeAsync(service, await SampleRequestAsync(service), Token(service.ServiceUrl, jku: foreignJku));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(CdsTestHttpEndpoints.SeenUnder(foreignJku));
        var row = Assert.Single(JsonNode.Parse(await ActivityAsync(service.ServiceId))!["items"]!.AsArray())!;
        Assert.Contains("jku", row["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_token_whose_sub_is_not_the_expected_client_id_is_401()
    {
        await TrustEpicAsync();
        var service = await CreateServiceAsync("prior-auth", "order-sign");

        var resp = await InvokeAsync(service, await SampleRequestAsync(service), Token(service.ServiceUrl, subject: "some-other-client"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Without_fixes_configured_epic_gets_cards_with_no_suggestions()
    {
        await TrustEpicAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign");

        var resp = await InvokeAsync(service, await SampleRequestAsync(service), Token(service.ServiceUrl));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("suggestions", json);
        Assert.DoesNotContain("selectionBehavior", json);
    }

    // ---- Feedback in Epic's format ----

    [Fact]
    public async Task Epic_batched_feedback_is_recorded_as_codes_and_counts_only()
    {
        await TrustEpicAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign");
        const string comment = "Patient Jane Roe tolerated it at age 12";
        var body = $$"""
            {"feedback":[
              {"card":"{{Guid.NewGuid()}}","outcome":"overridden","outcomeTimestamp":"2026-10-12T10:00:00Z",
               "overrideReasons":{"extension":{"com.epic.cdshooks.feedback.overrideReason.reasonCategory":"45",
                                               "com.epic.cdshooks.feedback.overrideReason.reasonTitle":"Jane Roe title"},
                                  "userComment":"{{comment}}"} },
              {"card":"{{Guid.NewGuid()}}","outcome":"overridden","outcomeTimestamp":"2026-10-12T10:00:01Z",
               "overrideReasons":{"reason":{"code":"will-monitor","display":"Will monitor","system":"urn:segue:cds:override-reason"},
                                  "userComment":"{{comment}}"} },
              {"card":"{{Guid.NewGuid()}}","outcome":"accepted","outcomeTimestamp":"2026-10-12T10:00:02Z",
               "acceptedSuggestions":[{"id":"{{Guid.NewGuid()}}"}]}
            ]}
            """;
        var request = new HttpRequestMessage(HttpMethod.Post, $"/cds-services/{service.ServiceId}/feedback")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(service.FeedbackUrl));

        var resp = await _fixture.AnonClient.SendAsync(request);

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        var activity = await ActivityAsync(service.ServiceId);
        var row = Assert.Single(JsonNode.Parse(activity)!["items"]!.AsArray())!;
        Assert.Equal("Feedback", row["kind"]!.GetValue<string>());
        Assert.Equal(3, row["cardCount"]!.GetValue<int>());
        Assert.Equal(1, row["suggestionsAccepted"]!.GetValue<int>());
        Assert.Equal(new[] { "45", "will-monitor" }, row["overrideReasonCodes"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.DoesNotContain("Jane Roe", activity);
        Assert.DoesNotContain("age 12", activity);
    }

    // ---- FHIR callback for missing prefetch ----

    [Fact]
    public async Task Missing_prefetch_is_fetched_from_the_epic_fhir_server_with_its_token_and_only_counts_are_recorded()
    {
        await TrustEpicAsync();
        var service = await CreateServiceAsync("care-gap", "patient-view");
        var body = await SampleRequestAsync(service);
        var patientId = $"epic-patient-{Guid.NewGuid():N}";
        var prefetch = body["prefetch"]!.AsObject();
        ServeFhir(prefetch, patientId);
        body.Remove("prefetch");
        body["context"]!["patientId"] = patientId;
        body["fhirServer"] = _fhirBase;
        body["fhirAuthorization"] = new JsonObject
        {
            ["access_token"] = "epic-access-token-must-not-be-stored",
            ["token_type"] = "Bearer",
            ["expires_in"] = 300,
            ["scope"] = "patient/*.read",
            ["subject"] = _clientId,
            ["patient"] = patientId,
        };

        var resp = await InvokeAsync(service, body, Token(service.ServiceUrl));

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        Assert.NotEmpty(JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["cards"]!.AsArray());
        var fetched = CdsTestHttpEndpoints.SeenUnder(_fhirBase + "/");
        Assert.Equal(5, fetched.Count(seen => !seen.Url.EndsWith("/metadata", StringComparison.Ordinal)));
        Assert.All(fetched, seen => Assert.Equal("Bearer epic-access-token-must-not-be-stored", seen.Authorization));

        var activity = await ActivityAsync(service.ServiceId);
        var row = Assert.Single(JsonNode.Parse(activity)!["items"]!.AsArray())!;
        Assert.Equal("Success", row["outcome"]!.GetValue<string>());
        Assert.True(row["callbackResourceCount"]!.GetValue<int>() >= 4);
        Assert.NotNull(row["callbackMs"]);
        Assert.DoesNotContain(patientId, activity);
        Assert.DoesNotContain("epic-access-token-must-not-be-stored", activity);
    }

    [Fact]
    public async Task A_fhir_server_that_is_not_the_issuer_fhir_base_is_never_called()
    {
        await TrustEpicAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign");
        var body = await SampleRequestAsync(service);
        body.Remove("prefetch");
        var elsewhere = $"https://elsewhere-{Guid.NewGuid():N}.example.org/fhir";
        body["fhirServer"] = elsewhere;
        body["fhirAuthorization"] = new JsonObject { ["access_token"] = "t", ["token_type"] = "Bearer", ["subject"] = _clientId };

        var resp = await InvokeAsync(service, body, Token(service.ServiceUrl));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["cards"]!.AsArray());
        Assert.Empty(CdsTestHttpEndpoints.SeenUnder(elsewhere));
        var row = Assert.Single(JsonNode.Parse(await ActivityAsync(service.ServiceId))!["items"]!.AsArray())!;
        Assert.Equal("Degraded", row["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void The_callback_client_has_no_request_logging_or_retry_handlers()
    {
        var factory = _fixture.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        var handler = factory.CreateHandler("ICdsFhirCallbackClient");

        var chain = new List<string>();
        for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(current.GetType().Name);
        }

        Assert.Contains(nameof(CdsTestHttpEndpoints.Handler), chain);
        Assert.DoesNotContain("ApiRequestLoggingHandler", chain);
        Assert.DoesNotContain(chain, name => name.Contains("Resilience", StringComparison.Ordinal));
        Assert.DoesNotContain(chain, name => name.StartsWith("Logging", StringComparison.Ordinal));
    }

    // ---- Admin: setup sheet ----

    [Fact]
    public async Task The_setup_sheet_needs_a_session_and_cdshooks_view()
    {
        var service = await CreateServiceAsync("prior-auth", "order-sign");
        var url = $"/api/v1/cds/services/{service.Id}/setup-sheet";
        var (_, _, otherJwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("workflow.view");
        using var other = _fixture.CreateAuthenticatedClient(otherJwt);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.AnonClient.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task The_setup_sheet_gives_a_viewer_the_values_epic_must_configure()
    {
        var issuerId = await TrustEpicAsync();
        var service = await CreateServiceAsync("drug-allergy", "order-sign", EpicServiceOptions(), issuerId);
        var (_, _, jwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("cdshooks.view");
        using var viewer = _fixture.CreateAuthenticatedClient(jwt);

        var resp = await viewer.GetAsync($"/api/v1/cds/services/{service.Id}/setup-sheet");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sheet = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.Equal(service.ServiceUrl, sheet["serviceUrl"]!.GetValue<string>());
        Assert.Equal(service.FeedbackUrl, sheet["feedbackUrl"]!.GetValue<string>());
        Assert.Equal("allergies", sheet["prefetch"]![0]!["key"]!.GetValue<string>());
        Assert.True(sheet["prefetch"]![0]!["fetchedIfMissing"]!.GetValue<bool>());
        Assert.Equal(new[] { "pt_declined", "will-monitor" }, sheet["overrideReasons"]!.AsArray().Select(r => r!["code"]!.GetValue<string>()));
        Assert.Contains(sheet["fixes"]!.AsArray(), fix => fix!["code"]?.GetValue<string>() == "PL-AZITHRO");
        Assert.Contains(sheet["links"]!.AsArray(), link => link!["type"]!.GetValue<string>() == "smart");
        var issuer = Assert.Single(sheet["issuers"]!.AsArray())!;
        Assert.Equal(_fhirBase, issuer["issuer"]!.GetValue<string>());
        Assert.Equal(JwksUrl, issuer["jwksUrl"]!.GetValue<string>());
        Assert.Equal(_clientId, issuer["expectedSubject"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_setup_sheet_of_an_unknown_service_is_404()
    {
        var resp = await _fixture.AdminClient.GetAsync($"/api/v1/cds/services/{Guid.NewGuid()}/setup-sheet");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- Admin: test callback ----

    [Fact]
    public async Task Test_callback_needs_cdshooks_test()
    {
        var issuerId = await TrustEpicAsync();
        var url = $"/api/v1/cds/trusted-issuers/{issuerId}/test-callback";
        var (_, _, viewJwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("cdshooks.view");
        using var viewer = _fixture.CreateAuthenticatedClient(viewJwt);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.AnonClient.PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(url, null)).StatusCode);
        Assert.Empty(CdsTestHttpEndpoints.SeenUnder($"{_fhirBase}/metadata"));
    }

    [Fact]
    public async Task Test_callback_reads_metadata_without_a_token()
    {
        var issuerId = await TrustEpicAsync();
        CdsTestHttpEndpoints.Map($"{_fhirBase}/metadata", _ => CdsTestHttpEndpoints.Json(
            """{"resourceType":"CapabilityStatement","fhirVersion":"4.0.1","software":{"name":"Epic","version":"Test"}}"""));
        var (_, _, jwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("cdshooks.view", "cdshooks.test");
        using var tester = _fixture.CreateAuthenticatedClient(jwt);

        var resp = await tester.PostAsync($"/api/v1/cds/trusted-issuers/{issuerId}/test-callback", null);

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        var result = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.True(result["reachable"]!.GetValue<bool>());
        Assert.Equal("4.0.1", result["fhirVersion"]!.GetValue<string>());
        Assert.Equal(_fhirBase, result["fhirBaseUrl"]!.GetValue<string>());
        var probe = Assert.Single(CdsTestHttpEndpoints.SeenUnder($"{_fhirBase}/metadata"));
        Assert.Null(probe.Authorization);
    }

    [Fact]
    public async Task Test_callback_for_an_unknown_issuer_is_404()
    {
        var resp = await _fixture.AdminClient.PostAsync($"/api/v1/cds/trusted-issuers/{Guid.NewGuid()}/test-callback", null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- Helpers ----

    private sealed record ServiceInfo(Guid Id, string ServiceId, string ServiceUrl, string FeedbackUrl);

    /// <summary>Fixes (remove + alternative), two dismiss reasons and an absolute plus a SMART link.</summary>
    private static object EpicServiceOptions() => new
    {
        Fixes = new
        {
            Enabled = true,
            FixTypes = new object[]
            {
                new { Key = "remove-draft-order", Enabled = true, Mappings = Array.Empty<object>() },
                new
                {
                    Key = "add-alternative-medication",
                    Enabled = true,
                    Mappings = new[]
                    {
                        new { Trigger = "penicillin", System = "urn:com.epic.cdshooks.action.code.system.preference-list-item", Code = "PL-AZITHRO", Display = "AZITHROMYCIN 250 MG" },
                    },
                },
            },
            RecommendedFixType = "remove-draft-order",
        },
        OverrideReasons = new[]
        {
            new { Code = "pt_declined", Display = "Patient declined" },
            new { Code = "will-monitor", Display = "Will monitor" },
        },
        Links = new object[]
        {
            new { Label = "Allergy guideline", Url = "https://guides.example.org/allergy", Type = "absolute" },
            new { Label = "Allergy app", Url = "https://app.example.org/launch", Type = "smart", AppContext = "launch-ctx", Autolaunchable = false },
        },
    };

    /// <summary>Registers the EHR as Epic would be: iss = FHIR base, JWKS URL (served by the stub) and expected sub.</summary>
    private async Task<Guid> TrustEpicAsync()
    {
        var parameters = _signingKey.ExportParameters(false);
        var jwks = new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "RSA",
                ["kid"] = _kid,
                ["use"] = "sig",
                ["alg"] = "RS384",
                ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
                ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
            }),
        }.ToJsonString();
        CdsTestHttpEndpoints.Map(JwksUrl, _ => CdsTestHttpEndpoints.Json(jwks));

        var resp = await _fixture.AdminClient.PostAsJsonAsync("/api/v1/cds/trusted-issuers", new
        {
            Name = "Integration Epic",
            Vendor = "Epic",
            Issuer = _fhirBase,
            JwksUrl,
            IsEnabled = true,
            ExpectedSubject = _clientId,
            AllowCallback = true,
        });
        await ApiFixture.EnsureOkAsync(resp);
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["id"]!.GetValue<Guid>();
    }

    /// <summary>Serves the sample's prefetch from the stub FHIR server, by the resource type each key queries.</summary>
    private void ServeFhir(JsonObject prefetch, string patientId)
    {
        var patient = prefetch["patient"]!.DeepClone().AsObject();
        patient["id"] = patientId;
        CdsTestHttpEndpoints.Map($"{_fhirBase}/Patient/", _ => CdsTestHttpEndpoints.Json(patient.ToJsonString()));
        CdsTestHttpEndpoints.Map($"{_fhirBase}/Condition?", _ => CdsTestHttpEndpoints.Json(prefetch["conditions"]!.ToJsonString()));
        CdsTestHttpEndpoints.Map($"{_fhirBase}/Observation?", _ => CdsTestHttpEndpoints.Json(prefetch["hba1c"]!.ToJsonString()));
        CdsTestHttpEndpoints.Map($"{_fhirBase}/Immunization?", _ => CdsTestHttpEndpoints.Json(prefetch["immunizations"]!.ToJsonString()));
        CdsTestHttpEndpoints.Map($"{_fhirBase}/Procedure?", _ => CdsTestHttpEndpoints.Json(prefetch["procedures"]!.ToJsonString()));
    }

    private async Task<ServiceInfo> CreateServiceAsync(string handlerKey, string hook, object? options = null, Guid? allowedIssuerId = null)
    {
        var body = JsonSerializer.SerializeToNode(new
        {
            ServiceId = $"it-epic-{handlerKey}-{Guid.NewGuid():N}",
            Title = $"IT Epic {handlerKey}",
            HandlerKey = handlerKey,
            Hook = hook,
            IsEnabled = true,
            CardSourceLabel = "CDS Epic integration test",
            AllowedIssuerIds = allowedIssuerId is { } id ? new[] { id } : null,
        })!.AsObject();
        if (options is not null)
        {
            foreach (var (key, value) in JsonSerializer.SerializeToNode(options)!.AsObject())
            {
                body[key] = value?.DeepClone();
            }
        }

        var resp = await _fixture.AdminClient.PostAsync(
            "/api/v1/cds/services", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        await ApiFixture.EnsureOkAsync(resp);
        var created = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var serviceGuid = created.GetProperty("id").GetGuid();

        var infoResp = await _fixture.AdminClient.GetAsync("/api/v1/cds/discovery-info");
        await ApiFixture.EnsureOkAsync(infoResp);
        var entry = JsonDocument.Parse(await infoResp.Content.ReadAsStringAsync()).RootElement.GetProperty("services")
            .EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == serviceGuid);
        return new ServiceInfo(
            serviceGuid,
            entry.GetProperty("serviceId").GetString()!,
            entry.GetProperty("serviceUrl").GetString()!,
            entry.GetProperty("feedbackUrl").GetString()!);
    }

    /// <summary>An Epic-shaped CDS client JWT: RS384, header kid + jku + typ JWT; iss, sub, aud, jti, iat, nbf, exp.</summary>
    private string Token(string audience, string? subject = null, string? jku = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _fhirBase,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now.AddMinutes(-5),
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
                [JwtRegisteredClaimNames.Sub] = subject ?? _clientId,
            },
            AdditionalHeaderClaims = new Dictionary<string, object> { ["jku"] = jku ?? JwksUrl },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_signingKey) { KeyId = _kid }, SecurityAlgorithms.RsaSha384),
        });
    }

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
