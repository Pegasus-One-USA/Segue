using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Governance;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

public sealed class SqlDestinationFailureDiagnosisRuleTests
{
    private readonly SqlDestinationFailureDiagnosisRule _sut = new();

    [Theory]
    [InlineData("Login failed for user 'svc'.")]
    [InlineData("FATAL: password authentication failed for user \"svc\"")]
    [InlineData("Access denied for user 'svc'@'%'")]
    public void Matches_login_failures(string message)
    {
        var exception = new InvalidOperationException(message);
        _sut.Matches(exception).Should().BeTrue();
        _sut.Diagnose(exception).Action.Should().Be(DiagnosisAction.SelfFix);
        _sut.Diagnose(exception).Cause.Should().Contain("credentials");
    }

    [Theory]
    [InlineData("The SELECT permission was denied on the object 'Patient'.")]
    [InlineData("permission denied for table patient")]
    [InlineData("INSERT command denied to user 'svc'@'%' for table 'patient'")]
    public void Matches_permission_denied(string message)
    {
        var exception = new InvalidOperationException(message);
        _sut.Matches(exception).Should().BeTrue();
        _sut.Diagnose(exception).Cause.Should().Contain("permission");
    }

    [Theory]
    [InlineData("Transaction (Process ID 52) was deadlocked on lock resources with another process")]
    [InlineData("Lock wait timeout exceeded; try restarting transaction")]
    [InlineData("deadlock detected")]
    public void Matches_lock_contention(string message)
    {
        var exception = new InvalidOperationException(message);
        _sut.Matches(exception).Should().BeTrue();
        _sut.Diagnose(exception).Cause.Should().Contain("locked");
    }

    [Fact]
    public void Matches_host_unreachable_as_fallback_diagnosis()
    {
        var exception = new InvalidOperationException("A network-related or instance-specific error occurred.");
        _sut.Matches(exception).Should().BeTrue();
        _sut.Diagnose(exception).Cause.Should().Contain("network/firewall");
    }

    [Fact]
    public void Does_not_match_unrelated_errors()
    {
        var exception = new InvalidOperationException("Something else went wrong.");
        _sut.Matches(exception).Should().BeFalse();
    }
}
