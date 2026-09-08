using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

public sealed class TokenEndpointFailureDiagnosisRuleTests
{
    private readonly TokenEndpointFailureDiagnosisRule _sut = new();

    [Theory]
    [InlineData("Epic token endpoint returned 400 (Bad Request). {\"error\":\"invalid_client\"}")]
    [InlineData("Healow token endpoint returned 401 (Unauthorized). invalid_client")]
    [InlineData("MEDITECH Greenfield token endpoint returned 400 (Bad Request). invalid_client")]
    public void Matches_any_provider_and_flags_invalid_client(string message)
    {
        var exception = new InvalidOperationException(message);
        _sut.Matches(exception).Should().BeTrue();
        var diagnosis = _sut.Diagnose(exception);
        diagnosis.Action.Should().Be(DiagnosisAction.SelfFix);
        diagnosis.Cause.Should().Contain("private key/secret");
    }

    [Fact]
    public void Matches_empty_token_response()
    {
        var exception = new InvalidOperationException("Epic token endpoint returned an empty access token.");
        _sut.Matches(exception).Should().BeTrue();
        _sut.Diagnose(exception).Cause.Should().Contain("token endpoint URL and scopes");
    }

    [Fact]
    public void Does_not_match_unrelated_errors()
    {
        var exception = new InvalidOperationException("Something else went wrong.");
        _sut.Matches(exception).Should().BeFalse();
    }

    // ---- Status-driven diagnosis (TokenEndpointException) ----------------------------------------------------
    //
    // The regression these cover: every one of the statuses below used to be worded "check the token endpoint URL,
    // client credentials, and scopes", because the only thing the rule could do was search the message text for
    // "invalid_client" and fall through on everything else. An operator reading that during an Epic maintenance
    // window would go and re-verify credentials that were never wrong.

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void A_server_error_is_diagnosed_as_an_outage_not_as_bad_credentials(int statusCode)
    {
        var exception = TokenEndpointException.FromResponse("Epic", statusCode, "Service Unavailable", null);

        _sut.Matches(exception).Should().BeTrue();
        var diagnosis = _sut.Diagnose(exception);

        diagnosis.Cause.Should().Contain("not available");
        diagnosis.Cause.Should().Contain("not a problem with this connection's credentials");
        diagnosis.Cause.Should().NotContain("Check the client ID");
    }

    [Fact]
    public void A_throttled_request_says_so_rather_than_blaming_the_credentials()
    {
        var exception = TokenEndpointException.FromResponse("Epic", 429, "Too Many Requests", null);

        var diagnosis = _sut.Diagnose(exception);

        diagnosis.Cause.Should().Contain("rate-limiting");
        diagnosis.Cause.Should().Contain("credentials are fine");
    }

    [Fact]
    public void A_real_invalid_client_still_points_at_the_credentials()
    {
        var exception = TokenEndpointException.FromResponse(
            "Epic", 400, "Bad Request", """{"error":"invalid_client"}""");

        var diagnosis = _sut.Diagnose(exception);

        diagnosis.Cause.Should().Contain("invalid_client");
        diagnosis.Cause.Should().Contain("client ID");
        diagnosis.Action.Should().Be(DiagnosisAction.SelfFix);
    }

    [Fact]
    public void An_invalid_scope_rejection_points_at_the_scopes()
    {
        var exception = TokenEndpointException.FromResponse(
            "Epic", 400, "Bad Request", """{"error":"invalid_scope"}""");

        _sut.Diagnose(exception).Cause.Should().Contain("scopes");
    }

    /// <summary>
    /// An outage stays SelfFix. ContactSupport means "needs the FHIRBridge team" (see DiagnosisAction), so using it
    /// for a vendor outage would raise a support ticket against us every time Epic has a maintenance window — the
    /// operator is the one who acts here, by retrying or chasing the vendor.
    /// </summary>
    [Fact]
    public void An_outage_is_not_routed_to_FHIRBridge_support()
    {
        var exception = TokenEndpointException.FromResponse("Epic", 503, "Service Unavailable", null);

        _sut.Diagnose(exception).Action.Should().Be(DiagnosisAction.SelfFix);
    }

    /// <summary>
    /// The generic client-credentials provider used to word its failure "OAuth2 token request returned …", which
    /// never matched the old "token endpoint returned" substring — so those failures got no diagnosis at all,
    /// despite the rule's own documentation claiming to cover them. Matching by exception TYPE means the wording
    /// can no longer drift out of coverage.
    /// </summary>
    [Fact]
    public void Matches_the_generic_oauth2_provider_the_substring_match_used_to_miss()
    {
        var exception = TokenEndpointException.FromResponse("OAuth2", 503, "Service Unavailable", null);

        _sut.Matches(exception).Should().BeTrue();
        _sut.Diagnose(exception).Cause.Should().Contain("not available");
    }

    [Fact]
    public void An_empty_token_payload_points_at_the_endpoint_url()
    {
        var exception = TokenEndpointException.EmptyResponse("Epic", "an empty access token.");

        _sut.Matches(exception).Should().BeTrue();
        _sut.Diagnose(exception).Cause.Should().Contain("did not return a usable access token");
    }
}
