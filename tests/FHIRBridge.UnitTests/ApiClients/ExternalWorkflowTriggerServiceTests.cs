using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Governance;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.ApiClients;

public sealed class ExternalWorkflowTriggerServiceTests
{
    private readonly Mock<IApiClientRepository> _repository = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<IGovernanceLogger> _governanceLogger = new();

    private ExternalWorkflowTriggerService Service() => new(_repository.Object, _passwordHasher.Object, _governanceLogger.Object);

    private ApiClient ValidClientWithReturnUrl(string returnUrl = "https://app.example.com/callback")
    {
        var client = new ApiClient("Test client", "cid_valid", "hash:correct");
        client.AddReturnUrl(returnUrl, null);
        _repository.Setup(x => x.GetByClientIdAsync("cid_valid", It.IsAny<CancellationToken>())).ReturnsAsync(client);
        _passwordHasher.Setup(x => x.Verify("correct-secret", "hash:correct")).Returns(true);
        return client;
    }

    [Fact]
    public async Task ValidateAsync_succeeds_for_a_registered_returnUrl_and_no_referer()
    {
        ValidClientWithReturnUrl();

        var result = await Service().ValidateAsync(
            "cid_valid", "correct-secret", "https://app.example.com/callback", refererHeader: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ValidatedReturnUrl.Should().Be("https://app.example.com/callback");
    }

    [Fact]
    public async Task ValidateAsync_succeeds_when_referer_matches_a_registered_origin()
    {
        ValidClientWithReturnUrl();

        var result = await Service().ValidateAsync(
            "cid_valid", "correct-secret", "https://app.example.com/callback",
            refererHeader: "https://app.example.com/trigger-page", CancellationToken.None);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_fails_when_referer_present_but_does_not_match_any_registered_origin()
    {
        ValidClientWithReturnUrl();

        var result = await Service().ValidateAsync(
            "cid_valid", "correct-secret", "https://app.example.com/callback",
            refererHeader: "https://attacker.example.net/", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_fails_for_an_unregistered_returnUrl()
    {
        ValidClientWithReturnUrl();

        var result = await Service().ValidateAsync(
            "cid_valid", "correct-secret", "https://not-registered.example.com/callback", refererHeader: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("HTTPS://APP.EXAMPLE.COM/callback")]
    [InlineData("https://app.example.com:443/callback")]
    public async Task ValidateAsync_accepts_an_equivalent_spelling_of_a_registered_exact_returnUrl(string callerUrl)
    {
        ValidClientWithReturnUrl();

        var result = await Service().ValidateAsync(
            "cid_valid", "correct-secret", callerUrl, refererHeader: null, CancellationToken.None);

        result.Should().NotBeNull();
    }

    [Theory]
    [InlineData("https://app.example.com/callback?next=/somewhere")]
    [InlineData("https://app.example.com/callback#/done")]
    public async Task ValidateAsync_still_rejects_query_or_fragment_on_an_exact_entry(string callerUrl)
    {
        ValidClientWithReturnUrl();

        var result = await Service().ValidateAsync(
            "cid_valid", "correct-secret", callerUrl, refererHeader: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_accepts_any_path_query_and_fragment_under_a_domain_entry()
    {
        var client = new ApiClient("Test client", "cid_valid", "hash:correct");
        client.AddReturnUrl("https://app.example.com", null, ReturnUrlMatchMode.Domain);
        _repository.Setup(x => x.GetByClientIdAsync("cid_valid", It.IsAny<CancellationToken>())).ReturnsAsync(client);
        _passwordHasher.Setup(x => x.Verify("correct-secret", "hash:correct")).Returns(true);

        var result = await Service().ValidateAsync(
            "cid_valid", "correct-secret", "https://app.example.com/any/page?x=1#/done", refererHeader: null, CancellationToken.None);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_rejects_a_different_host_even_when_a_domain_entry_exists()
    {
        var client = new ApiClient("Test client", "cid_valid", "hash:correct");
        client.AddReturnUrl("https://app.example.com", null, ReturnUrlMatchMode.Domain);
        _repository.Setup(x => x.GetByClientIdAsync("cid_valid", It.IsAny<CancellationToken>())).ReturnsAsync(client);
        _passwordHasher.Setup(x => x.Verify("correct-secret", "hash:correct")).Returns(true);

        var result = await Service().ValidateAsync(
            "cid_valid", "correct-secret", "https://app.example.com.evil.net/x", refererHeader: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_fails_for_a_wrong_secret()
    {
        ValidClientWithReturnUrl();
        _passwordHasher.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var result = await Service().ValidateAsync(
            "cid_valid", "wrong-secret", "https://app.example.com/callback", refererHeader: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_fails_for_a_disabled_client_even_with_a_registered_returnUrl()
    {
        var client = ValidClientWithReturnUrl();
        client.SetEnabled(false);

        var result = await Service().ValidateAsync(
            "cid_valid", "correct-secret", "https://app.example.com/callback", refererHeader: null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateCredentialAsync_succeeds_without_requiring_a_returnUrl()
    {
        ValidClientWithReturnUrl();

        var result = await Service().ValidateCredentialAsync("cid_valid", "correct-secret", refererHeader: null, CancellationToken.None);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateCredentialAsync_succeeds_when_referer_matches_a_registered_return_url_origin()
    {
        ValidClientWithReturnUrl();

        var result = await Service().ValidateCredentialAsync(
            "cid_valid", "correct-secret", refererHeader: "https://app.example.com/some/page", CancellationToken.None);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateCredentialAsync_fails_when_referer_does_not_match_any_registered_origin()
    {
        ValidClientWithReturnUrl();

        var result = await Service().ValidateCredentialAsync(
            "cid_valid", "correct-secret", refererHeader: "https://evil.example.com/", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_logs_every_attempt_without_ever_including_the_secret()
    {
        ValidClientWithReturnUrl();

        await Service().ValidateAsync(
            "cid_valid", "correct-secret", "https://not-registered.example.com/callback", refererHeader: null, CancellationToken.None);

        _governanceLogger.Verify(x => x.LogAuthenticationAsync(
            It.Is<AuthenticationEntry>(e =>
                e.AuthenticationType == "ExternalWorkflowTrigger"
                && !e.Success
                && (e.FailureReason == null || !e.FailureReason.Contains("correct-secret"))),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
