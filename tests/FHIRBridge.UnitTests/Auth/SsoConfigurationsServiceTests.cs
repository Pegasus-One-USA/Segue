using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Auth;

public sealed class SsoConfigurationsServiceTests
{
    private readonly Mock<ISystemSettingsCache> _settingsCache = new();
    private readonly Mock<ISystemSettingsService> _systemSettingsService = new();
    private readonly SamlAuthenticationOptions _samlOptions = new()
    {
        Enabled = false,
        ServiceProviderEntityId = "https://fhirbridge.example.org/saml/sp",
        IdentityProviderEntityId = "https://idp.example.org/saml",
        SingleSignOnUrl = "https://idp.example.org/saml/sso",
        IdentityProviderCertificate = "cert-base64",
        PortalRedirectUrl = "https://fhirbridge.example.org/dashboard",
        PortalErrorRedirectUrl = "https://fhirbridge.example.org/auth/login",
    };
    private readonly LocalAuthOptions _localAuthOptions = new();
    private readonly EntraAuthenticationOptions _entraOptions = new() { Enabled = true };
    private readonly GoogleAuthenticationOptions _googleOptions = new() { Enabled = false };

    private SsoConfigurationsService Service()
    {
        // No DB rows saved yet — every lookup falls straight through to the appsettings-derived default,
        // exactly like a fresh deployment that hasn't touched the SSO Configurations screen.
        _settingsCache.Setup(x => x.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, bool defaultValue, CancellationToken _) => defaultValue);
        _settingsCache.Setup(x => x.GetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string defaultValue, CancellationToken _) => defaultValue);

        return new SsoConfigurationsService(
            _settingsCache.Object,
            _systemSettingsService.Object,
            Options.Create(_samlOptions),
            Options.Create(_localAuthOptions),
            Options.Create(_entraOptions),
            Options.Create(_googleOptions));
    }

    [Fact]
    public async Task GetAsync_no_saved_overrides_falls_back_to_appsettings_values()
    {
        var dto = await Service().GetAsync(CancellationToken.None);

        dto.SamlEnabled.Should().BeFalse();
        dto.ServiceProviderEntityId.Should().Be(_samlOptions.ServiceProviderEntityId);
        dto.IdentityProviderEntityId.Should().Be(_samlOptions.IdentityProviderEntityId);
        dto.SingleSignOnUrl.Should().Be(_samlOptions.SingleSignOnUrl);
        dto.IdentityProviderCertificate.Should().Be(_samlOptions.IdentityProviderCertificate);
        dto.PortalRedirectUrl.Should().Be(_samlOptions.PortalRedirectUrl);
        dto.PortalErrorRedirectUrl.Should().Be(_samlOptions.PortalErrorRedirectUrl);
        dto.MagicLinkEnabled.Should().BeFalse();
        dto.EntraEnabled.Should().BeTrue();
        dto.GoogleEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_persists_every_field_as_a_system_setting_and_returns_fresh_state()
    {
        _systemSettingsService
            .Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, string value, string? description, CancellationToken _) =>
                new SystemSettingDto(Guid.NewGuid(), key, value, description, DateTime.UtcNow, DateTime.UtcNow, null, null));

        var request = new UpdateSsoConfigurationsRequest(
            SamlEnabled: true,
            ServiceProviderEntityId: "https://segue.example.org/saml/sp",
            IdentityProviderEntityId: "https://idp.hospital.example.org/saml",
            SingleSignOnUrl: "https://idp.hospital.example.org/saml/sso",
            IdentityProviderCertificate: "new-cert-base64",
            PortalRedirectUrl: "https://segue.example.org/dashboard",
            PortalErrorRedirectUrl: "https://segue.example.org/auth/login",
            MagicLinkEnabled: true);

        await Service().UpdateAsync(request, CancellationToken.None);

        _systemSettingsService.Verify(x => x.SetAsync(
            "Authentication:Saml:Enabled", "True", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _systemSettingsService.Verify(x => x.SetAsync(
            "Authentication:Saml:ServiceProviderEntityId", request.ServiceProviderEntityId, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _systemSettingsService.Verify(x => x.SetAsync(
            "Authentication:Saml:IdentityProviderEntityId", request.IdentityProviderEntityId, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _systemSettingsService.Verify(x => x.SetAsync(
            "Authentication:Saml:SingleSignOnUrl", request.SingleSignOnUrl, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _systemSettingsService.Verify(x => x.SetAsync(
            "Authentication:Saml:IdentityProviderCertificate", request.IdentityProviderCertificate, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _systemSettingsService.Verify(x => x.SetAsync(
            "Authentication:Saml:PortalRedirectUrl", request.PortalRedirectUrl, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _systemSettingsService.Verify(x => x.SetAsync(
            "Authentication:Saml:PortalErrorRedirectUrl", request.PortalErrorRedirectUrl, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _systemSettingsService.Verify(x => x.SetAsync(
            "LocalAuth:MagicLink:Enabled", "True", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
