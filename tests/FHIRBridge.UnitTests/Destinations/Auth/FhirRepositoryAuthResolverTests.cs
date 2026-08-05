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

    [Theory]
    [InlineData(null)]
    [InlineData("""{"dest_fhirAuthType":"none"}""")]
    public async Task None_or_absent_returns_null_without_touching_the_secret(string? metadataJson)
    {
        var secretProvider = SecretProvider("should-never-be-read");

        var header = await FhirRepositoryAuthResolver.ResolveAsync(
            metadataJson, Secret, secretProvider.Object, Mock.Of<IFhirDestinationTokenProvider>(), CancellationToken.None);

        header.Should().BeNull();
        secretProvider.Verify(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Bearer_parses_token_from_secret()
    {
        var secretProvider = SecretProvider("""{"token":"abc123"}""");

        var header = await FhirRepositoryAuthResolver.ResolveAsync(
            """{"dest_fhirAuthType":"bearer"}""", Secret, secretProvider.Object, Mock.Of<IFhirDestinationTokenProvider>(), CancellationToken.None);

        header!.Scheme.Should().Be("Bearer");
        header.Parameter.Should().Be("abc123");
    }

    [Fact]
    public async Task Basic_parses_username_and_password_from_secret_into_a_basic_header()
    {
        var secretProvider = SecretProvider("""{"username":"svc-account","password":"s3cret"}""");

        var header = await FhirRepositoryAuthResolver.ResolveAsync(
            """{"dest_fhirAuthType":"basic"}""", Secret, secretProvider.Object, Mock.Of<IFhirDestinationTokenProvider>(), CancellationToken.None);

        header!.Scheme.Should().Be("Basic");
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!));
        decoded.Should().Be("svc-account:s3cret");
    }

    [Fact]
    public async Task Basic_missing_username_or_password_throws()
    {
        var secretProvider = SecretProvider("""{"username":"svc-account"}""");

        var act = () => FhirRepositoryAuthResolver.ResolveAsync(
            """{"dest_fhirAuthType":"basic"}""", Secret, secretProvider.Object, Mock.Of<IFhirDestinationTokenProvider>(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Bearer_without_token_field_throws()
    {
        var secretProvider = SecretProvider("""{}""");

        var act = () => FhirRepositoryAuthResolver.ResolveAsync(
            """{"dest_fhirAuthType":"bearer"}""", Secret, secretProvider.Object, Mock.Of<IFhirDestinationTokenProvider>(), CancellationToken.None);

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

        var header = await FhirRepositoryAuthResolver.ResolveAsync(
            """{"dest_fhirAuthType":"clientCredentials"}""", Secret, secretProvider.Object, tokenProvider.Object, CancellationToken.None);

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

        var act = () => FhirRepositoryAuthResolver.ResolveAsync(
            """{"dest_fhirAuthType":"clientCredentials"}""", Secret, secretProvider.Object, Mock.Of<IFhirDestinationTokenProvider>(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Unsupported_auth_type_throws()
    {
        var secretProvider = SecretProvider("""{}""");

        var act = () => FhirRepositoryAuthResolver.ResolveAsync(
            """{"dest_fhirAuthType":"madeUp"}""", Secret, secretProvider.Object, Mock.Of<IFhirDestinationTokenProvider>(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
