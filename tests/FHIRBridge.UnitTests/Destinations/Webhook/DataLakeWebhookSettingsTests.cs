using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Webhook;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations.Webhook;

public sealed class DataLakeWebhookSettingsTests
{
    private static DestinationConfiguration Destination(string? target, string? connectionMetadataJson) =>
        new("Lake Feed", DestinationType.DataLakeWebhook, new SecretReference("kv", "secret"), target, connectionMetadataJson);

    [Theory]
    [InlineData("""{"dest_dlwAuthMode":"bearer"}""", DataLakeWebhookAuthMode.Bearer)]
    [InlineData("""{"dest_dlwAuthMode":"apiKeyHeader"}""", DataLakeWebhookAuthMode.ApiKeyHeader)]
    [InlineData("""{"dest_dlwAuthMode":"hmacSha256"}""", DataLakeWebhookAuthMode.HmacSha256)]
    [InlineData("""{"dest_dlwAuthMode":"basic"}""", DataLakeWebhookAuthMode.Basic)]
    public void Parse_reads_the_configured_auth_mode(string json, DataLakeWebhookAuthMode expected)
    {
        var settings = DataLakeWebhookSettings.Parse(Destination("https://ingest.example.com/events", json));

        settings.AuthMode.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"dest_dlwAuthMode":"not-a-real-mode"}""")]
    public void Missing_or_unrecognized_auth_mode_defaults_to_None(string? json)
    {
        var settings = DataLakeWebhookSettings.Parse(Destination("https://ingest.example.com/events", json));

        settings.AuthMode.Should().Be(DataLakeWebhookAuthMode.None);
    }

    [Fact]
    public void Defaults_are_lake_friendly_when_nothing_is_configured()
    {
        var settings = DataLakeWebhookSettings.Parse(Destination("https://ingest.example.com/events", "{}"));

        settings.PayloadShape.Should().Be(DataLakeWebhookPayloadShape.Ndjson);
        settings.ContentType.Should().Be("application/x-ndjson");
        settings.HttpMethod.Should().Be("POST");
        settings.BatchSize.Should().Be(DataLakeWebhookSettings.DefaultBatchSize);
        settings.MaxRequestBytes.Should().Be(DataLakeWebhookSettings.DefaultMaxRequestBytes);
        settings.Compression.Should().Be(DataLakeWebhookCompression.None);
        settings.IncludeSourceJson.Should().BeFalse("the raw source resource is far more PHI than mapped values");
        settings.FailureMode.Should().Be(
            DataLakeWebhookFailureMode.Fail, "a silently dropped clinical batch is invisible data loss");
    }

    [Fact]
    public void Endpoint_url_metadata_wins_over_Target()
    {
        var settings = DataLakeWebhookSettings.Parse(Destination(
            "https://from-target.example.com/x",
            """{"dest_dlwEndpointUrl":"https://from-metadata.example.com/y"}"""));

        settings.EndpointUrl.Should().Be("https://from-metadata.example.com/y");
    }

    [Fact]
    public void Falls_back_to_Target_when_endpoint_metadata_is_blank()
    {
        var settings = DataLakeWebhookSettings.Parse(Destination("https://from-target.example.com/x", "{}"));

        settings.EndpointUrl.Should().Be("https://from-target.example.com/x");
        settings.EndpointComesFromSecret.Should().BeFalse();
    }

    [Fact]
    public void A_blank_endpoint_is_allowed_for_auth_mode_none_because_the_secret_then_carries_the_url()
    {
        var settings = DataLakeWebhookSettings.Parse(Destination(null, """{"dest_dlwAuthMode":"none"}"""));

        settings.EndpointComesFromSecret.Should().BeTrue();
        settings.RequiresSecret.Should().BeFalse();
    }

    [Fact]
    public void A_blank_endpoint_is_rejected_for_every_other_auth_mode()
    {
        var act = () => DataLakeWebhookSettings.Parse(Destination(null, """{"dest_dlwAuthMode":"bearer"}"""));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no endpoint URL configured*");
    }

    [Theory]
    [InlineData("http://ingest.example.com/events")]
    [InlineData("ftp://ingest.example.com/events")]
    public void Plaintext_endpoints_are_refused_because_this_destination_carries_record_data(string endpoint)
    {
        var act = () => DataLakeWebhookSettings.Parse(Destination(endpoint, "{}"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*must use https*");
    }

    [Theory]
    [InlineData("http://localhost:5005/collect")]
    [InlineData("http://127.0.0.1:5005/collect")]
    public void Loopback_http_stays_allowed_for_local_development(string endpoint)
    {
        var settings = DataLakeWebhookSettings.Parse(Destination(endpoint, "{}"));

        settings.EndpointUrl.Should().Be(endpoint);
    }

    [Fact]
    public void A_relative_endpoint_is_rejected_rather_than_producing_an_invalid_request_uri_at_write_time()
    {
        var act = () => DataLakeWebhookSettings.Parse(Destination("/events", "{}"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not an absolute URI*");
    }

    [Fact]
    public void An_unsupported_http_method_is_rejected()
    {
        var act = () => DataLakeWebhookSettings.Parse(
            Destination("https://ingest.example.com/events", """{"dest_dlwHttpMethod":"DELETE"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*unsupported HTTP method*");
    }

