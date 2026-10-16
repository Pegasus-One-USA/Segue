using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace FHIRBridge.UnitTests.ApiClients;

public sealed class ClientCredentialsAccessTokenIssuerTests
{
    [Fact]
    public async Task A_token_stays_valid_while_its_client_is_enabled_and_unrotated()
    {
        var client = new ApiClient("Client", "cid_1", "hash:one");
        client.SetEnabled(true);
        var user = PrincipalFor(Issuer().Issue(client).AccessToken);

        (await ClientCredentialsAccessTokenIssuer.IsStillValidAsync(user, Repository(client), CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Regenerating_the_secret_revokes_tokens_already_issued()
    {
        var client = new ApiClient("Client", "cid_1", "hash:one");
        client.SetEnabled(true);
        var user = PrincipalFor(Issuer().Issue(client).AccessToken);

        client.RegenerateSecret("hash:two");

        (await ClientCredentialsAccessTokenIssuer.IsStillValidAsync(user, Repository(client), CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Disabling_the_client_revokes_tokens_already_issued()
    {
        var client = new ApiClient("Client", "cid_1", "hash:one");
        client.SetEnabled(true);
        var user = PrincipalFor(Issuer().Issue(client).AccessToken);

        client.SetEnabled(false);

        (await ClientCredentialsAccessTokenIssuer.IsStillValidAsync(user, Repository(client), CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_deleted_client_revokes_tokens_already_issued()
    {
        var client = new ApiClient("Client", "cid_1", "hash:one");
        client.SetEnabled(true);
        var user = PrincipalFor(Issuer().Issue(client).AccessToken);

        (await ClientCredentialsAccessTokenIssuer.IsStillValidAsync(user, Repository(null), CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_token_without_the_secret_fingerprint_is_refused()
    {
        var client = new ApiClient("Client", "cid_1", "hash:one");
        client.SetEnabled(true);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client_id", "cid_1"), new Claim("token_use", "client_credentials")], "Local"));

        (await ClientCredentialsAccessTokenIssuer.IsStillValidAsync(user, Repository(client), CancellationToken.None))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("0", 15)]
    [InlineData("-5", 15)]
    [InlineData("2147483647", 15)]
    [InlineData("not-a-number", 15)]
    public void The_configured_lifetime_is_clamped_to_a_sane_range(string configured, int expectedMinutes)
    {
        var client = new ApiClient("Client", "cid_1", "hash:one");

        var token = Issuer(configured).Issue(client);

        token.ExpiresOnUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(expectedMinutes), TimeSpan.FromSeconds(30));
    }

    private static ClientCredentialsAccessTokenIssuer Issuer(string? lifetimeMinutes = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ClientCredentialsTokenLifetimeMinutes"] = lifetimeMinutes,
            })
            .Build();
        var secrets = new Mock<IAppSecretAccessor>();
        secrets.SetupGet(x => x.JwtSigningKey).Returns("unit-test-signing-key-that-is-long-enough-for-hs256-0123456789");
        return new ClientCredentialsAccessTokenIssuer(configuration, secrets.Object);
    }

    private static ClaimsPrincipal PrincipalFor(string accessToken) =>
        new(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims, "Local"));

    private static IApiClientRepository Repository(ApiClient? client)
    {
        var repository = new Mock<IApiClientRepository>();
        repository.Setup(x => x.GetByClientIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(client);
        return repository.Object;
    }
}
