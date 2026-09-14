using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
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
    private readonly Mock<IConfigurationRepository> _configurationRepository = new();
    private readonly Mock<IEffectiveRuleResolver> _ruleResolver = new();
    private readonly CreateMappingProfileRequestValidator _sut;

    public CreateMappingProfileRequestValidatorTests()
    {
        // Default: no introspected tables, so the schema cross-check no-ops and only the shape rules apply —
        // matches every destination type the pre-existing tests exercise, none of which stub a real schema.
        _schemaService
            .Setup(s => s.GetSchemaAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DestinationSchemaDto(Guid.NewGuid(), []));

        _configurationRepository
            .Setup(r => r.GetDestinationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DestinationConfiguration(
                "Test SQL", DestinationType.SqlServer, new SecretReference("kv", "secret"), "dbo"));

        // Default: no applicable rules, so the rule-vs-column-type check no-ops for every pre-existing test.
        _ruleResolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[]);

        _sut = new CreateMappingProfileRequestValidator(
            _schemaService.Object, _configurationRepository.Object, _ruleResolver.Object);
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

    /// <summary>The regression this whole tier exists for: Patient.birthDate (a Date) mapped onto an int
    /// PatientAge column is valid precisely BECAUSE a DateMathAge rule turns it into an Integer first. The
    /// V2 builder authors that rule at Workflow scope, which the validator never queried — so it compared the
    /// raw extraction type against the column and refused to save a correctly-configured mapping.</summary>
    [Fact]
    public async Task Workflow_scoped_rule_output_type_supersedes_the_raw_source_type()
    {
        StubSchema(new DestinationColumnSchemaDto("PatientAge", "int", "Integer", true, null));
        StubRule(new TransformationRule(
            TransformScope.Workflow, TransformNodeType.DateMathAge, "{}",
            destinationType: DestinationType.SqlServer, resourceType: "Patient", destinationField: "PatientAge",
            expectedValueType: MappingValueType.Integer));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("PatientAge", "$.birthDate", MappingValueType.Date, false, null, null),
        ]);

        (await _sut.ValidateAsync(request)).IsValid.Should().BeTrue();
    }

    /// <summary>Rules run in order, each feeding the next, so only the LAST declared output type describes what
    /// actually reaches the column — checking every step would reject a valid chain for an intermediate type.</summary>
    [Fact]
    public async Task Only_the_last_declared_output_type_in_a_chain_is_checked_against_the_column()
    {
        StubSchema(new DestinationColumnSchemaDto("PatientAge", "int", "Integer", true, null));
        _ruleResolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)
            [
                new TransformationRule(
                    TransformScope.Workflow, TransformNodeType.StringNormalization, "{}",
                    destinationType: DestinationType.SqlServer, resourceType: "Patient",
                    destinationField: "PatientAge", expectedValueType: MappingValueType.String),
                new TransformationRule(
                    TransformScope.Workflow, TransformNodeType.DateMathAge, "{}",
                    destinationType: DestinationType.SqlServer, resourceType: "Patient",
                    destinationField: "PatientAge", expectedValueType: MappingValueType.Integer),
            ]);

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("PatientAge", "$.birthDate", MappingValueType.Date, false, null, null),
        ]);

        (await _sut.ValidateAsync(request)).IsValid.Should().BeTrue();
    }

    /// <summary>Rules the builder wrote before its workflow was first saved are unattached, and the mapping
    /// profile is saved BEFORE the workflow that would attach them — so the validator has to opt into that
    /// pending tier or it can never see the rule the user just authored.</summary>
    [Fact]
    public async Task Rules_are_resolved_with_the_workflow_id_and_the_pending_tier_included()
    {
        StubSchema(new DestinationColumnSchemaDto("PatientAge", "int", "Integer", true, null));
        var workflowId = Guid.NewGuid();

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("PatientAge", "$.birthDate", MappingValueType.Integer, false, null, null),
        ]) with { ResourcePipelineRouteId = workflowId };

        await _sut.ValidateAsync(request);

        _ruleResolver.Verify(
            r => r.ResolveAsync(
                DestinationType.SqlServer, "Patient", "PatientAge", workflowId, null, "Patient.birthDate",
                It.IsAny<CancellationToken>(), true),
            Times.Once);
    }

    // No destination-schema probe (SqlDestinationSchemaService.MapSqlServerType/MapPostgresType/MapMySqlType)
    // ever reports a column's own MappingValueType as literally "Json" — every character-string SQL type,
    // including a real Postgres jsonb, collapses to "String" — so a column intentionally used to hold JSON
    // must not be rejected purely on that exact-match mismatch, while a genuinely length-BOUNDED column
    // can't hold it and must still fail (IsJsonSafeForColumn).
    [Fact]
    public async Task Json_field_onto_an_unbounded_max_length_String_column_passes()
    {
        // DataType here is the BARE type name a live probe actually reports (SqlDestinationSchemaService.
        // ReadColumnsAsync reads DATA_TYPE, e.g. "nvarchar" — never a combined "nvarchar(max)" string);
        // MaxLength null is what -1 (SQL Server) / a genuinely NULL character_maximum_length (PostgreSQL
        // text) normalizes to.
        StubSchema(new DestinationColumnSchemaDto("ComponentJson", "nvarchar", "String", true, null));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("ComponentJson", "$.component", MappingValueType.Json, false, null, null),
        ]);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Json_field_onto_a_length_bounded_String_column_still_fails()
    {
        StubSchema(new DestinationColumnSchemaDto("ComponentJson", "nvarchar", "String", true, 200));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("ComponentJson", "$.component", MappingValueType.Json, false, null, null),
        ]);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Fields[0].ValueType" && e.ErrorMessage.Contains("expects String"));
    }

    // The actual regression this exists for: a MySQL `text` column (or PostgreSQL `text`, whose information_
    // schema DOES report a genuinely NULL character_maximum_length — already covered above). MySQL's own
    // information_schema NEVER reports a null CHARACTER_MAXIMUM_LENGTH for TEXT/MEDIUMTEXT/LONGTEXT — it's
    // always a real (if huge) number, because their capacity is fixed by the type keyword itself. So
    // MaxLength alone can't be the only signal — the column must also be recognized by its DATA TYPE NAME.
    [Theory]
    [InlineData("text", 65535)]
    [InlineData("mediumtext", 16777215)]
    [InlineData("longtext", 4294967295)]
    [InlineData("ntext", 1073741823)] // SQL Server's own legacy unbounded unicode type — same non-null quirk.
    public async Task Json_field_onto_a_MySQL_style_unbounded_by_name_text_column_passes(string dataType, long maxLength)
    {
        StubSchema(new DestinationColumnSchemaDto("All", dataType, "String", true, (int)maxLength));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("All", "$.component", MappingValueType.Json, false, null, null),
        ]);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }

    // TINYTEXT (255 chars) is genuinely too small for arbitrary JSON — must NOT be swept up by the same
    // by-name exception as its larger siblings just because it shares the "*text" naming family.
    [Fact]
    public async Task Json_field_onto_MySQL_TINYTEXT_still_fails()
    {
        StubSchema(new DestinationColumnSchemaDto("All", "tinytext", "String", true, 255));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("All", "$.component", MappingValueType.Json, false, null, null),
        ]);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Fields[0].ValueType" && e.ErrorMessage.Contains("expects String"));
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

    private void StubRule(TransformationRule rule) =>
        _ruleResolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<TransformationRule>)[rule]);

    [Fact]
    public async Task Global_rule_with_mismatched_expected_type_fails()
    {
        StubSchema(new DestinationColumnSchemaDto("PatientRefId", "varchar", "String", true, null));
        StubRule(new TransformationRule(
            TransformScope.Global, TransformNodeType.NumberCast, "{}",
            expectedValueType: MappingValueType.Integer));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("PatientRefId", "$.subject.reference", MappingValueType.String, false, null, null),
        ]);

        var result = await _sut.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == "Fields[0].TargetField" &&
            e.ErrorMessage.Contains("NumberCast") &&
            e.CustomState is TransformationRuleTypeConflict);
    }

    [Fact]
    public async Task Rule_without_a_declared_expected_type_does_not_fail_the_save()
    {
        StubSchema(new DestinationColumnSchemaDto("PatientRefId", "varchar", "String", true, null));
        StubRule(new TransformationRule(
            TransformScope.Global, TransformNodeType.NumberCast, "{}"));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("PatientRefId", "$.subject.reference", MappingValueType.String, false, null, null),
        ]);

        (await _sut.ValidateAsync(request)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Rule_with_matching_expected_type_passes()
    {
        StubSchema(new DestinationColumnSchemaDto("Age", "int", "Integer", true, null));
        StubRule(new TransformationRule(
            TransformScope.Global, TransformNodeType.DateMathAge, "{}",
            expectedValueType: MappingValueType.Integer));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("Age", "$.birthDate", MappingValueType.Integer, false, null, null),
        ]);

        (await _sut.ValidateAsync(request)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Rule_with_matching_expected_type_passes_even_when_raw_source_type_differs_from_column()
    {
        // The field's own ValueType reflects the raw JsonPath extraction (Date, from birthDate) — the rule
        // (DateMathAge) is what actually turns that into the Integer the column expects. The raw-type check
        // must defer to the rule's declared output type instead of comparing Date against Integer itself.
        StubSchema(new DestinationColumnSchemaDto("BirthDateAge", "int", "Integer", true, null));
        StubRule(new TransformationRule(
            TransformScope.Field, TransformNodeType.DateMathAge, "{}",
            expectedValueType: MappingValueType.Integer));

        var request = ValidRequest(fields:
        [
            new MappingFieldDto("BirthDateAge", "$.birthDate", MappingValueType.Date, false, null, null),
        ]);

        (await _sut.ValidateAsync(request)).IsValid.Should().BeTrue();
    }
}
