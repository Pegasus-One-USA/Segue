using System.Net;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Auth;

/// <summary>
/// The availability classifier is the only component that can tell "the vendor said no" from "the vendor said
/// nothing", so its status mapping is the load-bearing part. The 4xx cases matter most: classifying a 401/404 as
/// Down would block workflows that authenticate perfectly well today.
/// </summary>
public sealed class HttpSourceAvailabilityProbeTests
{
    private const string BaseUrl = "https://fhir.example.com";

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_server_that_answers_at_all_is_Up(HttpStatusCode status)
    {
        var probe = ProbeReturning(new HttpResponseMessage(status));

        var result = await probe.CheckAsync(BaseUrl, CancellationToken.None);

        result.Availability.Should().Be(
            SourceAvailability.Up,
            "a server that processed the request is running, so a later auth failure is a real credentials problem");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_server_error_is_Down(HttpStatusCode status)
    {
        var probe = ProbeReturning(new HttpResponseMessage(status));

        var result = await probe.CheckAsync(BaseUrl, CancellationToken.None);

        result.Availability.Should().Be(SourceAvailability.Down);
        result.Detail.Should().Contain(((int)status).ToString());
    }

    [Fact]
    public async Task A_transport_failure_is_Down_and_quotes_the_reason()
    {
        var probe = ProbeThrowing(new HttpRequestException(
            "An error occurred while sending the request.",
            new HttpRequestException("No such host is known. (fhir.example.com:443)")));

        var result = await probe.CheckAsync(BaseUrl, CancellationToken.None);

        result.Availability.Should().Be(SourceAvailability.Down);
        result.Detail.Should().Contain("No such host is known");
    }

    [Fact]
    public async Task An_open_circuit_breaker_is_Down()
    {
        var probe = ProbeThrowing(new BrokenCircuitException("The circuit is now open and is not allowing calls."));

        var result = await probe.CheckAsync(BaseUrl, CancellationToken.None);

        result.Availability.Should().Be(SourceAvailability.Down);
    }

    /// <summary>
    /// The fail-open contract: anything the probe cannot positively establish must be Unknown, because a wrong
    /// Down verdict cancels a run that would have worked.
    /// </summary>
    [Fact]
    public async Task An_unanticipated_failure_is_Unknown_not_Down()
    {
        var probe = ProbeThrowing(new InvalidOperationException("something entirely unexpected"));

        var result = await probe.CheckAsync(BaseUrl, CancellationToken.None);

        result.Availability.Should().Be(SourceAvailability.Unknown);
        result.IsDown.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://fhir.example.com")]
    public async Task An_unprobeable_base_url_is_Unknown_not_Down(string? baseUrl)
    {
        var probe = ProbeThrowing(new InvalidOperationException("should never be called"));

        var result = await probe.CheckAsync(baseUrl, CancellationToken.None);

        result.Availability.Should().Be(SourceAvailability.Unknown);
    }

    [Fact]
    public async Task A_run_cancellation_is_Unknown_rather_than_being_reported_as_an_outage()
    {
        var probe = ProbeThrowing(new TaskCanceledException());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await probe.CheckAsync(BaseUrl, cancelled.Token);

        result.Availability.Should().Be(SourceAvailability.Unknown);
    }

    [Fact]
    public async Task It_probes_the_metadata_endpoint_without_a_bearer_token()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var probe = new HttpSourceAvailabilityProbe(new HttpClient(handler));

        await probe.CheckAsync("https://fhir.example.com/", CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://fhir.example.com/metadata");
        handler.LastRequest.Headers.Authorization.Should().BeNull(
            "/metadata is unauthenticated per SMART App Launch — the probe must not need the token it protects");
    }

    private static HttpSourceAvailabilityProbe ProbeReturning(HttpResponseMessage response) =>
        new(new HttpClient(new CapturingHandler(response)));

    private static HttpSourceAvailabilityProbe ProbeThrowing(Exception exception) =>
        new(new HttpClient(new ThrowingHandler(exception)));

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CapturingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }

    /// <summary>
    /// Stands in for Polly's own BrokenCircuitException. The probe matches an open circuit by type NAME so the
    /// Runtime.Infrastructure assembly needs no direct Polly reference — this fake proves that matching works.
    /// </summary>
    private sealed class BrokenCircuitException : Exception
    {
        public BrokenCircuitException(string message) : base(message)
        {
        }
    }
}
