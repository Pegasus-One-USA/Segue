using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Governance;
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
}
