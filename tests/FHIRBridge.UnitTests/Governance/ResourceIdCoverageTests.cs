using System.Security.Cryptography;
using System.Text;
using FHIRBridge.Governance;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Governance;

public sealed class ResourceIdCoverageTests
{
    private readonly ErrorScrubber _scrubber = new();

    [Theory]
    [InlineData("HealthcareService/hs-9981 not found", "hs-9981")]
    [InlineData("Endpoint/ep42 failed", "ep42")]
    [InlineData("DeviceRequest/dr7 failed", "dr7")]
    [InlineData("Patient/abc", "abc")]
    public void Any_resource_type_id_is_tokenised(string text, string id)
    {
        var scrubbed = _scrubber.ScrubText(text);
        scrubbed.Should().NotContain(id).And.MatchRegex(@"/#[0-9a-f]{8}");
    }

    [Fact]
    public void Long_ids_are_masked_too()
    {
        var id = "a1" + new string('x', 120);
        _scrubber.ScrubText($"Patient/{id} failed").Should().NotContain(id);
    }

    [Fact]
    public void Relative_search_urls_lose_their_values()
    {
        _scrubber.ScrubText("GET Patient?identifier=MRN-445566 returned 500").Should().NotContain("MRN-445566");
    }

    [Theory]
    [InlineData("HTTP/1.1 500")]
    [InlineData("Content/Types are fine")]
    [InlineData("application/json")]
    public void Ordinary_text_is_left_alone(string text) => _scrubber.ScrubText(text).Should().Be(text);

    [Fact]
    public void Token_is_keyed_not_a_plain_hash_of_the_id()
    {
        var plain = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("123")))[..8].ToLowerInvariant();
        _scrubber.ScrubText("Patient/123").Should().NotContain(plain);
    }
}
