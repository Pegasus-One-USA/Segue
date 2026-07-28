using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Validation;

/// <summary>
/// Server-side mirror of the destination wizard's mapping checks — see
/// <c>CreateMappingProfileRequestValidator</c>'s XML doc for the Angular source of each rule.
/// </summary>
public sealed class CreateMappingProfileRequestValidatorTests
{
    private readonly CreateMappingProfileRequestValidator _sut = new();

    private static CreateMappingProfileRequest ValidRequest(
        string destinationObject = "dbo.Patient",
        IReadOnlyList<MappingFieldDto>? fields = null) =>
        new(
            "Patient -> SQL",
            "Patient",
            Guid.NewGuid(),
            Guid.NewGuid(),
            destinationObject,
            fields ?? [new MappingFieldDto("PatientId", "$.id", MappingValueType.String, true, null, null)]);

    [Fact]
    public void Valid_request_passes()
    {
        _sut.Validate(ValidRequest()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_name_fails()
    {
        var request = ValidRequest() with { Name = "" };
        var result = _sut.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateMappingProfileRequest.Name));
    }

    [Fact]
    public void No_fields_fails()
    {
        var request = ValidRequest(fields: []);
        var result = _sut.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateMappingProfileRequest.Fields));
    }

    [Fact]
    public void Field_with_empty_target_field_fails()
    {
        var request = ValidRequest(fields:
        [
            new MappingFieldDto("", "$.id", MappingValueType.String, true, null, null),
        ]);
        var result = _sut.Validate(request);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Upsert_destination_without_an_upsert_key_field_fails()
    {
        var request = ValidRequest(
            destinationObject: "dbo.Patient;mode=upsert",
            fields: [new MappingFieldDto("PatientId", "$.id", MappingValueType.String, true, null, null, IsUpsertKey: false)]);

        var result = _sut.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("upsert key"));
    }

    [Fact]
    public void Upsert_destination_with_an_upsert_key_field_passes()
    {
        var request = ValidRequest(
            destinationObject: "dbo.Patient;mode=upsert",
            fields: [new MappingFieldDto("PatientId", "$.id", MappingValueType.String, true, null, null, IsUpsertKey: true)]);

        _sut.Validate(request).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Non_upsert_destination_without_an_upsert_key_field_passes()
    {
        var request = ValidRequest(
            destinationObject: "dbo.Patient",
            fields: [new MappingFieldDto("PatientId", "$.id", MappingValueType.String, true, null, null, IsUpsertKey: false)]);

        _sut.Validate(request).IsValid.Should().BeTrue();
    }
}
