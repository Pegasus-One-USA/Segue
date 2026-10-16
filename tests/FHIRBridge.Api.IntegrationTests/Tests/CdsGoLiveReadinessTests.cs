using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// Phase 3 (Epic go-live readiness): GET api/v1/cds/readiness and the clinical sign-off endpoints
/// (POST / DELETE api/v1/cds/services/{id}/sign-off), end to end with their permissions.
/// </summary>
[Collection("ApiTests")]
public sealed class CdsGoLiveReadinessTests
{
    private const string ReadinessUrl = "/api/v1/cds/readiness";

    private readonly ApiFixture _fixture;

    /// <summary>Unique per test so the shared in-memory host never sees two tests' services collide.</summary>
    private readonly string _serviceId = $"it-golive-{Guid.NewGuid():N}";

    public CdsGoLiveReadinessTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    // ---- Readiness ----

    [Fact]
    public async Task Readiness_needs_a_session_and_cdshooks_view()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.AnonClient.GetAsync(ReadinessUrl)).StatusCode);

        var (_, _, jwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("workflow.view");
        using var other = _fixture.CreateAuthenticatedClient(jwt);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(ReadinessUrl)).StatusCode);
    }

    [Fact]
    public async Task A_viewer_gets_every_check_with_a_status_and_no_secrets()
    {
        // A trusted issuer with pasted keys only: the key material must never come back in the report.
        var issuer = await _fixture.AdminClient.PostAsJsonAsync("/api/v1/cds/trusted-issuers", new
        {
            Name = "Readiness test EHR",
            Vendor = "Other",
            Issuer = $"https://ehr-{Guid.NewGuid():N}.example.org",
            JwksJson = """{"keys":[{"kty":"EC","crv":"P-384","kid":"k1","x":"readiness-secret-x","y":"readiness-secret-y"}]}""",
            IsEnabled = true,
        });
        await ApiFixture.EnsureOkAsync(issuer);
        await CreateServiceAsync();

        var (_, _, jwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("cdshooks.view");
        using var viewer = _fixture.CreateAuthenticatedClient(jwt);
        var resp = await viewer.GetAsync(ReadinessUrl);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;
        var checks = root.GetProperty("checks").EnumerateArray().ToArray();
        string[] statuses = ["ok", "warning", "blocker"];
        Assert.Contains(root.GetProperty("status").GetString(), statuses);
        Assert.All(checks, check =>
        {
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("key").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("label").GetString()));
            Assert.Contains(check.GetProperty("status").GetString(), statuses);
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("detail").GetString()));
        });

        var keys = checks.Select(check => check.GetProperty("key").GetString()).ToArray();
        Assert.Contains("public-base-url", keys);
        Assert.Contains("replay-protection", keys);
        Assert.Contains("trusted-issuer", keys);
        Assert.Contains("issuer-jwks-url", keys);
        Assert.Contains("enabled-service", keys);
        Assert.Contains("sample-data-sign-off", keys);
        Assert.Contains("license", keys);
        Assert.Contains("rate-limit", keys);

        // The test host publishes at http://localhost outside Development: plain http is a blocker there.
        var publicBase = checks.Single(check => check.GetProperty("key").GetString() == "public-base-url");
        Assert.Equal("blocker", publicBase.GetProperty("status").GetString());
        Assert.Equal("blocker", root.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(publicBase.GetProperty("howToFix").GetString()));

        Assert.DoesNotContain("readiness-secret-x", body);
        Assert.DoesNotContain("SigningKey", body);
    }

    // ---- Clinical sign-off ----

    [Fact]
    public async Task Sign_off_needs_cdshooks_edit()
    {
        var id = await CreateServiceAsync();
        var (_, _, jwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("cdshooks.view");
        using var viewer = _fixture.CreateAuthenticatedClient(jwt);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync($"/api/v1/cds/services/{id}/sign-off", new { SignedOffBy = "Dr A", ContentVersion = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/v1/cds/services/{id}/sign-off")).StatusCode);
    }

    [Fact]
    public async Task An_editor_signs_off_a_service_and_a_content_change_clears_it()
    {
        var id = await CreateServiceAsync();
        var (_, _, jwt) = await _fixture.CreateUserWithPermissionsAndLoginAsync("cdshooks.view", "cdshooks.edit");
        using var editor = _fixture.CreateAuthenticatedClient(jwt);

        var signed = await editor.PostAsJsonAsync($"/api/v1/cds/services/{id}/sign-off", new { SignedOffBy = "Dr A. Patel, CMIO", Note = "P&T review", ContentVersion = await ContentVersionAsync(id) });
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        var dto = JsonDocument.Parse(await signed.Content.ReadAsStringAsync()).RootElement;
        Assert.True(dto.GetProperty("isClinicallySignedOff").GetBoolean());
        Assert.Equal("Dr A. Patel, CMIO", dto.GetProperty("signedOffBy").GetString());
        Assert.Equal("P&T review", dto.GetProperty("signOffNote").GetString());
        Assert.Equal(JsonValueKind.String, dto.GetProperty("signedOffAtUtc").ValueKind);
        Assert.True(dto.GetProperty("usesSampleReferenceData").GetBoolean());

        // The list and the setup sheet show it.
        var list = JsonDocument.Parse(await editor.GetStringAsync("/api/v1/cds/services")).RootElement;
        var row = list.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);
        Assert.Equal("Dr A. Patel, CMIO", row.GetProperty("signedOffBy").GetString());
        var sheet = JsonDocument.Parse(await editor.GetStringAsync($"/api/v1/cds/services/{id}/setup-sheet")).RootElement;
        Assert.True(sheet.GetProperty("isClinicallySignedOff").GetBoolean());

        // Switching it off keeps the sign-off; changing a setting clears it.
        var off = await editor.PutAsJsonAsync($"/api/v1/cds/services/{id}", ServiceBody(isEnabled: false));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.True(JsonDocument.Parse(await off.Content.ReadAsStringAsync()).RootElement.GetProperty("isClinicallySignedOff").GetBoolean());

        var changed = await editor.PutAsJsonAsync($"/api/v1/cds/services/{id}", ServiceBody(isEnabled: false, indicator: "critical"));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var afterChange = JsonDocument.Parse(await changed.Content.ReadAsStringAsync()).RootElement;
        Assert.False(afterChange.GetProperty("isClinicallySignedOff").GetBoolean());
        Assert.Equal(JsonValueKind.Null, afterChange.GetProperty("signedOffBy").ValueKind);

        // A sign-off for the version the reviewer saw before the change is refused: 409.
        var stale = await editor.PostAsJsonAsync($"/api/v1/cds/services/{id}/sign-off", new { SignedOffBy = "Dr A", ContentVersion = dto.GetProperty("contentVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("changed since it was opened", await stale.Content.ReadAsStringAsync());
        Assert.NotEqual(dto.GetProperty("contentVersion").GetString(), afterChange.GetProperty("contentVersion").GetString());
    }

    [Fact]
    public async Task Delete_removes_the_sign_off()
    {
        var id = await CreateServiceAsync();
        await ApiFixture.EnsureOkAsync(await _fixture.AdminClient.PostAsJsonAsync($"/api/v1/cds/services/{id}/sign-off", new { SignedOffBy = "Dr A", ContentVersion = await ContentVersionAsync(id) }));

        var resp = await _fixture.AdminClient.DeleteAsync($"/api/v1/cds/services/{id}/sign-off");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("isClinicallySignedOff").GetBoolean());
    }

    [Fact]
    public async Task Sign_off_without_a_name_is_400_and_of_an_unknown_service_404()
    {
        var id = await CreateServiceAsync();

        var blank = await _fixture.AdminClient.PostAsJsonAsync($"/api/v1/cds/services/{id}/sign-off", new { SignedOffBy = "  ", ContentVersion = await ContentVersionAsync(id) });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Reviewer name", await blank.Content.ReadAsStringAsync());

        var unknown = await _fixture.AdminClient.PostAsJsonAsync($"/api/v1/cds/services/{Guid.NewGuid()}/sign-off", new { SignedOffBy = "Dr A", ContentVersion = "x" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ---- Helpers ----

    private object ServiceBody(bool isEnabled = true, string indicator = "warning") => new
    {
        ServiceId = _serviceId,
        Title = "IT go-live",
        HandlerKey = "drug-allergy",
        Hook = "order-sign",
        IsEnabled = isEnabled,
        CardSourceLabel = "CDS integration test",
        Settings = new Dictionary<string, object> { ["allergyIndicator"] = indicator },
        FetchMissingData = true,
    };

    private async Task<Guid> CreateServiceAsync()
    {
        var resp = await _fixture.AdminClient.PostAsJsonAsync("/api/v1/cds/services", ServiceBody());
        await ApiFixture.EnsureOkAsync(resp);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<string> ContentVersionAsync(Guid id) =>
        JsonDocument.Parse(await _fixture.AdminClient.GetStringAsync($"/api/v1/cds/services/{id}")).RootElement.GetProperty("contentVersion").GetString()!;
}
