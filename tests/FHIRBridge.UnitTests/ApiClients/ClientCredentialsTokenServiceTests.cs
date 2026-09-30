using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Governance;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.ApiClients;

public sealed class ClientCredentialsTokenServiceTests
{
    private readonly Mock<IApiClientRepository> _repository = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<IClientCredentialsAccessTokenIssuer> _tokenIssuer = new();
    private readonly Mock<IGovernanceLogger> _governanceLogger = new();

    private ClientCredentialsTokenService Service() => new(
        _repository.Object, _passwordHasher.Object, _tokenIssuer.Object, _governanceLogger.Object);

    [Fact]
    public async Task IssueTokenAsync_succeeds_for_a_valid_enabled_client_and_stamps_LastUsedOnUtc()
    {
        var client = new ApiClient("Test client", "cid_valid", "hash:correct");
        _repository.Setup(x => x.GetByClientIdAsync("cid_valid", It.IsAny<CancellationToken>())).ReturnsAsync(client);
        _passwordHasher.Setup(x => x.Verify("correct-secret", "hash:correct")).Returns(true);
        var expectedToken = new AccessTokenDto("jwt", "Bearer", DateTime.UtcNow.AddMinutes(15));
        _tokenIssuer.Setup(x => x.Issue(client)).Returns(expectedToken);

        var result = await Service().IssueTokenAsync("cid_valid", "correct-secret", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Token.Should().Be(expectedToken);
        client.LastUsedOnUtc.Should().NotBeNull();
        _repository.Verify(x => x.UpdateAsync(client, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IssueTokenAsync_fails_for_a_wrong_secret()
    {
        var client = new ApiClient("Test client", "cid_valid", "hash:correct");
        _repository.Setup(x => x.GetByClientIdAsync("cid_valid", It.IsAny<CancellationToken>())).ReturnsAsync(client);
        _passwordHasher.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var result = await Service().IssueTokenAsync("cid_valid", "wrong-secret", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Token.Should().BeNull();
        _tokenIssuer.Verify(x => x.Issue(It.IsAny<ApiClient>()), Times.Never);
    }

    [Fact]
    public async Task IssueTokenAsync_fails_for_an_unknown_client_id_without_throwing()
    {
        _repository.Setup(x => x.GetByClientIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiClient?)null);
        _passwordHasher.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var result = await Service().IssueTokenAsync("cid_does_not_exist", "any-secret", CancellationToken.None);

        result.Success.Should().BeFalse();
        // Still calls Verify (against a dummy hash) even though there's no such client — timing parity so an
        // unknown client id isn't distinguishable from a wrong secret by response latency.
        _passwordHasher.Verify(x => x.Verify("any-secret", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task IssueTokenAsync_fails_for_a_disabled_client_even_with_the_correct_secret()
    {
        var client = new ApiClient("Test client", "cid_disabled", "hash:correct");
        client.SetEnabled(false);
        _repository.Setup(x => x.GetByClientIdAsync("cid_disabled", It.IsAny<CancellationToken>())).ReturnsAsync(client);
        _passwordHasher.Setup(x => x.Verify("correct-secret", "hash:correct")).Returns(true);

        var result = await Service().IssueTokenAsync("cid_disabled", "correct-secret", CancellationToken.None);

        result.Success.Should().BeFalse();
        _tokenIssuer.Verify(x => x.Issue(It.IsAny<ApiClient>()), Times.Never);
    }

    [Fact]
    public async Task IssueTokenAsync_logs_every_attempt_without_ever_including_the_secret()
    {
        _repository.Setup(x => x.GetByClientIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiClient?)null);
        _passwordHasher.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        await Service().IssueTokenAsync("cid_x", "top-secret-value", CancellationToken.None);

        _governanceLogger.Verify(x => x.LogAuthenticationAsync(
            It.Is<AuthenticationEntry>(e =>
                e.AuthenticationType == "ClientCredentials"
                && !e.Success
                && (e.UserEmail == null || !e.UserEmail.Contains("top-secret-value"))
                && (e.FailureReason == null || !e.FailureReason.Contains("top-secret-value"))),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
