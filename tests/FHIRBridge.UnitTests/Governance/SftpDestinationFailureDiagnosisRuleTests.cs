using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Governance;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

public sealed class SftpDestinationFailureDiagnosisRuleTests
{
    private readonly SftpDestinationFailureDiagnosisRule _sut = new();

    [Fact]
    public void Matches_invalid_secret_uri_and_names_the_expected_format()
    {
        var exception = new InvalidOperationException(
            "SFTP destination secret must be an 'sftp://user:password@host:port/path' URI.");
        _sut.Matches(exception).Should().BeTrue();
        var diagnosis = _sut.Diagnose(exception);
        diagnosis.Action.Should().Be(DiagnosisAction.SelfFix);
        diagnosis.Cause.Should().Contain("sftp://user:password@host:port/path");
    }

    [Fact]
    public void Matches_ssh_net_exceptions_by_namespace()
    {
        var exception = new Renci.SshNet.Common.SshAuthenticationException("Permission denied (publickey,password).");
        _sut.Matches(exception).Should().BeTrue();
        _sut.Diagnose(exception).Cause.Should().Contain("username/password");
    }

    [Fact]
    public void Does_not_match_unrelated_errors()
    {
        var exception = new InvalidOperationException("Something else went wrong.");
        _sut.Matches(exception).Should().BeFalse();
    }
}
