using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace FHIRBridge.UnitTests.SystemSettings;

public sealed class OAuthPublicOriginResolverTests
{
    private readonly Mock<ISystemSettingsCache> _settingsCache = new();

    private OAuthPublicOriginResolver Resolver(string? configuredOAuthPublicBaseUrl = null) =>
        new(_settingsCache.Object, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OAuth:PublicBaseUrl"] = configuredOAuthPublicBaseUrl,
        }).Build());

    [Fact]
    public async Task ResolveAsync_uses_the_DB_backed_setting_when_present()
    {
        _settingsCache
            .Setup(x => x.GetStringAsync("OAuth:PublicBaseUrl", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://seguedemo.pegasusone.com");

        var origin = await Resolver().ResolveAsync("http://127.0.0.1:5000", CancellationToken.None);

        origin.Should().Be("https://seguedemo.pegasusone.com");
    }

    [Fact]
    public async Task ResolveAsync_trims_a_trailing_slash_off_the_configured_value()
    {
        _settingsCache
            .Setup(x => x.GetStringAsync("OAuth:PublicBaseUrl", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://seguedemo.pegasusone.com/");

        var origin = await Resolver().ResolveAsync("http://127.0.0.1:5000", CancellationToken.None);

        origin.Should().Be("https://seguedemo.pegasusone.com");
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_the_request_derived_origin_when_the_setting_is_blank()
    {
        // ISystemSettingsCache mimics the real cache's own contract: it returns whatever default the caller
        // passed when the DB has no override AND the fallback given to it (the config value) is itself blank.
        _settingsCache
            .Setup(x => x.GetStringAsync("OAuth:PublicBaseUrl", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string defaultValue, CancellationToken _) => defaultValue);

        var origin = await Resolver(configuredOAuthPublicBaseUrl: null).ResolveAsync(
            "https://seguedemo.pegasusone.com", CancellationToken.None);

        origin.Should().Be("https://seguedemo.pegasusone.com");
    }

    [Fact]
    public async Task ResolveAsync_passes_the_configured_appsettings_value_as_the_caches_own_fallback()
    {
        // Confirms the precedence chain end-to-end: DB override > OAuth:PublicBaseUrl config/env value > the
        // request's own origin — by asserting exactly what gets handed to the cache as ITS fallback.
        string? capturedDefault = null;
        _settingsCache
            .Setup(x => x.GetStringAsync("OAuth:PublicBaseUrl", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, defaultValue, _) => capturedDefault = defaultValue)
            .ReturnsAsync("https://from-db.example.com");

        await Resolver(configuredOAuthPublicBaseUrl: "https://from-config.example.com")
            .ResolveAsync("http://127.0.0.1:5000", CancellationToken.None);

        capturedDefault.Should().Be("https://from-config.example.com");
    }
}
