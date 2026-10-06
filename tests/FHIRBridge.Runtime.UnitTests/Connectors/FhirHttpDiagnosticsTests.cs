using System.Net;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Connectors;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.UnitTests.Connectors;

/// <summary>The TEMP-DEBUG wire diagnostics log exactly one request - GET .../FFBJCD/Patient - in full, and nothing else.</summary>
public sealed class FhirHttpDiagnosticsTests
{
    private const string TargetBaseUrl = "https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD";

    private static FhirSourceConfiguration SourceFor(string baseUrl, string? criteria) => new(
        RuntimeSourceType.Healow, "eCW", baseUrl, "https://auth/token", "client-1",
        null, null, [], SearchCount: 100, MaxPages: 1,
        PatientSearchCriteria: criteria);

    private static async Task<List<string>> RunAsync(string resourceType, string baseUrl = TargetBaseUrl, string? criteria = "name=brown", bool enabled = true)
    {
        var logger = new ListLogger<EClinicalWorksFhirSourceClient>();
        var options = Options.Create(new EpicFhirClientOptions { Diagnostics = { Enabled = enabled } });
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(new StubHandler()), new Tokens(), options, logger);

        await client.SearchAsync(resourceType, SourceFor(baseUrl, criteria), CancellationToken.None);
        return logger.Lines.Where(l => l.Contains("[FHIR-DIAG]")).ToList();
    }

    [Fact]
    public async Task The_target_request_is_logged_in_full_with_its_query_and_response_body()
    {
        var lines = await RunAsync("Patient");

        lines.Should().Contain(l => l.Contains("REQUEST") && l.Contains("/FFBJCD/Patient?name=brown") && l.Contains("query: name=brown"));
        lines.Should().Contain(l => l.Contains("RESPONSE 200") && l.Contains("\"resourceType\": \"Bundle\"") && l.Contains("body (") );
    }

    [Fact]
    public async Task The_bearer_token_is_never_written()
    {
        var lines = await RunAsync("Patient");

        lines.Should().Contain(l => l.Contains("Authorization: ***"));
        lines.Should().NotContain(l => l.Contains("secret-access-token"));
    }

    [Fact]
    public async Task Only_the_request_and_response_lines_exist() =>
        (await RunAsync("Patient")).Should().HaveCount(2);

    [Fact]
    public async Task Any_other_request_logs_nothing()
    {
        (await RunAsync("Observation")).Should().BeEmpty();
        (await RunAsync("Patient", baseUrl: "https://fhir.example.com/R4")).Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_logged_when_disabled() =>
        (await RunAsync("Patient", enabled: false)).Should().BeEmpty();

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "resourceType": "Bundle", "type": "searchset", "entry": [] }"""),
            });
    }

    private sealed class Tokens : IFhirAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult("secret-access-token");
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
