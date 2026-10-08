using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// eClinicalWorks and athenahealth as write-back targets, as the running app wires them: every capability the table
/// lists has a registered profile (a gap would only surface on a live run), the capabilities API tells the portal which
/// types need the vendor activated, and turning a connection's vendor write APIs on needs the EHR Write-Back edit right.
/// </summary>
[Collection("ApiTests")]
public sealed class EhrWriteVendorTargetTests(ApiFixture f)
{
    private static object WriteConnectionBody(bool activated) => new
    {
        Name = $"Epic-write-{Guid.NewGuid():N}",
        SourceSystemType = "Epic",
        BaseUrl = "https://example.test/fhir",
        ApplicationType = "Backend",
        Access = "Write",
        VendorWriteApisActivated = activated,
        Authentication = new
        {
            AuthenticationType = "SmartBackendServices",
            ClientId = "test-client-id",
            TokenEndpoint = "https://example.test/oauth2/token",
            Scopes = new[] { "system/*.read" },
            KeyId = "test-key-id",
            PrivateKeyKeyVaultName = "test-vault",
            PrivateKeySecretName = "test-secret",
        },
    };

    [Theory]
    [InlineData(SourceSystemType.Epic)]
    [InlineData(SourceSystemType.Healow)]
    [InlineData(SourceSystemType.Athenahealth)]
    public void Every_capability_has_a_registered_write_profile(SourceSystemType vendor)
    {
        var registry = f.Services.GetRequiredService<EhrWriteProfileRegistry>();

        foreach (var capability in EhrWriteCapabilities.For(vendor))
        {
            Assert.True(
                registry.Find(vendor, capability.ResourceType, capability.Variant) is not null,
                $"{vendor} {capability.ResourceType} {capability.Variant} has no registered profile.");
        }
    }

    [Fact]
    public async Task The_capabilities_api_says_which_types_need_the_vendor_activated()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/ehr-write-capabilities?vendor=Healow");
        await ApiFixture.EnsureOkAsync(resp);

        var capabilities = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("capabilities");
        var allergy = capabilities.EnumerateArray().First(c => c.GetProperty("resourceType").GetString() == "AllergyIntolerance");
        Assert.True(allergy.GetProperty("liveWriteSupported").GetBoolean());
        Assert.True(allergy.GetProperty("requiresVendorActivation").GetBoolean());
        Assert.Contains(capabilities.EnumerateArray(), c => c.GetProperty("createsHolderEncounter").GetBoolean());
    }

    [Fact]
    public async Task Activating_vendor_write_apis_needs_the_ehr_write_back_edit_right()
    {
        // ehrwriteback.create is what creating a connection with write access needs at all (EhrWriteConnectionTests).
        var (_, _, withoutJwt) = await f.CreateUserWithPermissionsAndLoginAsync("epic.create", "ehrwriteback.create");
        var (_, _, withJwt) = await f.CreateUserWithPermissionsAndLoginAsync("epic.create", "ehrwriteback.create", "ehrwriteback.edit");
        using var without = f.CreateAuthenticatedClient(withoutJwt);
        using var with = f.CreateAuthenticatedClient(withJwt);

        var refused = await without.PostAsJsonAsync("/api/v1/source-connections", WriteConnectionBody(activated: true));
        var notActivated = await without.PostAsJsonAsync("/api/v1/source-connections", WriteConnectionBody(activated: false));
        var allowed = await with.PostAsJsonAsync("/api/v1/source-connections", WriteConnectionBody(activated: true));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, notActivated.StatusCode);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        var created = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync()).RootElement;
        Assert.True(created.GetProperty("vendorWriteApisActivated").GetBoolean());
    }
}
