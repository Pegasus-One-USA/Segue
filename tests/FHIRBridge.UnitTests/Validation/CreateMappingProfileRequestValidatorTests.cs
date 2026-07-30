using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Validation;

/// <summary>
/// Server-side mirror of the destination wizard's mapping checks — see
/// <c>CreateMappingProfileRequestValidator</c>'s XML doc for the Angular source of each rule — plus the
/// destination-schema cross-check (column exists / not identity-computed / value-type matches / NOT NULL vs
/// IsRequired) added against <see cref="IDestinationSchemaService"/>.
/// </summary>
public sealed class CreateMappingProfileRequestValidatorTests
{
    private readonly Mock<IDestinationSchemaService> _schemaService = new();
    private readonly CreateMappingProfileRequestValidator _sut;

    public CreateMappingProfileRequestValidatorTests()
    {
        // Default: no introspected tables, so the schema cross-check no-ops and only the shape rules apply —
        // matches every destination type the pre-existing tests exercise, none of which stub a real schema.
        _schemaService
            .Setup(s => s.GetSchemaAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DestinationSchemaDto(Guid.NewGuid(), []));

        _sut = new CreateMappingProfileRequestValidator(_schemaService.Object);
    }

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

    private void StubSchema(params DestinationColumnSchemaDto[] columns) =>
        _schemaService
            .Setup(s => s.GetSchemaAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DestinationSchemaDto(
                Guid.NewGuid(),
                [new DestinationTableSchemaDto("dbo", "Patient", "dbo.Patient", columns)]));

    [Fact]
    public async Task Valid_request_passes()
    {
        (await _sut.ValidateAsync(ValidRequest())).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Empty_name_fails()
    {
        var request = ValidRequest() with { Name = "" };
        var result = await _sut.ValidateAsync(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateMappingProfileRequest.Name));
    }

    [Fact]
    public async Task No_fields_fails()
    {
        var request = ValidRequest(fields: []);
        var result = await _sut.ValidateAsync(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateMappingProfileRequest.Fields));
    }

    [Fact]
    public async Task Field_with_empty_target_field_fails()
    {
        var request = ValidRequest(fields:
        [
            new MappingFieldDto("", "$.id", MappingValueType.String, true, null, null),
        ]);
        var result = await _sut.ValidateAsync(request);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Upsert_destination_without_an_upsert_key_field_fails()
    {
        var request = ValidRequest(
            destinationObject: "dbo.Patient;mode=upsert",
            fields: [new MappingFieldDto("PatientId", "$.id", MappingValueType.String, true, null, null, IsUpsertKey: false)]);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("upsert key"));
    }

    [Fact]
    public async Task Upsert_destination_with_an_upsert_key_field_passes()
    {
        var request = ValidRequest(
            destinationObject: "dbo.Patient;mode=upsert",
            fields: [new MappingFieldDto("PatientId", "$.id", MappingValueType.String, true, null, null, IsUpsertKey: true)]);

        (await _sut.ValidateAsync(request)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Non_upsert_destination_without_an_upsert_key_field_passes()
    {
        var request = ValidRequest(
            destinationObject: "dbo.Patient",
            fields: [new MappingFieldDto("PatientId", "$.id", MappingValueType.String, true, null, null, IsUpsertKey: false)]);

        (await _sut.ValidateAsync(request)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Field_mapped_to_missing_column_fails()
    {
        StubSchema(new DestinationColumnSchemaDto("PatientId", "int", "Integer", false, null, IsPrimaryKey: true));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("PatientId", "$.id", MappingValueType.Integer, true, null, null),
            new MappingFieldDto("Nickname", "$.name[0].given[0]", MappingValueType.String, false, null, null),
        ]);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Fields[1].TargetField" && e.ErrorMessage.Contains("does not exist"));
    }

    [Fact]
    public async Task Field_mapped_to_identity_column_fails()
    {
        StubSchema(new DestinationColumnSchemaDto("ObservationId", "int", "Integer", false, null, IsPrimaryKey: true, IsAutoGenerated: true));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("ObservationId", "$.id", MappingValueType.Integer, true, null, null),
        ]);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("identity or computed column"));
    }

    [Fact]
    public async Task Field_value_type_mismatched_with_column_type_fails()
    {
        StubSchema(new DestinationColumnSchemaDto("PatientRefId", "int", "Integer", true, null));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("PatientRefId", "$.subject.reference", MappingValueType.String, false, null, null),
        ]);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Fields[0].ValueType" && e.ErrorMessage.Contains("expects Integer"));
    }

    [Fact]
    public async Task Not_null_column_without_required_or_default_fails()
    {
        StubSchema(new DestinationColumnSchemaDto("ObservationId", "int", "Integer", false, null, IsPrimaryKey: true));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("ObservationId", "$.id", MappingValueType.Integer, false, null, null),
        ]);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Fields[0].IsRequired" && e.ErrorMessage.Contains("does not allow NULLs"));
    }

    [Fact]
    public async Task Not_null_column_with_required_flag_passes()
    {
        StubSchema(new DestinationColumnSchemaDto("ObservationId", "int", "Integer", false, null, IsPrimaryKey: true));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("ObservationId", "$.id", MappingValueType.Integer, true, null, null),
        ]);

        (await _sut.ValidateAsync(request)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task CorrelateByCode_without_correlation_fields_fails()
    {
        var request = ValidRequest(fields:
        [
            new MappingFieldDto(
                "PatientId", "$.identifier[*].value", MappingValueType.String, false, null, null,
                ArrayPolicy: ArrayPolicy.CorrelateByCode),
        ]);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Fields[0].CorrelationCodeJsonPath");
        result.Errors.Should().Contain(e => e.PropertyName == "Fields[0].CorrelationCodeValue");
    }

    [Fact]
    public async Task CorrelateByCode_selecting_an_identifier_by_system_passes()
    {
        var request = ValidRequest(fields:
        [
            new MappingFieldDto(
                "InternalId", "$.identifier[*].value", MappingValueType.String, false, null, null,
                ArrayPolicy: ArrayPolicy.CorrelateByCode,
                CorrelationCodeJsonPath: "$.identifier[*].system",
                CorrelationCodeValue: "urn:oid:1.2.840.114350.1.72.1.7.7.10.696784.13260"),
        ]);

        (await _sut.ValidateAsync(request)).IsValid.Should().BeTrue();
    }
}
