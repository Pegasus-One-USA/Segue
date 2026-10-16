using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Application.Services.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Cds;
using FHIRBridge.Infrastructure.Cds.Coverage;
using FHIRBridge.Infrastructure.Cds.Handlers;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Security;
using FHIRBridge.SharedKernel.Observability;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// Coverage requirements, the outside-facing parts: the payer relay client (Segue's signature, SSRF guard, timeout,
/// response cap), Segue's signing key and JWKS, the rule file import (CSV / JSON, preview writes nothing, apply,
/// replace, export round trip), and the call log of a CRD call holding no patient data.
/// </summary>
public sealed class CdsCoverageRelayAndImportTests
{
    private const string SegueBase = "https://segue.example.org";
    private const string RelayUrl = "https://crd.payer.example/cds-services/crd-order-sign";

    // ---- Signing key and relay client ----

    private static ServiceProvider KeyServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<ISystemSettingRepository, InMemorySystemSettingRepository>();
        services.AddSingleton<InMemorySecretStore>();
        services.AddSingleton<ISecretWriter>(sp => sp.GetRequiredService<InMemorySecretStore>());
        services.AddSingleton<ISecretProvider>(sp => sp.GetRequiredService<InMemorySecretStore>());
        var vaults = new Mock<ITenantSecretVaultResolver>();
        vaults.Setup(v => v.ResolveVaultName(It.IsAny<string>())).Returns((string name) => name);
        services.AddSingleton(vaults.Object);
        return services.BuildServiceProvider();
    }

    private static CdsRelaySigningKeyProvider KeyProvider(ServiceProvider services) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CdsRelaySigningKeyProvider>.Instance);

    private static ICdsPublicBaseUrlResolver BaseUrl(bool configured = true)
    {
        var resolver = new Mock<ICdsPublicBaseUrlResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CdsPublicBaseUrl(configured ? SegueBase : "https://admin.example.org", configured));
        return resolver.Object;
    }

    private static CdsPayerRelayClient Client(HttpMessageHandler handler, CdsRelaySigningKeyProvider keys, bool configured = true) =>
        new(new HttpClient(handler), keys, BaseUrl(configured), CdsTestSupport.Options());

    private static CdsPayer Payer(string url = RelayUrl, string? clientId = "segue-at-payer") =>
        new("Acme", ["Acme Health"], null, true, relayEnabled: true, relayServiceUrl: url, relayClientId: clientId);

    private static CdsRequest Request() => CdsPayerRelayTestService.SyntheticRequest("Acme Health");

    [Fact]
    public async Task A_relayed_call_is_signed_rs384_with_segues_jku_iss_aud_sub_jti_and_a_short_exp()
    {
        using var services = KeyServices();
        var keys = KeyProvider(services);
        string? authorization = null;
        string? body = null;
        var handler = new CdsFakeHttpHandler(request =>
        {
            authorization = request.Headers.Authorization?.Parameter;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CdsFakeHttpHandler.Json(new JsonObject { ["cards"] = new JsonArray(new JsonObject { ["summary"] = "Payer says hi", ["indicator"] = "info", ["source"] = new JsonObject { ["label"] = "Payer" } }) });
        });

        var result = await Client(handler, keys).SendAsync(Payer(), Request(), TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Response!.Cards.Should().ContainSingle();
        handler.Seen.Should().ContainSingle().Which.Untraced.Should().BeTrue("relay bodies hold PHI and must not be traced");
        var token = new JsonWebTokenHandler().ReadJsonWebToken(authorization);
        token.Alg.Should().Be("RS384");
        token.GetHeaderValue<string>("jku").Should().Be($"{SegueBase}/cds-services/.well-known/jwks.json");
        token.Typ.Should().Be("JWT");
        token.Issuer.Should().Be(SegueBase);
        token.Audiences.Should().Equal(RelayUrl);
        token.Subject.Should().Be("segue-at-payer");
        token.Id.Should().NotBeNullOrEmpty();
        (token.ValidTo - token.IssuedAt).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));

        var jwks = await keys.GetJwksAsync(CancellationToken.None);
        var jwk = jwks.Keys.Should().ContainSingle().Subject;
        token.Kid.Should().Be(jwk.KeyId);
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(authorization, new TokenValidationParameters
        {
            ValidIssuer = SegueBase,
            ValidAudience = RelayUrl,
            IssuerSigningKey = new JsonWebKey(JsonSerializer.Serialize(jwks.Keys[0])),
        });
        validation.IsValid.Should().BeTrue("the JWKS Segue publishes verifies its relay signature");
        JsonNode.Parse(body!)!["hook"]!.GetValue<string>().Should().Be("order-sign");
    }

    [Fact]
    public async Task The_signing_key_is_created_once_stored_as_a_secret_reference_and_reused()
    {
        using var services = KeyServices();
        var first = await KeyProvider(services).GetJwksAsync(CancellationToken.None);
        var second = await KeyProvider(services).GetJwksAsync(CancellationToken.None);

        second.Keys[0].KeyId.Should().Be(first.Keys[0].KeyId);
        second.Keys[0].Modulus.Should().Be(first.Keys[0].Modulus);
        var setting = await services.GetRequiredService<ISystemSettingRepository>().GetByKeyAsync(CdsRelaySigningKeyProvider.SettingKey, CancellationToken.None);
        setting!.Value.Should().NotContain("PRIVATE KEY", "only a reference to the key is kept in settings");
        JsonSerializer.Serialize(first).Should().NotContain("\"d\"", "the JWKS holds the public key only");
    }

    [Fact]
    public async Task Two_instances_creating_the_key_at_once_end_up_signing_with_the_same_key()
    {
        using var services = KeyServices();
        var instances = Enumerable.Range(0, 4).Select(_ => KeyProvider(services)).ToArray();

        var keys = await Task.WhenAll(instances.Select(provider => Task.Run(() => provider.GetSigningKeyAsync(CancellationToken.None))));

        keys.Select(key => key.KeyId).Distinct().Should().ContainSingle("every instance adopts the one stored key reference");
        var stored = await services.GetRequiredService<ISystemSettingRepository>().GetByKeyAsync(CdsRelaySigningKeyProvider.SettingKey, CancellationToken.None);
        stored!.Value.Should().Contain(keys[0].KeyId!);
        foreach (var provider in instances)
        {
            (await provider.GetJwksAsync(CancellationToken.None)).Keys.Single().KeyId.Should().Be(keys[0].KeyId);
        }
    }

    [Fact]
    public async Task A_private_address_is_refused_by_the_ssrf_guard()
    {
        using var services = KeyServices();
        var guarded = CdsJwksHttpHandler.CreateGuarded(() => false);

        var result = await Client(guarded, KeyProvider(services))
            .SendAsync(Payer("https://127.0.0.1/cds-services/x"), Request(), TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Outcome.Should().Be("unreachable");
        guarded.AllowAutoRedirect.Should().BeFalse();
        guarded.UseProxy.Should().BeFalse();
    }

    [Fact]
    public async Task A_slow_payer_times_out_within_the_given_budget()
    {
        using var services = KeyServices();
        var handler = new CdsFakeHttpHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Client(handler, KeyProvider(services)).SendAsync(Payer(), Request(), TimeSpan.FromMilliseconds(200), CancellationToken.None);

        result.Outcome.Should().Be("timed out");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(500, "{}", "HTTP 500")]
    [InlineData(200, "not json", "invalid response")]
    [InlineData(200, "{\"nocards\":true}", "invalid response")]
    public async Task Failed_answers_are_reported_with_short_reasons(int status, string json, string outcome)
    {
        using var services = KeyServices();
        var handler = new CdsFakeHttpHandler(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json) });

        var result = await Client(handler, KeyProvider(services)).SendAsync(Payer(), Request(), TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Outcome.Should().Be(outcome);
    }

    [Fact]
    public async Task A_response_over_the_cap_is_refused_and_no_public_base_url_means_no_relay()
    {
        using var services = KeyServices();
        var big = new CdsFakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"cards\":[],\"pad\":\"" + new string('x', 2 * 1024 * 1024) + "\"}"),
        });
        (await Client(big, KeyProvider(services)).SendAsync(Payer(), Request(), TimeSpan.FromSeconds(5), CancellationToken.None))
            .Outcome.Should().Be("response too large");

        var any = new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(new JsonObject { ["cards"] = new JsonArray() }));
        (await Client(any, KeyProvider(services), configured: false).SendAsync(Payer(), Request(), TimeSpan.FromSeconds(5), CancellationToken.None))
            .Outcome.Should().Be("Cds:PublicBaseUrl is not set");
        any.Seen.Should().BeEmpty();
    }

    // ---- Import / export ----

    private sealed class NoCache : ICdsCoverageRuleCache
    {
        public int Invalidations { get; private set; }

        public Task<CdsCoverageRuleSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(CdsCoverageRuleSnapshot.Empty);

        public void Invalidate() => Invalidations++;
    }

    private readonly InMemoryCdsPayerRepository _payers = new();
    private readonly InMemoryCdsCoverageRuleRepository _rules = new();
    private readonly NoCache _cache = new();
    private readonly InMemoryCdsServiceInstanceRepository _instances = new();

    private CdsCoverageRuleService RuleService()
    {
        var names = new Mock<IUserDisplayNameResolver>();
        names.Setup(n => n.ResolveAsync(It.IsAny<IEnumerable<string?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        return new CdsCoverageRuleService(_rules, _payers, _cache, names.Object, _instances);
    }

    private async Task<CdsPayer> AddPayerAsync(string name = "Acme Health")
    {
        var payer = new CdsPayer(name, [name], null, true);
        await _payers.AddAsync(payer, CancellationToken.None);
        return payer;
    }

    private const string Csv =
        "payer,system,code,codeMatch,covered,paNeeded,docNeeded,docPurpose,infoNeeded,reason,policyUrl,questionnaire,effectiveFrom,effectiveTo,enabled\r\n" +
        "Acme Health,http://www.ama-assn.org/go/cpt,70553,exact,covered,auth-needed,clinical,withpa;withclaim,,\"MRI, with contrast, needs PA\",https://acme.example/p,,2026-01-01,,true\r\n" +
        "Acme Health,http://www.ama-assn.org/go/cpt,9921,prefix,covered,no-auth,,,,,,,,,\r\n";

    [Fact]
    public async Task A_csv_preview_checks_every_row_and_writes_nothing()
    {
        await AddPayerAsync();

        var result = await RuleService().ImportAsync("csv", "preview", "merge", null, Csv, CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.TotalRows.Should().Be(2);
        result.Added.Should().Be(2);
        result.ErrorCount.Should().Be(0);
        result.Rows.Select(r => r.Action).Should().Equal("add", "add");
        (await _rules.GetAllAsync(CancellationToken.None)).Should().BeEmpty("a preview writes nothing");
        _cache.Invalidations.Should().Be(0);
    }

    [Fact]
    public async Task Apply_adds_then_reimport_is_unchanged_then_an_edit_is_an_update_and_replace_deletes_the_rest()
    {
        await AddPayerAsync();
        var service = RuleService();

        (await service.ImportAsync("csv", "apply", "merge", null, Csv, CancellationToken.None)).Applied.Should().BeTrue();
        var rules = await _rules.GetAllAsync(CancellationToken.None);
        rules.Should().HaveCount(2);
        var mri = rules.Single(r => r.Code == "70553");
        mri.Reason.Should().Be("MRI, with contrast, needs PA");
        mri.DocPurposes.Should().Equal("withpa", "withclaim");
        mri.EffectiveFrom.Should().Be(new DateOnly(2026, 1, 1));
        rules.Single(r => r.Code == "9921").CodePrefixMatch.Should().BeTrue();

        (await service.ImportAsync("csv", "preview", "merge", null, Csv, CancellationToken.None)).Unchanged.Should().Be(2);

        var edited = Csv.Replace("auth-needed,clinical", "no-auth,clinical", StringComparison.Ordinal);
        var preview = await service.ImportAsync("csv", "preview", "merge", null, edited, CancellationToken.None);
        preview.Updated.Should().Be(1);
        mri.PaNeeded.Should().Be("auth-needed", "a preview does not change the stored rule");

        var onlyMri = string.Join("\r\n", Csv.Split("\r\n").Take(2)) + "\r\n";
        var replace = await service.ImportAsync("csv", "apply", "replace", null, onlyMri, CancellationToken.None);
        replace.Deleted.Should().Be(1);
        (await _rules.GetAllAsync(CancellationToken.None)).Should().ContainSingle().Which.Code.Should().Be("70553");
    }

    [Fact]
    public async Task An_oid_and_its_url_are_one_code_system_for_storage_import_and_uniqueness()
    {
        var payer = await AddPayerAsync();
        var service = RuleService();
        var csv =
            "payer,system,code,covered,effectiveFrom\r\n" +
            "Acme Health,urn:oid:2.16.840.1.113883.6.12,70553,covered,2026-01-01\r\n" +
            "Acme Health,http://www.ama-assn.org/go/cpt,70553,not-covered,2026-01-01\r\n";

        var preview = await service.ImportAsync("csv", "preview", "merge", null, csv, CancellationToken.None);
        preview.Rows.Single(r => r.RowNumber == 3).Errors.Should().Contain(e => e.Contains("more than once", StringComparison.Ordinal));

        var first = string.Join("\r\n", csv.Split("\r\n").Take(2)) + "\r\n";
        (await service.ImportAsync("csv", "apply", "merge", null, first, CancellationToken.None)).Applied.Should().BeTrue();
        (await _rules.GetAllAsync(CancellationToken.None)).Single().System.Should().Be("http://www.ama-assn.org/go/cpt", "the canonical form is stored");

        await service.Invoking(s => s.CreateAsync(
                new SaveCdsCoverageRuleRequest(payer.Id, "http://www.ama-assn.org/go/cpt/", "70553", "exact", "covered", null, null, null, null, null, null, null,
                    new DateOnly(2026, 1, 1), null, true),
                CancellationToken.None))
            .Should().ThrowAsync<FHIRBridge.SharedKernel.Exceptions.BusinessRuleException>().WithMessage("*already has a rule*");
    }

    [Fact]
    public async Task Contract_window_and_oth_are_accepted_codes()
    {
        await AddPayerAsync();
        var csv =
            "payer,system,code,covered,docNeeded,docPurpose,infoNeeded\r\n" +
            "Acme Health,http://www.ama-assn.org/go/cpt,70553,conditional,clinical,oth,contract-window;OTH\r\n";

        var result = await RuleService().ImportAsync("csv", "apply", "merge", null, csv, CancellationToken.None);

        result.ErrorCount.Should().Be(0);
        var rule = (await _rules.GetAllAsync(CancellationToken.None)).Single();
        rule.InfoNeeded.Should().Equal("contract-window", "OTH");
        rule.DocPurposes.Should().Equal("OTH");
    }

    [Fact]
    public async Task A_rule_change_clears_the_sign_off_of_coverage_requirements_services()
    {
        await AddPayerAsync();
        var crd = new CdsServiceInstance("crd-order-sign", "CRD", CdsHandlerKeys.CoverageRequirements, CdsHookNames.OrderSign, "CRD.", true, null, "Segue", null, null);
        crd.SignOff("Dr A", null, DateTime.UtcNow);
        var other = new CdsServiceInstance("allergy", "Allergy", "drug-allergy", CdsHookNames.OrderSign, "Allergy.", true, null, "Segue", null, null);
        other.SignOff("Dr B", null, DateTime.UtcNow);
        await _instances.AddAsync(crd, CancellationToken.None);
        await _instances.AddAsync(other, CancellationToken.None);

        await RuleService().ImportAsync("csv", "preview", "merge", null, Csv, CancellationToken.None);
        crd.IsClinicallySignedOff.Should().BeTrue("a preview changes nothing");

        await RuleService().ImportAsync("csv", "apply", "merge", null, Csv, CancellationToken.None);

        crd.IsClinicallySignedOff.Should().BeFalse("the payer rules the clinician reviewed changed");
        other.IsClinicallySignedOff.Should().BeTrue("other checks do not use payer rules");
    }

    [Fact]
    public async Task Bad_rows_are_reported_per_row_and_apply_writes_nothing()
    {
        await AddPayerAsync();
        var csv =
            "payer,system,code,covered,paNeeded,effectiveFrom,infoNeeded\r\n" +
            "Acme Health,http://www.ama-assn.org/go/cpt,70553,covered,auth-needed,,\r\n" +
            "Nobody,http://www.ama-assn.org/go/cpt,70554,covered,,,\r\n" +
            "Acme Health,http://www.ama-assn.org/go/cpt,70555,maybe,,,\r\n" +
            "Acme Health,http://www.ama-assn.org/go/cpt,70556,covered,,16/10/2026,\r\n" +
            "Acme Health,http://www.ama-assn.org/go/cpt,70557,not-covered,auth-needed,,\r\n" +
            "Acme Health,http://www.ama-assn.org/go/cpt,70558,covered,no-auth,,performer\r\n" +
            "Acme Health,http://www.ama-assn.org/go/cpt,70553,covered,,,\r\n";

        var result = await RuleService().ImportAsync("csv", "apply", "merge", null, csv, CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.ErrorCount.Should().Be(6);
        result.Rows.Where(r => r.Action == "error").Select(r => r.RowNumber).Should().Equal(3, 4, 5, 6, 7, 8);
        result.Rows.Single(r => r.RowNumber == 3).Errors.Should().Contain(e => e.Contains("No payer", StringComparison.Ordinal));
        result.Rows.Single(r => r.RowNumber == 5).Errors.Should().Contain(e => e.Contains("yyyy-MM-dd", StringComparison.Ordinal));
        result.Rows.Single(r => r.RowNumber == 8).Errors.Should().Contain(e => e.Contains("more than once", StringComparison.Ordinal));
        (await _rules.GetAllAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_required_column_is_a_file_error()
    {
        await AddPayerAsync();

        var result = await RuleService().ImportAsync("csv", "preview", "merge", null, "payer,system,code\r\nAcme Health,x,y\r\n", CancellationToken.None);

        result.FileErrors.Should().Contain(e => e.Contains("covered", StringComparison.Ordinal));
        result.TotalRows.Should().Be(0);
    }

    [Fact]
    public async Task Json_import_with_a_fixed_payer_and_export_round_trips_with_formula_guarding()
    {
        var payer = await AddPayerAsync();
        var json = """
            {"rules":[
              {"system":"http://www.ama-assn.org/go/cpt","code":"97110","covered":"conditional","paNeeded":"conditional",
               "infoNeeded":["performer","location"],"reason":"=HYPERLINK(\"http://evil\")"},
              {"system":"http://www.ama-assn.org/go/cpt","code":"E0601","covered":"covered","docNeeded":"admin","docPurpose":"withclaim",
               "questionnaire":"http://acme.example/Questionnaire/cpap|2","effectiveTo":"2027-12-31","enabled":false}
            ]}
            """;
        var service = RuleService();

        var imported = await service.ImportAsync("json", "apply", "merge", payer.Id, json, CancellationToken.None);
        imported.Applied.Should().BeTrue(string.Join(" ", imported.Rows.SelectMany(r => r.Errors)));
        imported.Added.Should().Be(2);

        var csv = await service.ExportAsync("csv", payer.Id, CancellationToken.None);
        csv.ContentType.Should().Be("text/csv");
        csv.Content.Should().Contain("\"'=HYPERLINK(\"\"http://evil\"\")\"", "a formula-looking cell is prefixed with an apostrophe");
        var again = await service.ImportAsync("csv", "preview", "merge", null, csv.Content, CancellationToken.None);
        again.Unchanged.Should().Be(2, string.Join(" ", again.Rows.SelectMany(r => r.Errors)));

        var exportedJson = await service.ExportAsync("json", null, CancellationToken.None);
        (await service.ImportAsync("json", "preview", "merge", null, exportedJson.Content, CancellationToken.None)).Unchanged.Should().Be(2);

        var template = service.GetTemplate("csv");
        template.Content.Split("\r\n")[0].Should().Be(string.Join(",", CdsCoverageFileFormats.CsvColumns));
    }

    // ---- The call log of a CRD call ----

    [Fact]
    public async Task A_crd_call_log_row_holds_payer_and_decision_codes_but_no_patient_data()
    {
        using var key = CdsTestSigningKey.Rsa();
        using var jwksCache = new MemoryCache(new MemoryCacheOptions());
        var issuers = new InMemoryCdsTrustedIssuerRepository();
        var instances = new InMemoryCdsServiceInstanceRepository();
        var callLog = new InMemoryCdsCallLogRepository();
        const string issuerUrl = "https://ehr.example.org/api/FHIR/R4";
        await issuers.AddAsync(new CdsTrustedIssuer("EHR", "Epic", issuerUrl, null, key.Jwks, null, true), CancellationToken.None);
        var handler = new CoverageRequirementsCdsHandler();
        await instances.AddAsync(
            new CdsServiceInstance("crd-order-sign", "CRD", handler.Key, CdsHookNames.OrderSign, "CRD.", true, null, "Segue", null, null),
            CancellationToken.None);

        var payer = new CdsPayer("Example Health Plan", ["EXAMPLE-PAYER"], null, true);
        var rule = new CdsCoverageRule(payer.Id, new FHIRBridge.Domain.Entities.CdsCoverageRuleValues(
            "http://www.ama-assn.org/go/cpt", "70553", false, "covered", "auth-needed", "clinical", null, null,
            "Secret reason text", null, null, null, null, true));
        var engine = new CdsCoverageEngine(new SnapshotCache(new CdsCoverageRuleSnapshot([payer], [rule])),
            new Mock<ICdsPayerRelayClient>(MockBehavior.Strict).Object, NullLogger<CdsCoverageEngine>.Instance);

        var settings = new Mock<ISystemSettingsCache>();
        settings.Setup(s => s.GetIntAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, int fallback, CancellationToken _) => Task.FromResult(fallback));
        settings.Setup(s => s.GetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string fallback, CancellationToken _) => Task.FromResult(fallback));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [CdsPublicBaseUrl.ConfigurationKey] = SegueBase })
            .Build();
        var options = CdsTestSupport.Options();
        var replay = new CdsReplayGuard(new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));
        var service = new CdsHooksService(
            instances,
            new CdsServiceHandlerRegistry([handler]),
            callLog,
            new CdsPublicBaseUrlResolver(settings.Object, configuration),
            new CdsJwtValidator(issuers, new CdsJwksProvider(new Mock<IHttpClientFactory>().Object, jwksCache, options, NullLogger<CdsJwksProvider>.Instance), replay, options),
            new CdsEvaluator(settings.Object, options, NullLogger<CdsEvaluator>.Instance, null, [engine]),
            new Mock<IGovernanceLogger>().Object,
            new Mock<ICdsMetrics>().Object,
            NullLogger<CdsHooksService>.Instance);

        var request = handler.BuildSampleRequest(CdsHookNames.OrderSign);
        const string patientId = "patient-crd-9f2c";
        request.Context!["patientId"] = patientId;
        var token = key.CreateToken(issuerUrl, $"{SegueBase}/cds-services/crd-order-sign");

        var result = await service.InvokeAsync("crd-order-sign", request, new CdsCallerContext($"Bearer {token}", SegueBase), CancellationToken.None);

        result.Status.Should().Be(CdsCallStatus.Ok);
        result.Response!.SystemActions.Should().ContainSingle();
        var row = (await callLog.GetPagedAsync(new CdsCallLogFilter(), 1, 10, CancellationToken.None)).Items.Should().ContainSingle().Subject;
        row.Outcome.Should().Be(CdsCallOutcome.Success);
        row.PayerId.Should().Be(payer.Id);
        row.PayerName.Should().Be("Example Health Plan");
        row.CoverageDecisions.Should().Be("covered/auth-needed/clinical");
        row.RelayUsed.Should().BeFalse();
        row.SystemActionCount.Should().Be(1);
        row.CoverageAssertionCount.Should().Be(1);
        var stored = JsonSerializer.Serialize(row);
        stored.Should().NotContain(patientId).And.NotContain("draft-mri-brain").And.NotContain("sample-coverage-1")
            .And.NotContain("Secret reason text").And.NotContain("MRI brain");
    }

    private sealed class SnapshotCache : ICdsCoverageRuleCache
    {
        private readonly CdsCoverageRuleSnapshot _snapshot;

        public SnapshotCache(CdsCoverageRuleSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<CdsCoverageRuleSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_snapshot);

        public void Invalidate()
        {
        }
    }
}
