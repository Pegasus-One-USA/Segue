using System.Net;
using System.Text;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Connectors;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Connectors;

/// <summary>
/// eCW answers an Observation search per category value, so a category the connector never asks for is silently
/// absent from the run — no error, just a short count. The category list originally named only vitals/labs/
/// social-history, which is why a live eCW Single Patient run extracted 30 Observations where the verified
/// proof-of-concept extracted 55: the missing 25 were all <c>survey</c> (disability status, PRAPARE scores, SDOH
/// screening answers).
/// </summary>
public sealed class EClinicalWorksObservationCategoryTests
{
    private static readonly FhirSourceConfiguration Source = new(
        RuntimeSourceType.Healow, "eCW", "https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD",
        "https://auth/token", "client-1", null, null, [], SearchCount: 100, MaxPages: 1);

    // The seven the proof-of-concept probes and a live Provider EMR run confirmed answer 200.
    private static readonly string[] ExpectedCategories =
        ["laboratory", "vital-signs", "social-history", "survey", "exam", "imaging", "sdoh"];

    [Fact]
    public async Task Observation_search_requests_every_category_ecw_serves()
    {
        var handler = new RecordingHandler();
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(handler), new StubTokenProvider());

        await client.SearchAsync("Observation", Source, CancellationToken.None);

        var requested = handler.RequestUris
            .Select(GetCategoryValue)
            .Where(c => c is not null)
            .ToArray();

        requested.Should().BeEquivalentTo(ExpectedCategories);
    }

    // The regression guard proper: 'survey' is what the live run was missing.
    [Fact]
    public async Task Observation_search_requests_the_survey_category()
    {
        var handler = new RecordingHandler();
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(handler), new StubTokenProvider());

        await client.SearchAsync("Observation", Source, CancellationToken.None);

        handler.RequestUris.Select(GetCategoryValue).Should().Contain("survey");
    }

    // Each category is a separate request whose results are merged and deduped by resource id, so a resource eCW
    // dual-tags (survey + sdoh) must be counted once, not twice.
    [Fact]
    public async Task Resources_returned_under_two_categories_are_deduped()
    {
        var handler = new RecordingHandler(sharedResourceId: "obs-dual-tagged");
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(handler), new StubTokenProvider());

        var resources = await client.SearchAsync("Observation", Source, CancellationToken.None);

        handler.RequestUris.Should().HaveCount(ExpectedCategories.Length);
        resources.Should().ContainSingle().Which.ResourceId.Should().Be("obs-dual-tagged");
    }

    private static string? GetCategoryValue(string uri)
    {
        var marker = "category=";
        var index = uri.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;

        var value = uri[(index + marker.Length)..];
        var end = value.IndexOf('&');
        return end < 0 ? value : value[..end];
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string? _sharedResourceId;

        public RecordingHandler(string? sharedResourceId = null) => _sharedResourceId = sharedResourceId;

        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.ToString());

            // Every category answers 200 — as eCW does — so nothing is skipped by the connector's
            // one-bad-value-doesn't-fail-the-rest guard and the assertions reflect the real category list.
            var body = _sharedResourceId is null
                ? "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"entry\":[]}"
                : "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"entry\":[{\"resource\":" +
                  "{\"resourceType\":\"Observation\",\"id\":\"" + _sharedResourceId +
                  "\",\"status\":\"final\"}}]}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/fhir+json"),
            });
        }
    }

    private sealed class StubTokenProvider : IFhirAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult("token");
    }
}
