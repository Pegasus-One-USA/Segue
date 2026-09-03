using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FHIRBridge.Domain.Enums;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

[Collection("ApiTests")]
public sealed class SsoTests(ApiFixture f)
{
    [Fact]
    public async Task GET_config__anonymous__returns_provider_shape()
    {
        var resp = await f.AnonClient.GetAsync("/api/v1/config");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        var entra = doc.GetProperty("entra");
        Assert.False(entra.GetProperty("enabled").GetBoolean());

        var google = doc.GetProperty("google");
        Assert.True(google.GetProperty("enabled").GetBoolean());
        Assert.Equal("test-google-client-id", google.GetProperty("clientId").GetString());
    }

    [Fact]
    public async Task POST_setup_superadmin_sso__when_already_initialized__returns_409()
    {
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/auth/setup-superadmin-sso", new
        {
            Provider = LoginProvider.Google,
            Token = "google:someone@testhospital.test"
        });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task POST_accept_invite_sso__email_mismatch__returns_400()
    {
        // The stub validator echoes the token as the external email; use a value that differs from the
        // invited address so the email-binding guard rejects the request with 400.
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/users/accept-invite-sso", new
        {
            Email = "invited@testhospital.test",
            InvitationToken = f.InvitationToken,
            Provider = LoginProvider.Google,
            Token = "google:someone-else@evil.test",
            AcceptTerms = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
