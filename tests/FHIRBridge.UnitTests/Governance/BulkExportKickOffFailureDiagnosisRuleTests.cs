using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Governance;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

public sealed class BulkExportKickOffFailureDiagnosisRuleTests
{
    private readonly BulkExportKickOffFailureDiagnosisRule _sut = new();

    [Theory]
    [InlineData("Bulk export kick-off returned 404 (Not Found) for https://fhir/Group/g/$export.")]
    [InlineData("Bulk export kick-off returned 401 (Unauthorized) for https://fhir/$export.")]
    [InlineData("Bulk export kick-off rejected as a duplicate for https://fhir/Group/g/$export: a bulk export is already running for this group.")]
    public void Matches_kickoff_failures(string message)
    {
        _sut.Matches(new InvalidOperationException(message)).Should().BeTrue();
    }

    [Fact]
    public void Duplicate_rejection_is_diagnosed_as_an_in_flight_export()
    {
        // A duplicate (e.g. eCW's HTTP 200 + OperationOutcome/duplicate) carries no status code in the message, so it
        // must be matched by phrase, not by the 3-digit status regex — otherwise it falls through to the generic case.
        var exception = new InvalidOperationException(
            "Bulk export kick-off rejected as a duplicate for https://fhir/Group/g/$export: a bulk export is already " +
            "running for this group. Wait for it to finish or cancel it before starting another.");

        _sut.Matches(exception).Should().BeTrue();
        var diagnosis = _sut.Diagnose(exception);
        diagnosis.Action.Should().Be(DiagnosisAction.SelfFix);
        diagnosis.Cause.Should().Contain("already running");
    }

    [Fact]
    public void Status_404_points_at_export_scope()
    {
        var diagnosis = _sut.Diagnose(new InvalidOperationException(
            "Bulk export kick-off returned 404 (Not Found) for https://fhir/$export."));
        diagnosis.Action.Should().Be(DiagnosisAction.SelfFix);
        diagnosis.Cause.Should().Contain("Export Scope");
    }

    [Theory]
    [InlineData("401")]
    [InlineData("403")]
    public void Status_401_or_403_points_at_scopes(string status)
    {
        var diagnosis = _sut.Diagnose(new InvalidOperationException(
            $"Bulk export kick-off returned {status} (Unauthorized) for https://fhir/Group/g/$export."));
        diagnosis.Cause.Should().Contain("scopes");
    }

    [Fact]
    public void Does_not_match_unrelated_errors()
    {
        _sut.Matches(new InvalidOperationException("Something else went wrong.")).Should().BeFalse();
    }
}
