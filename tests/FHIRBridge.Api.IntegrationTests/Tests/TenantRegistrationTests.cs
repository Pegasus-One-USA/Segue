using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class TenantRegistrationTests(ApiFixture f)
{
    // ── POST /api/v1/register ────────────────────────────────────────────────────

    [Fact]
    public async Task POST_register__valid_request__returns_201_with_token()
    {
        // Register a second tenant to verify the endpoint works independently
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/register", new
        {
            OrgName        = "Second Hospital",
            OrgType        = "Clinic",
            Country        = "US",
            Timezone       = "UTC",
            AdminEmail     = "admin2@secondhospital.test",
            AdminPassword  = "Admin@Second1234!",
            AdminFirstName = "Second",
            AdminLastName  = "Admin"
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.TryGetProperty("tenantId", out _));
        Assert.True(doc.TryGetProperty("userId", out _));
        Assert.True(doc.TryGetProperty("accessToken", out _));
    }

    [Fact]
    public async Task POST_register__duplicate_email__returns_409_or_400()
    {
        // Admin email is already used by the fixture-registered tenant
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/register", new
        {
            OrgName        = "Duplicate Org",
            OrgType        = "Hospital",
            Country        = "US",
            Timezone       = "UTC",
            AdminEmail     = ApiFixture.AdminEmail,  // already taken
            AdminPassword  = "Admin@Test1234!",
            AdminFirstName = "Dup",
            AdminLastName  = "Admin"
        });
        Assert.True(
            resp.StatusCode == HttpStatusCode.Conflict ||
            resp.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_register__weak_password__returns_400()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/register", new
        {
            OrgName        = "Weak Pass Org",
            OrgType        = "Hospital",
            Country        = "US",
            Timezone       = "UTC",
            AdminEmail     = "weakpw@test.local",
            AdminPassword  = "weak",               // too short, no complexity
            AdminFirstName = "Test",
            AdminLastName  = "User"
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
