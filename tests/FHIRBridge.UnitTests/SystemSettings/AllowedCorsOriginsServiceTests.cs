using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.SystemSettings;

public sealed class AllowedCorsOriginsServiceTests
{
    private readonly Mock<IAllowedCorsOriginRepository> _repository = new();
    private readonly Mock<IAllowedCorsOriginsCache> _cache = new();

    private AllowedCorsOriginsService Service(bool requireHttps = true) => new(
        _repository.Object,
        _cache.Object,
        Options.Create(new AllowedCorsOriginsOptions { RequireHttps = requireHttps }));

    [Fact]
    public async Task AddAsync_valid_https_origin_persists_and_invalidates_cache()
    {
        _repository.Setup(x => x.ExistsAsync("https://portal.example.com", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await Service().AddAsync(
            new CreateAllowedCorsOriginRequest("https://portal.example.com", "Prod VM"), CancellationToken.None);

        result.OriginUrl.Should().Be("https://portal.example.com");
        _repository.Verify(x => x.AddAsync(It.IsAny<AllowedCorsOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
        _cache.Verify(x => x.Invalidate(), Times.Once);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://portal.example.com")]
    public async Task AddAsync_malformed_or_unsupported_scheme_throws(string originUrl)
    {
        var act = () => Service().AddAsync(new CreateAllowedCorsOriginRequest(originUrl, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("https://portal.example.com/path")]
    [InlineData("https://portal.example.com?query=1")]
    [InlineData("https://portal.example.com/#fragment")]
    public async Task AddAsync_origin_with_path_query_or_fragment_throws(string originUrl)
    {
        var act = () => Service().AddAsync(new CreateAllowedCorsOriginRequest(originUrl, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddAsync_http_origin_throws_when_https_is_required()
    {
        var act = () => Service(requireHttps: true)
            .AddAsync(new CreateAllowedCorsOriginRequest("http://portal.example.com", null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddAsync_http_origin_allowed_when_https_not_required()
    {
        _repository.Setup(x => x.ExistsAsync("http://localhost:4200", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await Service(requireHttps: false)
            .AddAsync(new CreateAllowedCorsOriginRequest("http://localhost:4200", null), CancellationToken.None);

        result.OriginUrl.Should().Be("http://localhost:4200");
    }

    [Fact]
    public async Task AddAsync_duplicate_origin_throws_without_persisting()
    {
        _repository.Setup(x => x.ExistsAsync("https://portal.example.com", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => Service().AddAsync(new CreateAllowedCorsOriginRequest("https://portal.example.com", null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repository.Verify(x => x.AddAsync(It.IsAny<AllowedCorsOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_existing_origin_deletes_and_invalidates_cache()
    {
        var origin = new AllowedCorsOrigin("https://portal.example.com", null);
        _repository.Setup(x => x.GetByIdAsync(origin.Id, It.IsAny<CancellationToken>())).ReturnsAsync(origin);

        await Service().DeleteAsync(origin.Id, CancellationToken.None);

        _repository.Verify(x => x.DeleteAsync(origin, It.IsAny<CancellationToken>()), Times.Once);
        _cache.Verify(x => x.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_nonexistent_origin_throws_not_found()
    {
        _repository.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((AllowedCorsOrigin?)null);

        var act = () => Service().DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