    [Fact]
    public void Content_type_follows_the_payload_shape_unless_overridden()
    {
        var jsonArray = DataLakeWebhookSettings.Parse(Destination(
            "https://ingest.example.com/events", """{"dest_dlwPayloadShape":"jsonArray"}"""));
        var overridden = DataLakeWebhookSettings.Parse(Destination(
            "https://ingest.example.com/events",
            """{"dest_dlwPayloadShape":"ndjson","dest_dlwContentType":"application/json"}"""));

        jsonArray.ContentType.Should().Be("application/json");
        overridden.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void ApiKeyHeader_mode_gets_a_conventional_default_header_name()
    {
        var settings = DataLakeWebhookSettings.Parse(Destination(
            "https://ingest.example.com/events", """{"dest_dlwAuthMode":"apiKeyHeader"}"""));

        settings.AuthHeaderName.Should().Be("X-Api-Key");
    }

    [Fact]
    public void OAuth2_mode_requires_a_token_endpoint_and_client_id()
    {
        var act = () => DataLakeWebhookSettings.Parse(Destination(
            "https://ingest.example.com/events", """{"dest_dlwAuthMode":"oauth2ClientCredentials"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*dest_dlwTokenEndpoint*");
    }

    [Fact]
    public void Static_headers_are_read_from_the_nested_json_document()
    {
        var settings = DataLakeWebhookSettings.Parse(Destination(
            "https://ingest.example.com/events",
            """{"dest_dlwHeadersJson":"{\"X-Splunk-Channel\":\"abc-123\"}"}"""));

        settings.Headers.Should().ContainKey("X-Splunk-Channel").WhoseValue.Should().Be("abc-123");
    }

    [Fact]
    public void A_malformed_headers_document_yields_no_headers_rather_than_failing_the_destination()
    {
        var settings = DataLakeWebhookSettings.Parse(Destination(
            "https://ingest.example.com/events", """{"dest_dlwHeadersJson":"not json at all"}"""));

        settings.Headers.Should().BeEmpty();
    }

    [Fact]
    public void Expected_status_codes_parse_as_a_deduplicated_list_and_skip_junk()
    {
        var settings = DataLakeWebhookSettings.Parse(Destination(
            "https://ingest.example.com/events", """{"dest_dlwExpectedStatusCodes":"200, 202, 202, abc"}"""));

        settings.ExpectedStatusCodes.Should().BeEquivalentTo([200, 202]);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("999999", 50000)]
    public void Batch_size_is_clamped_to_a_sane_range(string configured, int expected)
    {
        var settings = DataLakeWebhookSettings.Parse(Destination(
            "https://ingest.example.com/events", $$"""{"dest_dlwBatchSize":"{{configured}}"}"""));

        settings.BatchSize.Should().Be(expected);
    }
}
