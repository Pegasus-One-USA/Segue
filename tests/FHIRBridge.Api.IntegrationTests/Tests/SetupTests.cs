using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class SetupTests(ApiFixture f)
{
    // The fixture seeds a SuperAdmin, so the deployment is already initialized.

    [Fact]
    public async Task GET_setup_status__anonymous__returns_requiresSetup_false_when_initialized()
    {
        var resp = await f.AnonClient.GetAsync("/api/v1/auth/setup-status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.False(doc.GetProperty("requiresSetup").GetBoolean());
    }

    [Fact]
    public async Task POST_setup_superadmin__when_already_initialized__returns_409()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/setup-superadmin", new
        {
            Email = "another-admin@testhospital.test",
            DisplayName = "Another Admin",
            Password = "AnotherAdmin@Test123!",
            // A complete first-run request, so the refusal is the "already set up" 409, not request validation.
            AcceptTerms = true,
            EmailSettings = new
            {
                Host = "localhost", Port = 25, EnableSsl = false, Username = "smtp-test", Password = "smtp-test",
                FromAddress = "noreply@testhospital.test", FromName = "FHIRBridge Integration Tests",
            },
        });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }
}
