using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Aggregates;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Sources;

public sealed class SourceConnectionApplicationTypeMappingTests
{
    private static readonly SourceAuthenticationConfiguration Auth =
        new(AuthenticationType.OAuthClientCredentials, "client-1", null, ["user/Patient.read"], null, null, null);

    [Fact]
    public void ToDto_carries_the_application_type_and_interactive_configuration()
    {
        var tenant = new Tenant("Contoso Health", "contoso");
        var interactive = new SourceInteractiveConfiguration(
            ["https://app.example.com/callback"], "https://app.example.com/launch", ["https://ehr.example.com/fhir"]);
        var source = tenant.AddSourceConnection(
            "Epic Standalone", SourceSystemType.Epic, "https://fhir.example.com", Auth,
            ApplicationType.Standalone, interactive);

        var dto = TenantConfigurationMapper.ToDto(source);

        dto.ApplicationType.Should().Be(ApplicationType.Standalone);
        dto.Interactive.Should().NotBeNull();
        dto.Interactive!.RedirectUris.Should().ContainSingle().Which.Should().Be("https://app.example.com/callback");
        dto.Interactive.LaunchUrl.Should().Be("https://app.example.com/launch");
        dto.Interactive.TrustedIssuers.Should().ContainSingle().Which.Should().Be("https://ehr.example.com/fhir");
    }

    [Fact]
    public void Backend_source_leaves_application_type_null_and_no_interactive_config()
    {
        var tenant = new Tenant("Contoso Health", "contoso");
        var source = tenant.AddSourceConnection("Epic Backend", SourceSystemType.Epic, "https://fhir.example.com", Auth);

        var dto = TenantConfigurationMapper.ToDto(source);

        dto.ApplicationType.Should().BeNull();
        dto.Interactive.Should().BeNull();
    }

    [Fact]
    public void ToDomain_round_trips_the_interactive_configuration_from_the_dto()
    {
        var dto = new SourceInteractiveConfigurationDto(
            ["https://a/cb", "https://b/cb"], "https://a/launch", ["https://iss1", "https://iss2"]);

        var domain = TenantConfigurationMapper.ToDomain(dto);

        domain.Should().NotBeNull();
        domain!.RedirectUris.Should().BeEquivalentTo("https://a/cb", "https://b/cb");
        domain.TrustedIssuers.Should().BeEquivalentTo("https://iss1", "https://iss2");
        domain.LaunchUrl.Should().Be("https://a/launch");
    }
}
