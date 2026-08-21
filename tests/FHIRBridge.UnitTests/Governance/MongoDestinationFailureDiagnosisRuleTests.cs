using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Governance;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

public sealed class MongoDestinationFailureDiagnosisRuleTests
{
    private readonly MongoDestinationFailureDiagnosisRule _sut = new();

    [Theory]
    [InlineData("The Mongo connection string must include a database name.")]
    [InlineData("Destination object must name a collection.")]
    public void Matches_own_config_validation_messages(string message)
    {
        var exception = new InvalidOperationException(message);
        _sut.Matches(exception).Should().BeTrue();
        var diagnosis = _sut.Diagnose(exception);
        diagnosis.Action.Should().Be(DiagnosisAction.SelfFix);
        diagnosis.Cause.Should().Contain("configuration");
    }

    [Fact]
    public void Matches_mongo_driver_exceptions_by_namespace()
    {
        var exception = new MongoDB.Driver.MongoException("Unable to connect to server.");
        _sut.Matches(exception).Should().BeTrue();
        _sut.Diagnose(exception).Action.Should().Be(DiagnosisAction.SelfFix);
    }

    [Fact]
    public void Does_not_match_unrelated_errors()
    {
        var exception = new InvalidOperationException("Something else went wrong.");
        _sut.Matches(exception).Should().BeFalse();
    }
}
