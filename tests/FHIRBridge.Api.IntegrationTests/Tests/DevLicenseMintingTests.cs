using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// Proves the actual security boundary on the TEMPORARY, DEV-ONLY <c>DevLicenseMintingController</c>: its
/// <c>IHostEnvironment.IsDevelopment()</c> gate must 404 the endpoint on any non-Development host. This
/// suite's shared <see cref="ApiFixture"/>/<see cref="ApiFactory"/> hosts the API under
/// <c>builder.UseEnvironment("Testing")</c> (see ApiFactory.ConfigureWebHost) — i.e. a real, running,
/// non-Development instance of the app — so a 404 here is a genuine end-to-end confirmation of the gate,
/// not a code-inspection claim. Even the fully-authenticated SuperAdmin client used everywhere else in this
/// suite must be refused: the environment gate is checked before anything else in the action, independent
/// of who is calling.
/// </summary>
[Collection("ApiTests")]
public sealed class DevLicenseMintingTests(ApiFixture f)
{
    private static readonly object ValidRequestBody = new
    {
        CustomerId = "cust-gate-test",
        CustomerName = "Gate Test Co",
        Edition = "standard",
        ExpiresUtc = DateTime.UtcNow.AddYears(1),
        MaxUsers = -1,
        MaxWorkflows = -1,
        MaxSourceConnections = -1,
        Features = Array.Empty<string>(),
    };

    [Fact]
    public async Task POST_dev_license_mint__non_development_host__returns_404_even_for_superadmin()
    {
        // f.AdminClient is a fully-authenticated SuperAdmin session against a host running in the
        // "Testing" environment (not Development) — exactly the case the gate exists to block.
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/dev/license-mint", ValidRequestBody);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task POST_dev_license_mint__non_development_host_unauthenticated__returns_404_or_401_never_a_token()
    {
        // Whether the framework short-circuits to 401 (unauthenticated) or the action's own gate returns
        // 404, the one outcome that must NEVER happen against a non-Development host is a 200 with a
        // minted token in the body.
        var resp = await f.AnonClient.PostAsJsonAsync("/api/v1/dev/license-mint", ValidRequestBody);

        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
    }
}
