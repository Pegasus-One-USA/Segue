using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Auth;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations.Auth;

public sealed class FhirRepositoryAuthResolverTests
{
    private static readonly SecretReference Secret = new("kv", "secret");

    private static Mock<ISecretProvider> SecretProvider(string value)
    {
        var mock = new Mock<ISecretProvider>();
        mock.Setup(s => s.GetSecretAsync(Secret, It.IsAny<CancellationToken>())).ReturnsAsync(value);
        return mock;
    }

    private static Task<System.Net.Http.Headers.AuthenticationHeaderValue?> ResolveAsync(
        string? metadataJson,
        ISecretProvider secretProvider,
        IFhirDestinationTokenProvider? tokenProvider = null,
        IAzureManagedIdentityFhirTokenProvider? managedIdentityTokenProvider = null,
        string? baseUrl = "https://fhir.example.com")
        => FhirRepositoryAuthResolver.ResolveAsync(
            metadataJson, Secret, secretProvider, tokenProvider ?? Mock.Of<IFhirDestinationTokenProvider>(),
            managedIdentityTokenProvider ?? Mock.Of<IAzureManagedIdentityFhirTokenProvider>(), baseUrl, CancellationToken.None);

    [Theory]
    [InlineData(null)]
    [InlineData("""{"dest_fhirAuthType":"none"}""")]
    public async Task None_or_absent_returns_null_without_touching_the_secret(string? metadataJson)
    {
        var secretProvider = SecretProvider("should-never-be-read");

        var header = await ResolveAsync(metadataJson, secretProvider.Object);

        header.Should().BeNull();
        secretProvider.Verify(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Bearer_parses_token_from_secret()
    {
        var secretProvider = SecretProvider("""{"token":"abc123"}""");

        var header = await ResolveAsync("""{"dest_fhirAuthType":"bearer"}""", secretProvider.Object);

        header!.Scheme.Should().Be("Bearer");
        header.Parameter.Should().Be("abc123");
    }

    [Fact]
    public async Task Basic_parses_username_and_password_from_secret_into_a_basic_header()
    {
        var secretProvider = SecretProvider("""{"username":"svc-account","password":"s3cret"}""");

        var header = await ResolveAsync("""{"dest_fhirAuthType":"basic"}""", secretProvider.Object);

        header!.Scheme.Should().Be("Basic");
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!));
        decoded.Should().Be("svc-account:s3cret");
    }

    [Fact]
    public async Task Basic_missing_username_or_password_throws()
    {
        var secretProvider = SecretProvider("""{"username":"svc-account"}""");

        var act = () => ResolveAsync("""{"dest_fhirAuthType":"basic"}""", secretProvider.Object);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Bearer_without_token_field_throws()
    {
        var secretProvider = SecretProvider("""{}""");

        var act = () => ResolveAsync("""{"dest_fhirAuthType":"bearer"}""", secretProvider.Object);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ClientCredentials_calls_token_provider_with_parsed_secret_fields()
    {
        var secretProvider = SecretProvider(
            """{"clientId":"cid","clientSecret":"csec","tokenEndpoint":"https://aidbox/auth/token","scope":"system/*.write"}""");
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider
            .Setup(t => t.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("minted-token");

        var header = await ResolveAsync("""{"dest_fhirAuthType":"clientCredentials"}""", secretProvider.Object, tokenProvider.Object);

        header!.Parameter.Should().Be("minted-token");
        tokenProvider.Verify(
            t => t.GetAccessTokenAsync(
                It.Is<FhirDestinationOAuth2Options>(o =>
                    o.ClientId == "cid" && o.ClientSecret == "csec" &&
                    o.TokenEndpoint == "https://aidbox/auth/token" && o.Scope == "system/*.write"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("""{"clientId":"cid"}""")]
    [InlineData("""{"clientSecret":"csec"}""")]
    [InlineData("""{"clientId":"cid","clientSecret":"csec"}""")]
    public async Task ClientCredentials_missing_required_fields_throws(string secretJson)
    {
        var secretProvider = SecretProvider(secretJson);

        var act = () => ResolveAsync("""{"dest_fhirAuthType":"clientCredentials"}""", secretProvider.Object);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Unsupported_auth_type_throws()
    {
        var secretProvider = SecretProvider("""{}""");

        var act = () => ResolveAsync("""{"dest_fhirAuthType":"madeUp"}""", secretProvider.Object);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── managedIdentity (Azure FHIR Service) ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ManagedIdentity_never_touches_the_secret_and_defaults_scope_to_base_url_default()
    {
        var secretProvider = SecretProvider("should-never-be-read");
        var managedIdentityTokenProvider = new Mock<IAzureManagedIdentityFhirTokenProvider>();
        managedIdentityTokenProvider
            .Setup(t => t.GetAccessTokenAsync(
                "https://myfhirservice.fhir.azurehealthcareapis.com/.default", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("mi-token");

        var header = await ResolveAsync(
            """{"dest_fhirAuthType":"managedIdentity"}""",
            secretProvider.Object,
            managedIdentityTokenProvider: managedIdentityTokenProvider.Object,
            baseUrl: "https://myfhirservice.fhir.azurehealthcareapis.com");

        header!.Scheme.Should().Be("Bearer");
        header.Parameter.Should().Be("mi-token");
        secretProvider.Verify(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManagedIdentity_honors_an_explicit_scope_override_and_user_assigned_identity_client_id()
    {
        var secretProvider = SecretProvider("should-never-be-read");
        var managedIdentityTokenProvider = new Mock<IAzureManagedIdentityFhirTokenProvider>();
        managedIdentityTokenProvider
            .Setup(t => t.GetAccessTokenAsync("api://custom-scope/.default", "user-assigned-client-id", "https://login.microsoftonline.us/", It.IsAny<CancellationToken>()))
            .ReturnsAsync("mi-token");

        var header = await ResolveAsync(
            """{"dest_fhirAuthType":"managedIdentity","dest_fhirAzureScope":"api://custom-scope/.default","dest_fhirManagedIdentityClientId":"user-assigned-client-id","dest_fhirAuthorityHost":"https://login.microsoftonline.us/"}""",
            secretProvider.Object,
            managedIdentityTokenProvider: managedIdentityTokenProvider.Object);

        header!.Parameter.Should().Be("mi-token");
    }

    [Fact]
    public async Task ManagedIdentity_without_scope_or_base_url_throws()
    {
        var secretProvider = SecretProvider("should-never-be-read");

        var act = () => ResolveAsync(
            """{"dest_fhirAuthType":"managedIdentity"}""", secretProvider.Object, baseUrl: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
