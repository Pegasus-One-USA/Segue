using FHIRBridge.Governance;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

// SafeErrorText.Redact is the one user-facing form of an upstream failure text: the bulk export poller's job and run
// errors, and the Bulk Data Status popup's live error. A pre-signed file URL grants access to bulk NDJSON on its own,
// and a source's error body can echo URLs or be a whole HTML page; only the host of any URL may remain, and no markup.
public sealed class SafeErrorTextRedactTests
{
    [Fact]
    public void Cuts_every_url_to_its_scheme_and_host()
    {
        var redacted = SafeErrorText.Redact(
            "Bulk export file download returned 403 (Forbidden) for https://files.example.com/exports/7f3a/Patient.ndjson?sv=2024&sig=SECRET. "
            + "Retry at http://fhir.example.com:8443/status/1?_token=SECRET#frag");

        redacted.Should().Be(
            "Bulk export file download returned 403 (Forbidden) for https://files.example.com Retry at http://fhir.example.com:8443");
    }

    [Fact]
    public void Drops_markup_and_collapses_whitespace_and_newlines()
    {
        var redacted = SafeErrorText.Redact(
            "Bulk export status returned 404 (Not Found). <?xml version=\"1.0\"?>\n<Error>\r\n  <Code>BlobNotFound</Code>\n</Error>");

        redacted.Should().Be("Bulk export status returned 404 (Not Found). BlobNotFound");
    }

    [Fact]
    public void Removes_urls_inside_markup_too()
    {
        var redacted = SafeErrorText.Redact("<a href=\"https://files.example.com/x?sig=SECRET\">https://files.example.com/x?sig=SECRET</a>");

        redacted.Should().NotContain("SECRET").And.NotContain("sig=").And.NotContain("<");
        redacted.Should().Be("https://files.example.com");
    }

    [Fact]
    public void Caps_the_text_at_300_characters_after_cutting_urls()
    {
        // URLs are cut first, so the cap can never leave half a URL (and its query) behind.
        var longUrl = "https://files.example.com/" + new string('a', 400) + "?sig=SECRET";
        var redacted = SafeErrorText.Redact(new string('x', 280) + " " + longUrl + " " + new string('y', 100));

        redacted.Should().NotContain("SECRET").And.NotContain("/aaa");
        redacted.Length.Should().Be(301); // 300 characters and the ellipsis
        redacted.Should().EndWith("…");
    }

    [Fact]
    public void Leaves_ordinary_text_as_it_is()
    {
        SafeErrorText.Redact("Source connection 'abc' no longer exists.").Should().Be("Source connection 'abc' no longer exists.");
    }
}
