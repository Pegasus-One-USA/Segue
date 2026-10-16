using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.ApiClients;

public sealed class ApiClientServiceTests
{
    private readonly Mock<IApiClientRepository> _repository = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();

    private ApiClientService Service() => new(
        _repository.Object,
        _passwordHasher.Object,
        new FHIRBridge.UnitTests.Security.PassthroughUserDisplayNameResolver());

    [Fact]
    public async Task CreateAsync_returns_a_verifiable_plaintext_secret_exactly_once()
    {
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns<string>(value => $"hash:{value}");

        var result = await Service().CreateAsync(new CreateApiClientRequest("Test client"), CancellationToken.None);

        result.PlaintextSecret.Should().NotBeNullOrWhiteSpace();
        result.Client.ClientId.Should().StartWith("cid_");
        _repository.Verify(x => x.AddAsync(
            It.Is<ApiClient>(c => c.ClientSecretHash == $"hash:{result.PlaintextSecret}"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_rejects_a_blank_name()
    {
        var act = () => Service().CreateAsync(new CreateApiClientRequest("   "), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repository.Verify(x => x.AddAsync(It.IsAny<ApiClient>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegenerateSecretAsync_keeps_the_ClientId_stable_and_rotates_the_hash()
    {
        var client = new ApiClient("Test client", "cid_original", "hash:old");
        _repository.Setup(x => x.GetByIdAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(client);
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns<string>(value => $"hash:{value}");

        var result = await Service().RegenerateSecretAsync(client.Id, CancellationToken.None);

        result.Client.ClientId.Should().Be("cid_original");
        client.ClientSecretHash.Should().Be($"hash:{result.PlaintextSecret}");
        client.ClientSecretHash.Should().NotBe("hash:old");
    }

    [Fact]
    public async Task RegenerateSecretAsync_rejects_an_unknown_client()
    {
        _repository.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiClient?)null);

        var act = () => Service().RegenerateSecretAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task UpdateAsync_renames_and_toggles_enabled()
    {
        var client = new ApiClient("Original name", "cid_x", "hash:x");
        _repository.Setup(x => x.GetByIdAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(client);

        var result = await Service().UpdateAsync(
            client.Id, new UpdateApiClientRequest("Renamed", false), CancellationToken.None);

        result.Name.Should().Be("Renamed");
        result.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_removes_the_client()
    {
        var client = new ApiClient("Test client", "cid_x", "hash:x");
        _repository.Setup(x => x.GetByIdAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(client);

        await Service().DeleteAsync(client.Id, CancellationToken.None);

        _repository.Verify(x => x.DeleteAsync(client, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddReturnUrlAsync_rejects_a_relative_url()
    {
        var client = new ApiClient("Test client", "cid_x", "hash:x");
        _repository.Setup(x => x.GetByIdAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(client);

        var act = () => Service().AddReturnUrlAsync(
            client.Id, new AddApiClientReturnUrlRequest("/relative/callback", null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("https://app.example.com/some/page")]
    [InlineData("https://app.example.com/?x=1")]
    [InlineData("https://app.example.com/#/done")]
    public async Task AddReturnUrlAsync_domain_mode_rejects_a_page_path_query_or_fragment(string url)
    {
        var client = new ApiClient("Test client", "cid_x", "hash:x");
        _repository.Setup(x => x.GetByIdAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(client);

        var act = () => Service().AddReturnUrlAsync(
            client.Id, new AddApiClientReturnUrlRequest(url, null, ReturnUrlMatchMode.Domain), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("https://app.example.com")]
    [InlineData("https://app.example.com/")]
    [InlineData("HTTPS://App.Example.com:443")]
    public async Task AddReturnUrlAsync_domain_mode_stores_just_the_normalized_origin(string url)
    {
        var client = new ApiClient("Test client", "cid_x", "hash:x");
        _repository.Setup(x => x.GetByIdAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(client);

        var result = await Service().AddReturnUrlAsync(
            client.Id, new AddApiClientReturnUrlRequest(url, null, ReturnUrlMatchMode.Domain), CancellationToken.None);

        result.ReturnUrls.Should().ContainSingle(x => x.Url == "https://app.example.com" && x.MatchMode == ReturnUrlMatchMode.Domain);
    }

    [Fact]
    public async Task AddReturnUrlAsync_rejects_a_duplicate_return_url_for_the_same_client()
    {
        var client = new ApiClient("Test client", "cid_x", "hash:x");
        _repository.Setup(x => x.GetByIdAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(client);
        _repository
            .Setup(x => x.ReturnUrlExistsAsync(client.Id, "https://app.example.com/callback", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var act = () => Service().AddReturnUrlAsync(
            client.Id, new AddApiClientReturnUrlRequest("https://app.example.com/callback", null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddReturnUrlAsync_then_RemoveReturnUrlAsync_round_trips()
    {
        var client = new ApiClient("Test client", "cid_x", "hash:x");
        _repository.Setup(x => x.GetByIdAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(client);

        var afterAdd = await Service().AddReturnUrlAsync(
            client.Id, new AddApiClientReturnUrlRequest("https://app.example.com/callback", "Prod"), CancellationToken.None);

        afterAdd.ReturnUrls.Should().ContainSingle(x => x.Url == "https://app.example.com/callback" && x.Label == "Prod");

        var returnUrlId = afterAdd.ReturnUrls.Single().Id;
        var afterRemove = await Service().RemoveReturnUrlAsync(client.Id, returnUrlId, CancellationToken.None);

        afterRemove.ReturnUrls.Should().BeEmpty();
    }
}
