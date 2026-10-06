using System.Net;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Connectors;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.UnitTests.Connectors;

/// <summary>The wire diagnostics (Runtime:Epic:Diagnostics) are off by default, log the request / response when on, and
/// never write a bearer token.</summary>
public sealed class FhirHttpDiagnosticsTests
{
    private static readonly FhirSourceConfiguration Source = new(
        RuntimeSourceType.Healow, "eCW", "https://fhir.example.com/R4", "https://auth/token", "client-1",
        null, null, [], SearchCount: 100, MaxPages: 1,
        PatientSearchCriteria: "name=brown");

    private static async Task<List<string>> RunAsync(bool enabled, bool includeValues = true)
    {
        var logger = new ListLogger<EClinicalWorksFhirSourceClient>();
        var options = Options.Create(new EpicFhirClientOptions
        {
            Diagnostics = { Enabled = enabled, IncludeQueryValues = includeValues },
        });
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(new StubHandler()), new Tokens(), options, logger);

        await client.SearchAsync("Patient", Source, CancellationToken.None);
        return logger.Lines.Where(l => l.Contains("[FHIR-DIAG]")).ToList();
    }

    [Fact]
    public async Task Nothing_is_logged_unless_diagnostics_are_enabled() =>
        (await RunAsync(enabled: false)).Should().BeEmpty();

    [Fact]
    public async Task Enabled_logs_scope_request_and_response_without_the_bearer_token()
    {
        var lines = await RunAsync(enabled: true);

        lines.Should().Contain(l => l.Contains("SCOPE Patient") && l.Contains("patientSearchCriteria=True") && l.Contains("finalParameterNames=[name]"));
        lines.Should().Contain(l => l.Contains("REQUEST") && l.Contains("query: name=brown") && l.Contains("Authorization: ***"));
        lines.Should().Contain(l => l.Contains("RESPONSE 200") && l.Contains("\"resourceType\": \"Bundle\""));
        lines.Should().NotContain(l => l.Contains("secret-access-token"));
    }

    [Fact]
    public async Task Query_values_can_be_withheld()
    {
        var lines = await RunAsync(enabled: true, includeValues: false);

        lines.Should().Contain(l => l.Contains("query: name=<withheld>"));
        lines.Should().NotContain(l => l.Contains("brown"));
    }

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
