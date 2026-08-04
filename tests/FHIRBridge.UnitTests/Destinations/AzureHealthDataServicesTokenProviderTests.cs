using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// Covers credential selection (client-credentials vs. managed identity) and cache short-circuiting. Actual token
/// acquisition against Entra ID is out of scope for a unit test — those paths are exercised only far enough to
/// confirm our own validation passed and the Azure SDK credential took over (at which point a pre-cancelled token
/// makes it fail fast rather than attempting real network I/O).
/// </summary>
public sealed class AzureHealthDataServicesTokenProviderTests
{
    private const string FhirServiceUrl = "https://myworkspace-myfhir.fhir.azurehealthcareapis.com";

    private static AzureHealthDataServicesConnectionOptions ClientCredentialsOptions(
        string? tenantId = "tenant-1", string? clientId = "client-1") =>
        new(FhirServiceUrl, "clientCredentials", tenantId, clientId, Scope: null, ManagedIdentityClientId: null);

    private static AzureHealthDataServicesConnectionOptions ManagedIdentityOptions(string? managedIdentityClientId = null) =>
        new(FhirServiceUrl, "managedIdentity", TenantId: null, ClientId: null, Scope: null, managedIdentityClientId);

    [Fact]
    public async Task Returns_the_cached_token_without_building_a_credential()
    {
        var cache = new Mock<IFhirAccessTokenCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("cached-token");
        var provider = new AzureHealthDataServicesTokenProvider(cache.Object);

        var token = await provider.GetAccessTokenAsync(ClientCredentialsOptions(), clientSecret: null, CancellationToken.None);

        token.Should().Be("cached-token");
        cache.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null, "client-1")]
    [InlineData("tenant-1", null)]
    public async Task Client_credentials_mode_without_tenant_or_client_id_throws(string? tenantId, string? clientId)
    {
        var provider = new AzureHealthDataServicesTokenProvider(EmptyCache());

        var act = async () => await provider.GetAccessTokenAsync(
            ClientCredentialsOptions(tenantId, clientId), clientSecret: "secret", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tenant id and client id*");
    }

    [Fact]
    public async Task Client_credentials_mode_without_a_secret_throws()
    {
        var provider = new AzureHealthDataServicesTokenProvider(EmptyCache());

        var act = async () => await provider.GetAccessTokenAsync(ClientCredentialsOptions(), clientSecret: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*client secret*");
    }

    [Fact]
    public async Task Managed_identity_mode_requires_neither_tenant_client_id_nor_secret()
    {
        var provider = new AzureHealthDataServicesTokenProvider(EmptyCache());

        // No tenant/client id/secret supplied. Our own guard clauses only apply to client-credentials mode, so this
        // must get past them and reach the actual Azure SDK credential — which, given a pre-cancelled token and no
        // managed identity endpoint in this environment, fails fast without ever calling back into our validation.
        var act = async () => await provider.GetAccessTokenAsync(
            ManagedIdentityOptions(), clientSecret: null, new CancellationToken(canceled: true));

        var thrown = await act.Should().ThrowAsync<Exception>();
        thrown.Which.Should().NotBeOfType<InvalidOperationException>();
    }

    private static IFhirAccessTokenCache EmptyCache()
    {
        var cache = new Mock<IFhirAccessTokenCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        return cache.Object;
    }
}
