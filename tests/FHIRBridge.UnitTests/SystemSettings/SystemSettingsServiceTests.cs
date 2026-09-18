using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.SystemSettings;

public sealed class SystemSettingsServiceTests
{
    private readonly Mock<ISystemSettingRepository> _repository = new();
    private readonly Mock<ISystemSettingsCache> _cache = new();

    private SystemSettingsService Service() => new(
        _repository.Object,
        _cache.Object,
        new FHIRBridge.UnitTests.Security.PassthroughUserDisplayNameResolver());

    // OAuth:PublicBaseUrl is written straight into a URL registered with an EHR (see
    // OAuthPublicOriginResolver) — a bad value here must fail the save, not silently corrupt every
    // redirect_uri built afterward.
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("https://portal.example.com/path")]
    [InlineData("https://portal.example.com?query=1")]
    [InlineData("https://portal.example.com/#fragment")]
    public async Task SetAsync_rejects_a_malformed_OAuth_PublicBaseUrl(string value)
    {
        var act = () => Service().SetAsync("OAuth:PublicBaseUrl", value, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repository.Verify(x => x.UpsertAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetAsync_accepts_a_blank_OAuth_PublicBaseUrl_meaning_derive_it_from_the_request()
    {
        _repository
            .Setup(x => x.UpsertAsync("OAuth:PublicBaseUrl", string.Empty, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SystemSetting("OAuth:PublicBaseUrl", string.Empty, null));

        var result = await Service().SetAsync("OAuth:PublicBaseUrl", "   ", null, CancellationToken.None);

        result.Value.Should().BeEmpty();
        _cache.Verify(x => x.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task SetAsync_accepts_a_valid_OAuth_PublicBaseUrl_and_invalidates_the_cache()
    {
        _repository
            .Setup(x => x.UpsertAsync(
                "OAuth:PublicBaseUrl", "https://seguedemo.pegasusone.com", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SystemSetting("OAuth:PublicBaseUrl", "https://seguedemo.pegasusone.com", null));

        var result = await Service().SetAsync(
            "OAuth:PublicBaseUrl", "https://seguedemo.pegasusone.com/", null, CancellationToken.None);

        result.Value.Should().Be("https://seguedemo.pegasusone.com");
        _cache.Verify(x => x.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task SetAsync_does_not_validate_unrelated_keys()
    {
        _repository
            .Setup(x => x.UpsertAsync("RuntimeWorker:IntervalSeconds", "not-a-number", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SystemSetting("RuntimeWorker:IntervalSeconds", "not-a-number", null));

        var act = () => Service().SetAsync("RuntimeWorker:IntervalSeconds", "not-a-number", null, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
