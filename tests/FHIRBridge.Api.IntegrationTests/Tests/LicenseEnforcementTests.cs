using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Infrastructure.Licensing;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// End-to-end proof that real license enforcement blocks an actual HTTP call with 403 once a quota/allow-list
/// is hit — the exact scenario the user reported (set a limit, still able to create one more anyway).
///
/// Mints a real, in-process-signed license token via <see cref="DevLicenseMintingService"/> (the same dev
/// private key <c>SignedLicenseValidator</c> verifies against — see that class's remarks) and applies it via
/// the real, non-environment-gated <c>POST /api/v1/license</c> endpoint, rather than the temporary
/// <c>DevLicenseMintingController</c> HTTP endpoint (which 404s outside Development — see
/// <see cref="DevLicenseMintingTests"/> — and this suite's <see cref="ApiFactory"/> runs "Testing").
///
/// <see cref="ApiFixture"/>'s host (and its underlying <c>ILicenseService.Current</c> singleton) is shared
/// across every test in the "ApiTests" collection, so applying a restrictive license here mutates state every
/// other test in the collection would also see. Each test restores an unrestricted license in a `finally`
/// block so it doesn't affect test order elsewhere in the collection.
/// </summary>
[Collection("ApiTests")]
public sealed class LicenseEnforcementTests(ApiFixture f)
{
    private static string MintToken(
        int maxUsers = LicenseLimits.Unlimited,
        int maxWorkflows = LicenseLimits.Unlimited,
        int maxSourceConnections = LicenseLimits.Unlimited,
        IReadOnlyList<string>? allowedSourceTypes = null) =>
        new DevLicenseMintingService().Mint(new DevLicenseMintRequest(
            CustomerId: $"license-enforcement-test-{Guid.NewGuid():N}",
            CustomerName: "License Enforcement Test",
            Edition: "standard",
            ExpiresUtc: DateTime.UtcNow.AddYears(1),
            MaxUsers: maxUsers,
            MaxWorkflows: maxWorkflows,
            MaxSourceConnections: maxSourceConnections,
            Features: Array.Empty<string>(),
            AllowedSourceTypes: allowedSourceTypes));

    private static async Task ApplyLicenseAsync(HttpClient client, string token)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/license", new { Token = token });
        resp.EnsureSuccessStatusCode();
    }

    private static async Task RestoreUnlimitedLicenseAsync(HttpClient client) =>
        await ApplyLicenseAsync(client, MintToken());

    private static async Task<int> GetCurrentUserCountAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/v1/license");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return doc.GetProperty("usage").GetProperty("userCount").GetInt32();
    }

    private static object NewUserPayload(string email) => new
    {
        Email = email,
        DisplayName = "License Test User",
        Password = "LicenseTest@Pass123!",
        RoleNames = new[] { "Operations" },
        RequirePasswordChange = false,
    };

    /// <summary>
    /// The user's exact reported scenario: apply a license with MaxUsers set to exactly the current live
    /// count + 1 (so this install is at capacity after one more create), successfully create that one
    /// permitted user, then confirm the NEXT create is rejected with 403 — byte-for-byte "set a 3-user limit,
    /// try to register a 4th" once expressed relative to whatever count this shared host already carries.
    /// </summary>
    [Fact]
    public async Task Applying_a_MaxUsers_license_blocks_user_creation_past_the_cap_with_403()
    {
        var currentUserCount = await GetCurrentUserCountAsync(f.AdminClient);
        await ApplyLicenseAsync(f.AdminClient, MintToken(maxUsers: currentUserCount + 1));

        try
        {
            var allowedCreate = await f.AdminClient.PostAsJsonAsync(
                "/api/v1/users", NewUserPayload($"license-test-{Guid.NewGuid():N}@testhospital.test"));
            Assert.Equal(HttpStatusCode.Created, allowedCreate.StatusCode);

            var blockedCreate = await f.AdminClient.PostAsJsonAsync(
                "/api/v1/users", NewUserPayload($"license-test-{Guid.NewGuid():N}@testhospital.test"));
            Assert.Equal(HttpStatusCode.Forbidden, blockedCreate.StatusCode);

            var body = await blockedCreate.Content.ReadAsStringAsync();
            Assert.Contains("license", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await RestoreUnlimitedLicenseAsync(f.AdminClient);
        }
    }

    /// <summary>
    /// Allow-list rejection (not a counted quota): a license naming only "Cerner" as an allowed source vendor
    /// must reject creating a "Sample"-vendor source connection with 403, even though no numeric cap was ever
    /// hit.
    /// </summary>
    [Fact]
    public async Task Applying_an_AllowedSourceTypes_license_blocks_a_disallowed_vendor_with_403()
    {
        await ApplyLicenseAsync(f.AdminClient, MintToken(allowedSourceTypes: new[] { "Cerner" }));

        try
        {
            var payload = new
            {
                name = $"License Test Source {Guid.NewGuid():N}",
                sourceSystemType = "Sample",
                baseUrl = "https://example.org/fhir",
                authentication = new
                {
                    authenticationType = "None",
                    clientId = (string?)null,
                    tokenEndpoint = (string?)null,
                    scopes = Array.Empty<string>(),
                    clientSecretKeyVaultName = (string?)null,
                    clientSecretName = (string?)null,
                    privateKeyKeyVaultName = (string?)null,
                    privateKeySecretName = (string?)null,
                    keyId = (string?)null,
                },
            };

            var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/source-connections", payload);

            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("license", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await RestoreUnlimitedLicenseAsync(f.AdminClient);
        }
    }
}
