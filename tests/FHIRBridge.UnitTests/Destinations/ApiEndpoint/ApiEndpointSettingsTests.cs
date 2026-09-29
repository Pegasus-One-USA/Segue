using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.ApiEndpoint;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations.ApiEndpoint;

public sealed class ApiEndpointSettingsTests
{
    private static DestinationConfiguration Destination(string? target, string? connectionMetadataJson) =>
        new("Partner API", DestinationType.ApiEndpoint, new SecretReference("kv", "secret"), target, connectionMetadataJson);

    [Theory]
    [InlineData("""{"dest_apiAuthMode":"bearer"}""", ApiEndpointAuthMode.Bearer)]
    [InlineData("""{"dest_apiAuthMode":"apiKeyHeader"}""", ApiEndpointAuthMode.ApiKeyHeader)]
    [InlineData("""{"dest_apiAuthMode":"apiKeyQuery"}""", ApiEndpointAuthMode.ApiKeyQuery)]
    [InlineData("""{"dest_apiAuthMode":"basic"}""", ApiEndpointAuthMode.Basic)]
    [InlineData("""{"dest_apiAuthMode":"hmacSha256"}""", ApiEndpointAuthMode.HmacSha256)]
    [InlineData(
        """{"dest_apiAuthMode":"oauth2ClientCredentials","dest_apiTokenEndpoint":"https://auth.example.com/token","dest_apiClientId":"client-1"}""",
        ApiEndpointAuthMode.OAuth2ClientCredentials)]
    [InlineData("""{"dest_apiAuthMode":"clientCertificate"}""", ApiEndpointAuthMode.ClientCertificate)]
    public void Parse_reads_every_configured_auth_mode(string json, ApiEndpointAuthMode expected)
    {
        var settings = ApiEndpointSettings.Parse(Destination("https://api.example.com/records", json));

        settings.AuthMode.Should().Be(expected);
    }

    [Fact]
    public void Missing_endpoint_url_throws_because_ApiEndpoint_has_no_secret_carried_url_mode()
    {
        var act = () => ApiEndpointSettings.Parse(Destination(null, "{}"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*no endpoint URL configured*");
    }

    [Fact]
    public void Defaults_are_general_purpose_when_nothing_is_configured()
    {
        var settings = ApiEndpointSettings.Parse(Destination("https://api.example.com/records", "{}"));

        settings.HttpMethod.Should().Be("POST");
        settings.PayloadShape.Should().Be(ApiEndpointPayloadShape.JsonArray);
        settings.ContentType.Should().Be("application/json");
        settings.AuthMode.Should().Be(ApiEndpointAuthMode.None);
        settings.BatchSize.Should().Be(ApiEndpointSettings.DefaultBatchSize);
        settings.MaxRequestBytes.Should().Be(ApiEndpointSettings.DefaultMaxRequestBytes);
        settings.RequireHttps.Should().BeFalse("an arbitrary customer API is not assumed to always carry PHI");
        settings.FailureMode.Should().Be(ApiEndpointFailureMode.Fail);
    }

    [Fact]
    public void Plain_http_is_allowed_by_default()
    {
        var settings = ApiEndpointSettings.Parse(Destination("http://internal.example.com/records", "{}"));

        settings.EndpointUrl.Should().Be("http://internal.example.com/records");
    }

    [Fact]
    public void Http_is_rejected_when_RequireHttps_is_enabled_and_not_loopback()
    {
        var act = () => ApiEndpointSettings.Parse(Destination(
            "http://api.example.com/records", """{"dest_apiRequireHttps":"true"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*https*");
    }

    [Fact]
    public void Loopback_http_is_allowed_even_when_RequireHttps_is_enabled()
    {
        var settings = ApiEndpointSettings.Parse(Destination(
            "http://localhost:8080/records", """{"dest_apiRequireHttps":"true"}"""));

        settings.EndpointUrl.Should().Be("http://localhost:8080/records");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public void Unsupported_http_method_throws(string method)
    {
        var act = () => ApiEndpointSettings.Parse(Destination(
            "https://api.example.com/records", $$"""{"dest_apiHttpMethod":"{{method}}"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*unsupported HTTP method*");
    }

    [Fact]
    public void ApiKeyHeader_defaults_the_header_name_when_not_configured()
    {
        var settings = ApiEndpointSettings.Parse(Destination(
            "https://api.example.com/records", """{"dest_apiAuthMode":"apiKeyHeader"}"""));

        settings.AuthHeaderName.Should().Be("X-Api-Key");
    }

    [Fact]
    public void ApiKeyQuery_defaults_the_query_param_name_when_not_configured()
    {
        var settings = ApiEndpointSettings.Parse(Destination(
            "https://api.example.com/records", """{"dest_apiAuthMode":"apiKeyQuery"}"""));

        settings.ApiKeyQueryParamName.Should().Be("api_key");
    }

    [Fact]
    public void OAuth2ClientCredentials_requires_token_endpoint_and_client_id()
    {
        var act = () => ApiEndpointSettings.Parse(Destination(
            "https://api.example.com/records", """{"dest_apiAuthMode":"oauth2ClientCredentials"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*dest_apiTokenEndpoint*");
    }

    [Fact]
    public void Parses_static_headers_and_query_params()
    {
        var settings = ApiEndpointSettings.Parse(Destination(
            "https://api.example.com/records",
            """{"dest_apiHeadersJson":"{\"X-Tenant\":\"acme\"}","dest_apiQueryParamsJson":"{\"version\":\"2\"}"}"""));

        settings.Headers.Should().ContainKey("X-Tenant").WhoseValue.Should().Be("acme");
        settings.QueryParams.Should().ContainKey("version").WhoseValue.Should().Be("2");
    }

    [Fact]
    public void Malformed_headers_json_yields_an_empty_map_rather_than_failing_the_destination()
    {
        var settings = ApiEndpointSettings.Parse(Destination(
            "https://api.example.com/records", """{"dest_apiHeadersJson":"not json"}"""));

        settings.Headers.Should().BeEmpty();
    }
}
