using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Auth;

/// <summary>
/// The preflight decorator's whole job is choosing between two stories for the same failure: "the source is down"
/// and "your credentials are wrong". These tests pin both directions, plus the fail-open rule that stops a wrong
/// verdict from cancelling a run that would have worked.
/// </summary>
public sealed class PreflightFhirAccessTokenProviderTests
{
    private static FhirSourceConfiguration EpicSource() => new(
        RuntimeSourceType.Epic,
        "Epic Production",
        "https://fhir.example.com",
        TokenEndpoint: "https://auth.example.com/token",
        "client-1",
        null,
        null,
        [],
        SourceConnectionId: Guid.NewGuid(),
        ApplicationType: ApplicationType.Backend);

    [Fact]
    public async Task A_down_source_fails_before_a_token_is_ever_requested()
    {
        var inner = new FakeTokenProvider("should-never-be-reached");
        var sut = new PreflightFhirAccessTokenProvider(
            inner,
            new StubProbe(SourceAvailabilityResult.Down("Could not connect: connection refused.")));

        var act = () => sut.GetAccessTokenAsync(EpicSource(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<SourceUnavailableException>();
        exception.Which.UserMessage.Should().Contain("not reachable");
        exception.Which.UserMessage.Should().Contain("credentials on this connection are not the cause");
        inner.CallCount.Should().Be(0, "there is no point authenticating against a host that isn't answering");
    }

    /// <summary>
    /// The reported bug: the vendor's edge answers normally, so the pre-check passes, but the token exchange comes
    /// back with a bare invalid_client because the service behind the edge is degraded. The re-check is what turns
    /// that into an outage message.
    /// </summary>
    [Fact]
    public async Task An_invalid_client_failure_is_re_reported_as_an_outage_when_the_source_has_gone_down()
    {
        var probe = new SequencedProbe(
            SourceAvailabilityResult.Up("The FHIR endpoint answered with HTTP 200."),
            SourceAvailabilityResult.Down("The FHIR endpoint returned HTTP 503."));
        var inner = new FakeTokenProvider(
            TokenEndpointException.FromResponse("Epic", 400, "Bad Request", """{"error":"invalid_client"}"""));
        var sut = new PreflightFhirAccessTokenProvider(inner, probe);

        var act = () => sut.GetAccessTokenAsync(EpicSource(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<SourceUnavailableException>();
        exception.Which.Detail.Should().Contain("503");
        exception.Which.InnerException.Should().BeOfType<TokenEndpointException>(
            "the original token failure stays attached for the log/ErrorLog trail");
        probe.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task An_invalid_client_failure_against_a_healthy_source_keeps_its_credentials_diagnosis()
    {
        var probe = new StubProbe(SourceAvailabilityResult.Up("The FHIR endpoint answered with HTTP 200."));
        var inner = new FakeTokenProvider(
            TokenEndpointException.FromResponse("Epic", 400, "Bad Request", """{"error":"invalid_client"}"""));
        var sut = new PreflightFhirAccessTokenProvider(inner, probe);

        var act = () => sut.GetAccessTokenAsync(EpicSource(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<TokenEndpointException>();
        exception.Which.UserMessage.Should().Contain("invalid_client");
        exception.Which.UserMessage.Should().Contain("client ID");
    }

    /// <summary>
    /// The guardrail. An Unknown verdict must not block: a probe that failed to measure has no business cancelling
    /// work that would have succeeded.
    /// </summary>
    [Fact]
    public async Task An_unknown_verdict_proceeds_to_the_real_call()
    {
        var inner = new FakeTokenProvider("real-token");
        var sut = new PreflightFhirAccessTokenProvider(
            inner,
            new StubProbe(SourceAvailabilityResult.Unknown("The availability probe could not complete.")));

        var token = await sut.GetAccessTokenAsync(EpicSource(), CancellationToken.None);

        token.Should().Be("real-token");
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_healthy_source_is_unaffected()
    {
        var inner = new FakeTokenProvider("real-token");
        var sut = new PreflightFhirAccessTokenProvider(
            inner,
            new StubProbe(SourceAvailabilityResult.Up("The FHIR endpoint answered with HTTP 200.")));

        var token = await sut.GetAccessTokenAsync(EpicSource(), CancellationToken.None);

        token.Should().Be("real-token");
    }

    [Fact]
    public async Task A_cancelled_run_is_not_relabelled_as_an_outage()
    {
        var probe = new SequencedProbe(
            SourceAvailabilityResult.Up("up"),
            SourceAvailabilityResult.Down("down"));
        var inner = new FakeTokenProvider(new OperationCanceledException());
        var sut = new PreflightFhirAccessTokenProvider(inner, probe);

        var act = () => sut.GetAccessTokenAsync(EpicSource(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        probe.CallCount.Should().Be(1, "a cancellation must not trigger the after-failure re-check");
    }

    /// <summary>
    /// Callers feature-detect the registered provider by pattern-matching on IFhirPatientContextProvider /
    /// IFhirGrantedScopeProvider (SourceConnectionRuntimeResolver for an interactive launch's resolved base URL,
    /// FhirSourceConnectorBase for patient context, SourceNodeExecutors for granted scopes). A decorator that
    /// dropped them would silently break interactive launches, so the forwarding is pinned here.
    /// </summary>
    [Fact]
    public async Task It_forwards_patient_context_granted_scope_and_resolved_base_url()
    {
        var inner = new FakeFullProvider();
        var sut = new PreflightFhirAccessTokenProvider(
            inner,
            new StubProbe(SourceAvailabilityResult.Up("up")));

        sut.Should().BeAssignableTo<IFhirPatientContextProvider>();
        sut.Should().BeAssignableTo<IFhirGrantedScopeProvider>();

        (await sut.GetPatientContextAsync(EpicSource(), CancellationToken.None)).Should().Be("patient-1");
        (await sut.GetResolvedBaseUrlAsync(EpicSource(), CancellationToken.None)).Should().Be("https://hospital-a.example.com");
        (await sut.GetGrantedScopeAsync(EpicSource(), CancellationToken.None)).Should().Be("system/Patient.read");

        await sut.DiscardTokenAsync(EpicSource(), CancellationToken.None);
        inner.TokenDiscarded.Should().BeTrue();
    }

    [Fact]
    public async Task Wrapping_a_token_only_provider_degrades_instead_of_throwing()
    {
        var sut = new PreflightFhirAccessTokenProvider(
            new FakeTokenProvider("real-token"),
            new StubProbe(SourceAvailabilityResult.Up("up")));

        (await sut.GetPatientContextAsync(EpicSource(), CancellationToken.None)).Should().BeNull();
        (await sut.GetResolvedBaseUrlAsync(EpicSource(), CancellationToken.None)).Should().BeNull();
        (await sut.GetGrantedScopeAsync(EpicSource(), CancellationToken.None)).Should().BeNull();

        var act = () => sut.DiscardTokenAsync(EpicSource(), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    private sealed class FakeTokenProvider : IFhirAccessTokenProvider
    {
        private readonly string? _token;
        private readonly Exception? _exception;

        public FakeTokenProvider(string token)
        {
            _token = token;
        }

        public FakeTokenProvider(Exception exception)
        {
            _exception = exception;
        }

        public int CallCount { get; private set; }

        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
        {
            CallCount++;
            return _exception is not null
                ? throw _exception
                : Task.FromResult(_token!);
        }
    }

    private sealed class FakeFullProvider : IFhirAccessTokenProvider, IFhirPatientContextProvider, IFhirGrantedScopeProvider
    {
        public bool TokenDiscarded { get; private set; }

        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult("real-token");

        public Task<string?> GetPatientContextAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("patient-1");

        public Task<string?> GetResolvedBaseUrlAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("https://hospital-a.example.com");

        public Task DiscardTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
        {
            TokenDiscarded = true;
            return Task.CompletedTask;
        }

        public Task<string?> GetGrantedScopeAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("system/Patient.read");
    }

    private sealed class StubProbe : ISourceAvailabilityProbe
    {
        private readonly SourceAvailabilityResult _result;

        public StubProbe(SourceAvailabilityResult result)
        {
            _result = result;
        }

        public Task<SourceAvailabilityResult> CheckAsync(string? baseUrl, CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }

    /// <summary>Returns a different verdict per call, so the before-check and after-check can be told apart.</summary>
    private sealed class SequencedProbe : ISourceAvailabilityProbe
    {
        private readonly SourceAvailabilityResult[] _results;

        public SequencedProbe(params SourceAvailabilityResult[] results)
        {
            _results = results;
        }

        public int CallCount { get; private set; }

        public Task<SourceAvailabilityResult> CheckAsync(string? baseUrl, CancellationToken cancellationToken)
        {
            var index = Math.Min(CallCount, _results.Length - 1);
            CallCount++;
            return Task.FromResult(_results[index]);
        }
    }
}
