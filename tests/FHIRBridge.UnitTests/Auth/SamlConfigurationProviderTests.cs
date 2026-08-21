using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Security;
using FHIRBridge.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Auth;

public sealed class SamlConfigurationProviderTests
{
    // A throwaway self-signed certificate (base64-encoded DER), just to exercise cert loading —
    // not meant to represent a real IdP.
    private const string SelfSignedCertificateBase64 =
        "MIICpjCCAY6gAwIBAgIIZKHncLFn41IwDQYJKoZIhvcNAQELBQAwEzERMA8GA1UEAxMIdGVzdC1pZHAwHhcNMjYwODE3MTIwMjU1WhcNMzEwODE4MTIwMjU1WjATMREwDwYDVQQDEwh0ZXN0LWlkcDCCASIwDQYJKoZIhvcNAQEBBQADggEPADCCAQoCggEBAOIX9nPz9g84+2lDkNsPzHWhkq67HI7Nfz9Uxo2r//poWUUW5hwHgpmSk4OBKyMP+dYY4ScDdKEV4HgD3bBxIgF3R7TWc/CeiMG663iiR4os++/5RjtEGQtF4JhuPyCLwCvYGV3pbr5+YR3KY0gKlMoGNT6oCN5scGqpSc3WENSb7okp4PUkLZ/yA1LjpwkJHVR1NhN/0VnM+JOYbn4vY9FTcJuvBLI9w0CCPih3galMmVBRkuSsahJpdFuCYGTKiUWBrNMT5jaS4cKnp+6xB3DhrgDMFUV0DoLR45ftdg8zgCIYFbUPQkNWyLVEZStIADTAerIumq/i1v9FfgZ9TfUCAwEAATANBgkqhkiG9w0BAQsFAAOCAQEAhO6dJA5LO5X+JF/BWFUKw3U9ge0zuTYXDNEPEcU1Ec4kNHit28Usuv/vMTpDIHZkVbAPBSfUT+wkyKa0pOfOM4tqVA0JFpe+R0iopySjsG75ieXSCSny2AMNtQZ6IN2dAhmiMghQ/J+NeWOIPsMx0r5HAlfWq6bSCqchcl/IlN+NNESHNSikD55vlj765/0/1Aj3cHHpBCQRCsYeGClyVJ0f+689PJDFDFIsXkbDmXsk2Ue74yAAVYRuEBnSL5ovkyD9noM8fMVurVCqreC6CXveV3CmSN48BfII7E+HiNBblQyx4J1NDhOeouf+uAIZJQnmiGGXMmeJ0WfqbqoemA==";

    private static SamlAuthenticationOptions ValidOptions() => new()
    {
        Enabled = true,
        ServiceProviderEntityId = "https://fhirbridge.example.org/saml/sp",
        IdentityProviderEntityId = "https://idp.hospital.example.org/saml",
        SingleSignOnUrl = "https://idp.hospital.example.org/saml/sso",
        IdentityProviderCertificate = SelfSignedCertificateBase64,
    };

    // The provider reads every field via ISystemSettingsCache with the appsettings-derived value as the
    // fallback default — this pass-through mock has no DB rows, so every call resolves straight to
    // whatever ValidOptions() (or a mutated copy) supplied as the default, same as production behaves
    // when no admin override has been saved from the SSO Configurations screen yet.
    private static Mock<ISystemSettingsCache> PassThroughSettingsCache()
    {
        var mock = new Mock<ISystemSettingsCache>();
        mock.Setup(x => x.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, bool defaultValue, CancellationToken _) => defaultValue);
        mock.Setup(x => x.GetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string defaultValue, CancellationToken _) => defaultValue);
        return mock;
    }

    private static SamlConfigurationProvider Provider(SamlAuthenticationOptions options) =>
        new(Options.Create(options), PassThroughSettingsCache().Object);

    [Fact]
    public async Task GetConfigurationAsync_disabled_throws()
    {
        var options = ValidOptions();
        options.Enabled = false;

        var act = () => Provider(options).GetConfigurationAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(nameof(SamlAuthenticationOptions.ServiceProviderEntityId))]
    [InlineData(nameof(SamlAuthenticationOptions.SingleSignOnUrl))]
    [InlineData(nameof(SamlAuthenticationOptions.IdentityProviderCertificate))]
    public async Task GetConfigurationAsync_missing_required_field_throws(string missingField)
    {
        var options = ValidOptions();
        typeof(SamlAuthenticationOptions).GetProperty(missingField)!.SetValue(options, null);

        var act = () => Provider(options).GetConfigurationAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetConfigurationAsync_valid_options_builds_expected_configuration()
    {
        var options = ValidOptions();

        var config = await Provider(options).GetConfigurationAsync(CancellationToken.None);

        config.Issuer.Should().Be(options.ServiceProviderEntityId);
        config.SingleSignOnDestination.Should().Be(new Uri(options.SingleSignOnUrl!));
        config.AllowedIssuer.Should().Be(options.IdentityProviderEntityId);
        config.AllowedAudienceUris.Should().Contain(options.ServiceProviderEntityId);
        config.SignatureValidationCertificates.Should().ContainSingle();
        config.SignAuthnRequest.Should().BeFalse();
    }
}
