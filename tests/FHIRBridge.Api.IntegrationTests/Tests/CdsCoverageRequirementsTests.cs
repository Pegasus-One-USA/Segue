using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Api.IntegrationTests.TestHelpers;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// Coverage requirements (Da Vinci CRD) end to end through the API host: an Epic-shaped call to a
/// coverage-requirements service answered from local payer rules (systemActions carrying the CRD
/// coverage-information extension), appointment-book, the relay to an in-test fake payer CRD service signed with
/// Segue's key, Segue's public JWKS, and the payer / rule admin endpoints with their permissions.
/// </summary>
[Collection("ApiTests")]
public sealed class CdsCoverageRequirementsTests : IDisposable
{
    private const string Cpt = "http://www.ama-assn.org/go/cpt";
    private const string ExtensionUrl = "http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information";

    private readonly ApiFixture _fixture;
    private readonly RSA _signingKey = RSA.Create(2048);
    private readonly string _kid = Guid.NewGuid().ToString("N");
    private readonly string _fhirBase = $"https://ehr-{Guid.NewGuid():N}.example.org/api/FHIR/R4";
    private readonly string _clientId = $"epic-cds-client-{Guid.NewGuid():N}";

    /// <summary>Unique per test so payers created by other tests on the shared host never match.</summary>
    private readonly string _payerIdentifier = $"PAYER-{Guid.NewGuid():N}";

    public CdsCoverageRequirementsTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose() => _signingKey.Dispose();

    private string JwksUrl => _fhirBase.Replace("/api/FHIR/R4", "/jwks.json", StringComparison.Ordinal);

    // ---- Live calls ----

    [Fact]
    public async Task An_epic_order_sign_gets_a_system_action_with_coverage_information_and_activity_records_codes_only()
    {
        await TrustEpicAsync();
        var payer = await CreatePayerAsync();
        await CreateRuleAsync(payer, "70553", pa: "auth-needed", doc: "clinical");
        var service = await CreateServiceAsync("order-sign");
        var body = await SampleRequestAsync(service);
        UsePayer(body);
        const string patientId = "crd-it-patient-5521";
        body["context"]!["patientId"] = patientId;

        var resp = await InvokeAsync(service, body);

        var json = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {json}");
        var answer = JsonNode.Parse(json)!;
        var action = Assert.Single(answer["systemActions"]!.AsArray())!;
        Assert.Equal("update", action["type"]!.GetValue<string>());
        Assert.Equal("ServiceRequest", action["resource"]!["resourceType"]!.GetValue<string>());
        var extension = action["resource"]!["extension"]!.AsArray().Single(e => e!["url"]!.GetValue<string>() == ExtensionUrl)!;
        var parts = extension["extension"]!.AsArray().ToDictionary(p => p!["url"]!.GetValue<string>(), p => p!);
        Assert.Equal("Coverage/sample-coverage-1", parts["coverage"]["valueReference"]!["reference"]!.GetValue<string>());
        Assert.Equal("covered", parts["covered"]["valueCode"]!.GetValue<string>());
        Assert.Equal("auth-needed", parts["pa-needed"]["valueCode"]!.GetValue<string>());
        Assert.Equal("clinical", parts["doc-needed"]["valueCode"]!.GetValue<string>());
        Assert.True(Guid.TryParse(parts["coverage-assertion-id"]["valueString"]!.GetValue<string>(), out _));
        var card = Assert.Single(answer["cards"]!.AsArray())!;
        Assert.Equal("CoverageInformation", card["source"]!["topic"]!["code"]!.GetValue<string>());
        Assert.True(Guid.TryParse(card["uuid"]!.GetValue<string>(), out _));

        var activity = await ActivityAsync(service.ServiceId);
        var row = Assert.Single(JsonNode.Parse(activity)!["items"]!.AsArray())!;
        Assert.Equal(payer.Name, row["payerName"]!.GetValue<string>());
        Assert.Equal("covered/auth-needed/clinical", Assert.Single(row["coverageDecisions"]!.AsArray())!.GetValue<string>());
        Assert.Equal(1, row["systemActionCount"]!.GetValue<int>());
        Assert.Equal(1, row["coverageAssertionCount"]!.GetValue<int>());
        Assert.False(row["relayUsed"]!.GetValue<bool>());
        Assert.DoesNotContain(patientId, activity);
        Assert.DoesNotContain("draft-mri-brain", activity);
    }

