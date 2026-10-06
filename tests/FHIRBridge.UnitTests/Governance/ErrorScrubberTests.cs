using FHIRBridge.Governance;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

public sealed class ErrorScrubberTests
{
    private readonly ErrorScrubber _scrubber = new();

    [Theory]
    [InlineData("SSN is 123-45-6789 here", "123-45-6789")]
    [InlineData("Contact jane.doe@example.com now", "jane.doe@example.com")]
    [InlineData("Call 555-123-4567 please", "555-123-4567")]
    [InlineData("born 1984-03-12 in", "1984-03-12")]
    [InlineData("MRN 123456789012 failed", "123456789012")]
    [InlineData("Bearer abcdefghijklmnop1234", "abcdefghijklmnop1234")]
    [InlineData("Server=x;Password=Sup3rS3cret;Database=y", "Sup3rS3cret")]
    [InlineData("GET https://host/path?patient=John&ssn=1 failed", "patient=John")]
    [InlineData("Cannot insert duplicate key value is (Smith, John)", "Smith, John")]
    [InlineData("{\"family\":\"Smith\",\"given\":[\"John\",\"Q\"]}", "Smith")]
    [InlineData("Value 'John Smith' is invalid", "John Smith")]
    [InlineData("firstName=Jane lastName=Roe", "Jane")]
    public void ScrubText_RemovesSensitiveValue(string input, string sensitive)
    {
        _scrubber.ScrubText(input).Should().NotContain(sensitive);
    }

    [Fact]
    public void ScrubText_KeepsDiagnosticallyUsefulText()
    {
        var text = "Cannot connect to host 'sql-prod' on port 1433: the operation timed out (can't retry, it doesn't help). id 0f8fad5b-d9cb-469f-a165-70867728950e";

        var scrubbed = _scrubber.ScrubText(text);

        scrubbed.Should().Contain("sql-prod").And.Contain("1433").And.Contain("timed out")
            .And.Contain("0f8fad5b-d9cb-469f-a165-70867728950e");
    }

    [Fact]
    public void Scrub_IncludesFullChainWithStackTraces_AndRootCause()
    {
        Exception ex;
        try
        {
            try { throw new InvalidOperationException("inner failure for Patient"); }
            catch (Exception inner) { throw new ApplicationException("outer wrapper", inner); }
        }
        catch (Exception e) { ex = e; }
        ex.Data["patientName"] = "Jane Roe";

        var result = _scrubber.Scrub(ex);

        result.ExceptionType.Should().Be("ApplicationException");
        result.RootCauseType.Should().Be("InvalidOperationException");
        result.Message.Should().Contain("outer wrapper").And.Contain("root cause");
        result.Detail.Should().Contain("System.ApplicationException").And.Contain("--- Inner exception [1] System.InvalidOperationException")
            .And.Contain("at FHIRBridge.UnitTests.Governance.ErrorScrubberTests");
        result.Detail.Should().Contain("Data keys (values withheld): patientName").And.NotContain("Jane Roe");
    }

    [Fact]
    public void Scrub_FlattensAggregateException()
    {
        var agg = new AggregateException(new InvalidOperationException("a"), new TimeoutException("b"));

        var result = _scrubber.Scrub(agg);

        result.Detail.Should().Contain("InvalidOperationException").And.Contain("TimeoutException");
    }
}
