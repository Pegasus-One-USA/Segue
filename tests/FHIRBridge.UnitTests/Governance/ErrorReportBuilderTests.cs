using System.Text;
using FHIRBridge.Api.Operations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Governance;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

public sealed class ErrorReportBuilderTests
{
    private static ErrorLogDto Row(string message, string? stack = null) => new(
        Guid.NewGuid(), DateTime.UtcNow, "Error", "InvalidOperationException", message, stack, "Api", "corr-1",
        "ERR-20260101-000001", "Validation", null, null, null, null, null, null, null, "Open", null, null, "SelfFix", "Fix it");

    private static ErrorDashboardDto Summary() =>
        new(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, 1, 1, 0, 0, 1, 1, 0, [], [], [], [], []);

    [Fact]
    public void Build_RescrubsLegacyRows_AndHonoursIncludeStackTrace()
    {
        var rows = new[] { Row("bad value for ssn 123-45-6789", "at X in C:\\a.cs:line 1\nmail jane@example.com") };

        var withStack = ErrorReportBuilder.Build(Summary(), rows, false, true, "1.0", new ErrorScrubber());
        var withoutStack = ErrorReportBuilder.Build(Summary(), rows, false, false, "1.0", new ErrorScrubber());

        withStack.Errors[0].Message.Should().NotContain("123-45-6789");
        withStack.Errors[0].StackTrace.Should().NotContain("jane@example.com");
        withoutStack.Errors[0].StackTrace.Should().BeNull();
        withStack.Errors[0].WhatToDo.Should().Be("Check your configuration");
    }

    [Fact]
    public void ToCsv_QuotesCommasAndNeutralisesFormulas()
    {
        var report = ErrorReportBuilder.Build(Summary(), [Row("=cmd|' /C calc'!A0, with comma")], false, false, "1.0", new ErrorScrubber());

        var csv = Encoding.UTF8.GetString(ErrorReportBuilder.ToCsv(report));

        csv.Should().Contain("\"'=cmd");
        csv.Should().StartWith("\uFEFFErrorReferenceId,");
    }
}
