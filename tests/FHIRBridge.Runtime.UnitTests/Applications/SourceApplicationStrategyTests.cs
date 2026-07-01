using System.Net;
using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Applications;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Applications;

public sealed class SourceApplicationStrategyTests
{
    private static EpicAccessTokenProvider Epic() =>
        new(new HttpClient(new NoopHandler()), new FakeJwtFactory());

    private static SmartAuthorizationCodeTokenProvider Interactive() =>
        new(new HttpClient(new NoopHandler()), new InMemoryFhirAuthorizationCodeTokenStore());

    private static ISourceApplicationStrategy[] AllStrategies() =>
    [
        new BackendServicesApplicationStrategy(Epic()),
        new EhrLaunchApplicationStrategy(Interactive()),
        new StandaloneApplicationStrategy(Interactive()),
        new PatientApplicationStrategy(Interactive())
    ];

    private static SourceApplicationStrategyRegistry Registry() => new(AllStrategies());

    [Theory]
    [InlineData(ApplicationType.Backend)]
    [InlineData(ApplicationType.EhrLaunch)]
    [InlineData(ApplicationType.Standalone)]
    [InlineData(ApplicationType.Patient)]
    public void Registry_resolves_each_application_type_to_the_owning_strategy(ApplicationType type)
    {
        Registry().Resolve(type).Handles.Should().Be(type);
    }

    [Fact]
    public void Registry_throws_for_an_unregistered_application_type()
    {
        var act = () => Registry().Resolve((ApplicationType)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Registry_rejects_two_strategies_for_the_same_type()
    {
        var act = () => new SourceApplicationStrategyRegistry(
            [new BackendServicesApplicationStrategy(Epic()), new BackendServicesApplicationStrategy(Epic())]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Backend_describes_a_non_interactive_client_credentials_flow()
    {
        var descriptor = new BackendServicesApplicationStrategy(Epic()).Describe();

        descriptor.OAuthFlow.Should().Be(SmartOAuthFlows.ClientCredentials);
        descriptor.ScopePrefix.Should().Be(SmartScopePrefixes.System);
        descriptor.IsInteractive.Should().BeFalse();
        descriptor.RequiresPkce.Should().BeFalse();
        descriptor.RequiresRedirectUri.Should().BeFalse();
        descriptor.SupportsRefreshToken.Should().BeFalse();
    }

    [Theory]
    [InlineData(ApplicationType.EhrLaunch, SmartScopePrefixes.User)]
    [InlineData(ApplicationType.Standalone, SmartScopePrefixes.User)]
    [InlineData(ApplicationType.Patient, SmartScopePrefixes.Patient)]
    public void Interactive_types_describe_authorization_code_with_pkce(ApplicationType type, string expectedPrefix)
    {
        var descriptor = Registry().Resolve(type).Describe();

        descriptor.OAuthFlow.Should().Be(SmartOAuthFlows.AuthorizationCode);
        descriptor.ScopePrefix.Should().Be(expectedPrefix);
        descriptor.IsInteractive.Should().BeTrue();
        descriptor.RequiresPkce.Should().BeTrue();
        descriptor.RequiresRedirectUri.Should().BeTrue();
        descriptor.SupportsRefreshToken.Should().BeTrue();
    }

    [Fact]
    public void Only_ehr_launch_requires_a_launch_token_and_trusted_issuer_allow_list()
    {
        new EhrLaunchApplicationStrategy(Interactive()).Describe().RequiresLaunchToken.Should().BeTrue();
        new EhrLaunchApplicationStrategy(Interactive()).Describe().RequiresTrustedIssuerAllowList.Should().BeTrue();

        new StandaloneApplicationStrategy(Interactive()).Describe().RequiresLaunchToken.Should().BeFalse();
        new PatientApplicationStrategy(Interactive()).Describe().RequiresTrustedIssuerAllowList.Should().BeFalse();
        new BackendServicesApplicationStrategy(Epic()).Describe().RequiresLaunchToken.Should().BeFalse();
    }

    [Fact]
    public void Backend_validation_passes_with_a_key_client_id_token_endpoint_and_base_url()
    {
        new BackendServicesApplicationStrategy(Epic()).Validate(BackendConfig()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Backend_validation_fails_without_a_signing_key()
    {
        var result = new BackendServicesApplicationStrategy(Epic())
            .Validate(BackendConfig() with { PrivateKeyPem = null });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("private key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Standalone_validation_fails_without_an_authorization_endpoint()
    {
        var result = new StandaloneApplicationStrategy(Interactive())
            .Validate(InteractiveConfig() with { AuthorizationEndpoint = null });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("authorization endpoint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Interactive_validation_passes_when_client_id_and_both_endpoints_are_present()
    {
        var source = InteractiveConfig();

        new StandaloneApplicationStrategy(Interactive()).Validate(source).IsValid.Should().BeTrue();
        new EhrLaunchApplicationStrategy(Interactive()).Validate(source).IsValid.Should().BeTrue();
        new PatientApplicationStrategy(Interactive()).Validate(source).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validation_reports_a_missing_base_url()
    {
        var result = new StandaloneApplicationStrategy(Interactive())
            .Validate(InteractiveConfig() with { BaseUrl = null });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("FHIR base URL", StringComparison.OrdinalIgnoreCase));
    }

    private static FhirSourceConfiguration BackendConfig() => new(
        RuntimeSourceType.Epic,
        "Epic Backend",
        "https://fhir.example.com",
        "https://auth.example.com/token",
        "client-id",
        "key-1",
        "-----BEGIN PRIVATE KEY-----abc-----END PRIVATE KEY-----",
        ["system/Patient.read"],
        ApplicationType: ApplicationType.Backend);

    private static FhirSourceConfiguration InteractiveConfig() => new(
        RuntimeSourceType.Epic,
        "Epic Standalone",
        "https://fhir.example.com",
        "https://auth.example.com/token",
        "client-id",
        null,
        null,
        ["user/Patient.read"],
        AuthorizationEndpoint: "https://auth.example.com/authorize",
        ApplicationType: ApplicationType.Standalone);

    private sealed class FakeJwtFactory : IBackendServicesJwtFactory
    {
        public string CreateClientAssertion(BackendServicesJwtRequest request) => "fake-assertion";
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
