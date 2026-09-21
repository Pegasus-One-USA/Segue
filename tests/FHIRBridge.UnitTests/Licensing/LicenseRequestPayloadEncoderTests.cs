using FHIRBridge.Infrastructure.Licensing;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Licensing;

public sealed class LicenseRequestPayloadEncoderTests
{
    [Fact]
    public void Encode_then_TryDecode_round_trips_every_field()
    {
        var payload = new LicenseRequestPayload(
            "Mercy Health System", "admin@mercy.example", "Mercy Health", "1 Main St, Springfield",
            "+1-555-0100", "abc123uniquekey");

        var encoded = LicenseRequestPayloadEncoder.Encode(payload);
        var decoded = LicenseRequestPayloadEncoder.TryDecode(encoded);

        decoded.Should().Be(payload);
    }

    [Fact]
    public void Encode_omits_optional_fields_and_round_trips_nulls()
    {
        var payload = new LicenseRequestPayload(
            "Solo Clinic", "owner@solo.example", null, null, "+1-555-0199", "uniquekey2");

        var decoded = LicenseRequestPayloadEncoder.TryDecode(LicenseRequestPayloadEncoder.Encode(payload));

        decoded.Should().Be(payload);
        decoded!.CompanyName.Should().BeNull();
        decoded.Address.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-valid-base64!!")]
    [InlineData("dGhpcyBpcyBqdXN0IHNvbWUgcmFuZG9tIHRleHQ=")]
    public void TryDecode_returns_null_for_garbage_without_throwing(string garbage)
    {
        var result = LicenseRequestPayloadEncoder.TryDecode(garbage);

        result.Should().BeNull();
    }

    [Fact]
    public void TryDecode_returns_null_for_a_tampered_blob()
    {
        var encoded = LicenseRequestPayloadEncoder.Encode(
            new LicenseRequestPayload("Name", "e@x.com", null, null, "555", "key"));
        var bytes = Convert.FromBase64String(encoded);
        bytes[^1] ^= 0xFF; // flip a bit in the auth tag
        var tampered = Convert.ToBase64String(bytes);

        LicenseRequestPayloadEncoder.TryDecode(tampered).Should().BeNull();
    }
}