    [Fact]
    public async Task Appointment_book_annotates_the_appointment()
    {
        await TrustEpicAsync();
        var payer = await CreatePayerAsync();
        await CreateRuleAsync(payer, "97110", pa: "no-auth");
        var service = await CreateServiceAsync("appointment-book");
        var body = await SampleRequestAsync(service);
        UsePayer(body);

        var resp = await InvokeAsync(service, body);

        var json = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {json}");
        var action = Assert.Single(JsonNode.Parse(json)!["systemActions"]!.AsArray())!;
        Assert.Equal("Appointment", action["resource"]!["resourceType"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_relay_payer_gets_a_call_signed_by_segue_without_the_ehr_token_and_its_answer_comes_back_via_segue()
    {
        await TrustEpicAsync();
        var relayUrl = $"https://payer-{Guid.NewGuid():N}.example.org/cds-services/crd-order-sign";
        var payer = await CreatePayerAsync(relayUrl);
        CdsTestHttpEndpoints.Map(relayUrl, request =>
        {
            var inbound = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            var order = inbound["context"]!["draftOrders"]!["entry"]![0]!["resource"]!.DeepClone().AsObject();
            order["extension"] = new JsonArray(new JsonObject
            {
                ["url"] = ExtensionUrl,
                ["extension"] = new JsonArray(
                    new JsonObject { ["url"] = "coverage", ["valueReference"] = new JsonObject { ["reference"] = "Coverage/sample-coverage-1" } },
                    new JsonObject { ["url"] = "covered", ["valueCode"] = "not-covered" },
                    new JsonObject { ["url"] = "date", ["valueDate"] = "2026-10-16" },
                    new JsonObject { ["url"] = "coverage-assertion-id", ["valueString"] = "payer-assertion-1" }),
            });
            var answer = new JsonObject
            {
                ["cards"] = new JsonArray(new JsonObject
                {
                    ["summary"] = "Not covered by the payer",
                    ["indicator"] = "warning",
                    ["source"] = new JsonObject { ["label"] = "Payer's own label" },
                }),
                ["systemActions"] = new JsonArray(new JsonObject { ["type"] = "update", ["description"] = "Coverage", ["resource"] = order }),
            };
            return CdsTestHttpEndpoints.Json(answer.ToJsonString());
        });
        var service = await CreateServiceAsync("order-sign");
        var body = await SampleRequestAsync(service);
        UsePayer(body);
        body["fhirServer"] = _fhirBase;
        body["fhirAuthorization"] = new JsonObject
        {
            ["access_token"] = "ehr-token-never-forwarded",
            ["token_type"] = "Bearer",
            ["scope"] = "patient/*.read",
            ["subject"] = _clientId,
        };

        var resp = await InvokeAsync(service, body);

        var json = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {json}");
        var answerBack = JsonNode.Parse(json)!;
        // The payer's own card, plus Segue's instructions card for the coverage information the payer sent.
        var cards = answerBack["cards"]!.AsArray();
        Assert.Equal(2, cards.Count);
        Assert.Contains(cards, c => c!["summary"]!.GetValue<string>() == "Not covered by the payer");
        Assert.All(cards, c => Assert.Equal($"{payer.Name} via Segue", c!["source"]!["label"]!.GetValue<string>()));
        Assert.Single(answerBack["systemActions"]!.AsArray());

        var sent = Assert.Single(CdsTestHttpEndpoints.SeenUnder(relayUrl));
        Assert.DoesNotContain("ehr-token-never-forwarded", sent.Body);
        Assert.DoesNotContain("fhirAuthorization", sent.Body);
        Assert.StartsWith("Bearer ", sent.Authorization);
        var token = new JsonWebTokenHandler().ReadJsonWebToken(sent.Authorization!["Bearer ".Length..]);
        Assert.Equal("RS384", token.Alg);
        Assert.EndsWith("/cds-services/.well-known/jwks.json", token.GetHeaderValue<string>("jku"));
        Assert.Equal(relayUrl, Assert.Single(token.Audiences));
        Assert.Equal("segue-client-at-payer", token.Subject);

        // The JWKS Segue publishes verifies the relay signature.
        var jwks = await _fixture.AnonClient.GetStringAsync("/cds-services/.well-known/jwks.json");
        var key = new JsonWebKeySet(jwks).Keys.Single(k => k.Kid == token.Kid);
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(sent.Authorization["Bearer ".Length..], new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidAudience = relayUrl,
            IssuerSigningKey = key,
        });
        Assert.True(validation.IsValid);

        var row = Assert.Single(JsonNode.Parse(await ActivityAsync(service.ServiceId))!["items"]!.AsArray())!;
        Assert.True(row["relayUsed"]!.GetValue<bool>());
        Assert.Equal("ok", row["relayOutcome"]!.GetValue<string>());
        Assert.Equal("not-covered/-/-", Assert.Single(row["coverageDecisions"]!.AsArray())!.GetValue<string>());
    }

    [Fact]
    public async Task A_failing_relay_falls_back_to_local_rules()
    {
        await TrustEpicAsync();
        var relayUrl = $"https://payer-{Guid.NewGuid():N}.example.org/cds-services/crd";
        var payer = await CreatePayerAsync(relayUrl);
        await CreateRuleAsync(payer, "70553", pa: "auth-needed");
        CdsTestHttpEndpoints.Map(relayUrl, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var service = await CreateServiceAsync("order-sign");
        var body = await SampleRequestAsync(service);
        UsePayer(body);

        var resp = await InvokeAsync(service, body);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Single(JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["systemActions"]!.AsArray());
        var row = Assert.Single(JsonNode.Parse(await ActivityAsync(service.ServiceId))!["items"]!.AsArray())!;
        Assert.Equal("HTTP 503; local rules used", row["relayOutcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Segues_jwks_is_public_and_holds_only_a_public_rs384_key()
    {
        var resp = await _fixture.AnonClient.GetAsync("/cds-services/.well-known/jwks.json");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        var key = Assert.Single(JsonNode.Parse(json)!["keys"]!.AsArray())!;
        Assert.Equal("RSA", key["kty"]!.GetValue<string>());
        Assert.Equal("RS384", key["alg"]!.GetValue<string>());
        Assert.Null(key["d"]);
        Assert.Null(key["p"]);
    }

    // ---- Admin ----

    [Fact]
    public async Task Hooks_are_validated_per_handler_type()
    {
        var resp = await _fixture.AdminClient.PostAsJsonAsync("/api/v1/cds/services", new
        {
            ServiceId = $"it-pa-{Guid.NewGuid():N}",
            Title = "PA on appointment-book",
            HandlerKey = "prior-auth",
            Hook = "appointment-book",
            IsEnabled = false,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var handlers = JsonNode.Parse(await _fixture.AdminClient.GetStringAsync("/api/v1/cds/handlers"))!.AsArray();
        var crd = handlers.Single(h => h!["key"]!.GetValue<string>() == "coverage-requirements")!;
        Assert.Equal("Coverage requirements (CRD)", crd["displayName"]!.GetValue<string>());
        Assert.Contains(crd["supportedHooks"]!.AsArray(), h => h!.GetValue<string>() == "appointment-book");
        Assert.Contains(crd["settings"]!.AsArray(), s => s!["key"]!.GetValue<string>() == "useSystemActions");
        Assert.False(crd["usesSampleReferenceData"]!.GetValue<bool>());
        Assert.True(handlers.Single(h => h!["key"]!.GetValue<string>() == "renal-dose")!["usesSampleReferenceData"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Payer_and_rule_crud_import_preview_apply_and_export_work_through_the_api()
    {
        var payer = await CreatePayerAsync();
        var csv =
            "system,code,codeMatch,covered,paNeeded,docNeeded,docPurpose,reason,policyUrl,effectiveFrom\r\n" +
            $"{Cpt},70553,exact,covered,auth-needed,clinical,withpa,\"Needs PA, imaging\",https://payer.example.org/policy,2026-01-01\r\n" +
            $"{Cpt},993,prefix,covered,no-auth,,,,,\r\n";

        using var preview = new MultipartFormDataContent { { new StringContent(csv, Encoding.UTF8, "text/csv"), "file", "rules.csv" } };
        var previewResp = await _fixture.AdminClient.PostAsync($"/api/v1/cds/coverage-rules/import?mode=preview&payerId={payer.Id}", preview);
        await ApiFixture.EnsureOkAsync(previewResp);
        var previewResult = JsonNode.Parse(await previewResp.Content.ReadAsStringAsync())!;
        Assert.False(previewResult["applied"]!.GetValue<bool>());
        Assert.Equal(2, previewResult["added"]!.GetValue<int>());
        Assert.Equal(0, (await RulesAsync(payer.Id))["totalCount"]!.GetValue<int>());

        using var apply = new MultipartFormDataContent { { new StringContent(csv, Encoding.UTF8, "text/csv"), "file", "rules.csv" } };
        var applyResp = await _fixture.AdminClient.PostAsync($"/api/v1/cds/coverage-rules/import?mode=apply&payerId={payer.Id}", apply);
        await ApiFixture.EnsureOkAsync(applyResp);
        Assert.True(JsonNode.Parse(await applyResp.Content.ReadAsStringAsync())!["applied"]!.GetValue<bool>());
        var list = await RulesAsync(payer.Id);
        Assert.Equal(2, list["totalCount"]!.GetValue<int>());

        var filtered = JsonNode.Parse(await _fixture.AdminClient.GetStringAsync($"/api/v1/cds/coverage-rules?payerId={payer.Id}&code=705"))!;
        var rule = Assert.Single(filtered["items"]!.AsArray())!;
        Assert.Equal("Needs PA, imaging", rule["reason"]!.GetValue<string>());
        Assert.Equal("2026-01-01", rule["effectiveFrom"]!.GetValue<string>());
        Assert.Equal(payer.Name, rule["payerName"]!.GetValue<string>());

        var ruleId = rule["id"]!.GetValue<Guid>();
        var put = await _fixture.AdminClient.PutAsJsonAsync($"/api/v1/cds/coverage-rules/{ruleId}", new
        {
            PayerId = payer.Id,
            System = Cpt,
            Code = "70553",
            CodeMatch = "exact",
            Covered = "not-covered",
            EffectiveFrom = "2026-01-01",
            IsEnabled = true,
        });
        await ApiFixture.EnsureOkAsync(put);
        Assert.Equal("not-covered", JsonNode.Parse(await put.Content.ReadAsStringAsync())!["covered"]!.GetValue<string>());

        var bad = await _fixture.AdminClient.PutAsJsonAsync($"/api/v1/cds/coverage-rules/{ruleId}", new
        {
            PayerId = payer.Id,
            System = Cpt,
            Code = "70553",
            Covered = "not-covered",
            PaNeeded = "auth-needed",
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var export = await _fixture.AdminClient.GetAsync($"/api/v1/cds/coverage-rules/export?format=csv&payerId={payer.Id}");
        await ApiFixture.EnsureOkAsync(export);
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        var exported = await export.Content.ReadAsStringAsync();
        Assert.StartsWith("payer,system,code,codeMatch,covered,paNeeded,docNeeded,docPurpose,infoNeeded,reason,policyUrl,questionnaire,effectiveFrom,effectiveTo,enabled", exported);
        Assert.Contains(payer.Name, exported);

        var template = await _fixture.AdminClient.GetAsync("/api/v1/cds/coverage-rules/import-template?format=json");
        await ApiFixture.EnsureOkAsync(template);
        Assert.NotNull(JsonNode.Parse(await template.Content.ReadAsStringAsync())!["rules"]);

        var payerDto = JsonNode.Parse(await _fixture.AdminClient.GetStringAsync($"/api/v1/cds/payers/{payer.Id}"))!;
        Assert.Equal(2, payerDto["ruleCount"]!.GetValue<int>());
        Assert.False(payerDto["forwardFhirAuthorization"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.NoContent, (await _fixture.AdminClient.DeleteAsync($"/api/v1/cds/payers/{payer.Id}")).StatusCode);
        Assert.Equal(0, (await RulesAsync(payer.Id))["totalCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Coverage_admin_endpoints_need_the_cdshooks_permissions()
    {
        var payer = await CreatePayerAsync($"https://payer-{Guid.NewGuid():N}.example.org/cds-services/x");
        var (_, _, otherJwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("workflow.view");
        using var other = _fixture.CreateAuthenticatedClient(otherJwt);
        var (_, _, viewJwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("cdshooks.view");
        using var viewer = _fixture.CreateAuthenticatedClient(viewJwt);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.AnonClient.GetAsync("/api/v1/cds/payers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/cds/payers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/cds/coverage-rules")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/cds/payers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/cds/coverage-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/cds/coverage-rules/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/cds/coverage-rules/import-template")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/cds/payers", PayerBody(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/v1/cds/payers/{payer.Id}", PayerBody(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/v1/cds/payers/{payer.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/cds/coverage-rules/import?mode=preview",
            new StringContent("payer,system,code,covered\r\n", Encoding.UTF8, "text/csv"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/v1/cds/payers/{payer.Id}/test-relay", null)).StatusCode);

        // Edit without Delete may preview a replace import but not apply it: applying a replace deletes rules.
        var (_, _, editJwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("cdshooks.view", "cdshooks.edit");
        using var editor = _fixture.CreateAuthenticatedClient(editJwt);
        const string headerOnly = "system,code,covered\r\n";
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsync($"/api/v1/cds/coverage-rules/import?mode=apply&strategy=replace&payerId={payer.Id}",
            new StringContent(headerOnly, Encoding.UTF8, "text/csv"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"/api/v1/cds/coverage-rules/import?mode=preview&strategy=replace&payerId={payer.Id}",
            new StringContent(headerOnly, Encoding.UTF8, "text/csv"))).StatusCode);
    }

    [Fact]
    public async Task Test_relay_sends_a_synthetic_call_and_reports_the_answer()
    {
        var relayUrl = $"https://payer-{Guid.NewGuid():N}.example.org/cds-services/crd";
        var payer = await CreatePayerAsync(relayUrl);
        CdsTestHttpEndpoints.Map(relayUrl, _ => CdsTestHttpEndpoints.Json("""{"cards":[{"summary":"ok","indicator":"info","source":{"label":"p"}}]}"""));
        var (_, _, jwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("cdshooks.view", "cdshooks.test");
        using var tester = _fixture.CreateAuthenticatedClient(jwt);

        var resp = await tester.PostAsync($"/api/v1/cds/payers/{payer.Id}/test-relay", null);

        await ApiFixture.EnsureOkAsync(resp);
        var result = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.True(result["reachable"]!.GetValue<bool>());
        Assert.Equal(1, result["cardCount"]!.GetValue<int>());
        Assert.Equal("ok", result["outcome"]!.GetValue<string>());
        var sent = Assert.Single(CdsTestHttpEndpoints.SeenUnder(relayUrl));
        Assert.Contains("segue-test-patient", sent.Body);
    }

    // ---- Helpers ----

    private sealed record ServiceInfo(Guid Id, string ServiceId, string ServiceUrl);

    private sealed record PayerInfo(Guid Id, string Name);

    private object PayerBody(string? relayUrl) => new
    {
        Name = $"IT Payer {Guid.NewGuid():N}"[..20],
        Identifiers = new[] { _payerIdentifier },
        IsEnabled = true,
        RelayEnabled = relayUrl is not null,
        RelayServiceUrl = relayUrl,
        RelayTimeoutMs = 1000,
        RelayClientId = "segue-client-at-payer",
    };

    private async Task<PayerInfo> CreatePayerAsync(string? relayUrl = null)
    {
        var resp = await _fixture.AdminClient.PostAsJsonAsync("/api/v1/cds/payers", PayerBody(relayUrl));
        await ApiFixture.EnsureOkAsync(resp);
        var payer = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        return new PayerInfo(payer["id"]!.GetValue<Guid>(), payer["name"]!.GetValue<string>());
    }

    private async Task CreateRuleAsync(PayerInfo payer, string code, string? pa = null, string? doc = null)
    {
        var resp = await _fixture.AdminClient.PostAsJsonAsync("/api/v1/cds/coverage-rules", new
        {
            PayerId = payer.Id,
            System = Cpt,
            Code = code,
            CodeMatch = "exact",
            Covered = "covered",
            PaNeeded = pa,
            DocNeeded = doc,
            Reason = "Integration test rule.",
            IsEnabled = true,
        });
        await ApiFixture.EnsureOkAsync(resp);
    }

    private async Task<JsonNode> RulesAsync(Guid payerId) =>
        JsonNode.Parse(await _fixture.AdminClient.GetStringAsync($"/api/v1/cds/coverage-rules?payerId={payerId}"))!;

    /// <summary>Points the sample's Coverage at this test's payer (its Organization identifier).</summary>
    private void UsePayer(JsonObject body)
    {
        var entries = body["prefetch"]!["coverage"]!["entry"]!.AsArray();
        foreach (var entry in entries)
        {
            var resource = entry!["resource"]!.AsObject();
            if (resource["resourceType"]!.GetValue<string>() == "Coverage")
            {
                resource["payor"] = new JsonArray(new JsonObject
                {
                    ["identifier"] = new JsonObject { ["value"] = _payerIdentifier },
                    ["display"] = "Integration payer",
                });
            }
        }
    }

    private async Task<ServiceInfo> CreateServiceAsync(string hook)
    {
        var resp = await _fixture.AdminClient.PostAsJsonAsync("/api/v1/cds/services", new
        {
            ServiceId = $"it-crd-{Guid.NewGuid():N}",
            Title = "IT coverage requirements",
            HandlerKey = "coverage-requirements",
            Hook = hook,
            IsEnabled = true,
            CardSourceLabel = "Segue CRD",
        });
        await ApiFixture.EnsureOkAsync(resp);
        var created = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        var id = created["id"]!.GetValue<Guid>();
        var info = JsonNode.Parse(await _fixture.AdminClient.GetStringAsync("/api/v1/cds/discovery-info"))!["services"]!.AsArray()
            .Single(s => s!["id"]!.GetValue<Guid>() == id)!;
        return new ServiceInfo(id, info["serviceId"]!.GetValue<string>(), info["serviceUrl"]!.GetValue<string>());
    }

    private async Task TrustEpicAsync()
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
            Name = "Integration Epic (CRD)",
            Vendor = "Epic",
            Issuer = _fhirBase,
            JwksUrl,
            IsEnabled = true,
            ExpectedSubject = _clientId,
            AllowCallback = true,
        });
        await ApiFixture.EnsureOkAsync(resp);
    }

    private string Token(string audience)
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
                [JwtRegisteredClaimNames.Sub] = _clientId,
            },
            AdditionalHeaderClaims = new Dictionary<string, object> { ["jku"] = JwksUrl },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_signingKey) { KeyId = _kid }, SecurityAlgorithms.RsaSha384),
        });
    }

    private async Task<JsonObject> SampleRequestAsync(ServiceInfo service) =>
        JsonNode.Parse(await _fixture.AdminClient.GetStringAsync($"/api/v1/cds/services/{service.Id}/sample-request"))!.AsObject();

    private async Task<HttpResponseMessage> InvokeAsync(ServiceInfo service, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/cds-services/{service.ServiceId}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(service.ServiceUrl));
        return await _fixture.AnonClient.SendAsync(request);
    }

    private async Task<string> ActivityAsync(string serviceId) =>
        await _fixture.AdminClient.GetStringAsync($"/api/v1/cds/activity?serviceId={Uri.EscapeDataString(serviceId)}");
}
