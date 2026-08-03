using FHIRBridge.Governance;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests;

/// <summary>
/// Locks the central leak guardrail (<see cref="SafeErrorText"/>). If any of these regress, raw exception /
/// upstream-response text (HTML error pages, stack traces, oversized/technical payloads) could reach a client —
/// exactly the leak this guard exists to prevent.
/// </summary>
public sealed class SafeErrorTextTests
{
    [Theory]
    [InlineData("Source connection 'Epic Prod' was not found.")]
    [InlineData("Please check the information you entered.")]
    [InlineData("The source connection is currently unavailable.")]
    public void Sanitize_allows_short_human_sentences(string message)
    {
        Assert.Equal(message, SafeErrorText.Sanitize(message));
    }

    [Theory]
    // HTML error page (the real Epic $export 404 case)
    [InlineData("<!DOCTYPE html><html><head><title>404 - File or directory not found.</title></head></html>")]
    // Markup fragment
    [InlineData("<div>Server Error</div>")]
    // Stack-trace-ish / technical
    [InlineData("System.InvalidOperationException: something failed")]
    [InlineData("Boom\n   at Foo.Bar()\n   at Baz.Qux()")]
    // Multi-line
    [InlineData("line one\r\nline two")]
    public void Sanitize_rejects_markup_stacktrace_and_multiline(string message)
    {
        Assert.Null(SafeErrorText.Sanitize(message));
    }

    [Fact]
    public void Sanitize_rejects_oversized_payloads()
    {
        Assert.Null(SafeErrorText.Sanitize(new string('x', 5000)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_rejects_empty(string? message)
    {
        Assert.Null(SafeErrorText.Sanitize(message));
    }

    [Fact]
    public void SanitizeOr_falls_back_to_generic_when_unsafe()
    {
        Assert.Equal("Something went wrong.", SafeErrorText.SanitizeOr("<html>leak</html>", "Something went wrong."));
    }

    [Fact]
    public void SanitizeOr_keeps_a_safe_message()
    {
        Assert.Equal("Record not found.", SafeErrorText.SanitizeOr("Record not found.", "Something went wrong."));
    }
}
