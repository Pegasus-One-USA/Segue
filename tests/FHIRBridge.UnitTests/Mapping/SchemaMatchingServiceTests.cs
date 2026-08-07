using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Mapping;
using FHIRBridge.Application.Services.Mapping;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Mapping;

/// <summary>
/// Covers the destination-driven schema matching engine end to end: the worked Patient example from the
/// spec (dob/mrn/sex/firstName/lastName/mobile → BirthDate/Identifier/Gender/GivenName/FamilyName/Phone),
/// the approved-mapping override short-circuit, and the SaveApprovedMappingAsync insert-vs-update branch.
/// </summary>
public sealed class SchemaMatchingServiceTests
{
    private const string SourceSystem = "Epic";
    private const string ResourceType = "Patient";
    private const string DestinationTable = "Patient";

    private static readonly List<FhirFieldMetadata> PatientDestinationFields =
    [
        new("Identifier", MappingValueType.String, IsRequired: true),
        new("GivenName", MappingValueType.String),
        new("FamilyName", MappingValueType.String),
        new("BirthDate", MappingValueType.Date),
        new("Gender", MappingValueType.String),
        new("Phone", MappingValueType.String)
    ];

    private const string PatientSourceJson = """
        {
            "firstName": "John",
            "lastName": "Doe",
            "dob": "1985-04-12",
            "sex": "M",
            "mrn": "E12345",
            "mobile": "+1-555-123-4567"
        }
        """;

    private static (SchemaMatchingService Service, Mock<ISchemaMappingRepository> Repository) CreateSut()
    {
        var repository = new Mock<ISchemaMappingRepository>();
        repository
            .Setup(x => x.GetApprovedAsync(SourceSystem, ResourceType, DestinationTable, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SchemaMapping>)[]);

        return (new SchemaMatchingService(repository.Object), repository);
    }

    [Fact]
    public async Task SuggestMappingsAsync_matches_every_destination_field_for_the_worked_patient_example()
    {
        var (service, _) = CreateSut();
        using var sourceJson = JsonDocument.Parse(PatientSourceJson);

        var suggestions = await service.SuggestMappingsAsync(
            SourceSystem, ResourceType, DestinationTable, sourceJson, PatientDestinationFields);

        suggestions.Should().HaveCount(6);

        suggestions.Single(s => s.DestinationField == "BirthDate").SourceField.Should().Be("$.dob");
        suggestions.Single(s => s.DestinationField == "Gender").SourceField.Should().Be("$.sex");
        suggestions.Single(s => s.DestinationField == "Identifier").SourceField.Should().Be("$.mrn");
        suggestions.Single(s => s.DestinationField == "GivenName").SourceField.Should().Be("$.firstName");
        suggestions.Single(s => s.DestinationField == "FamilyName").SourceField.Should().Be("$.lastName");
        suggestions.Single(s => s.DestinationField == "Phone").SourceField.Should().Be("$.mobile");

        // Every matched field should be well clear of "no match" — the exact confidence numbers depend on the
        // weighted formula, not a fixed oracle (see MappingScorer), but a correctly matched field should never
        // score near zero.
        suggestions.Should().OnlyContain(s => s.Confidence > 0.5);
    }

    [Fact]
    public async Task SuggestMappingsAsync_gives_dob_high_confidence_via_synonym_and_value_pattern()
    {
        var (service, _) = CreateSut();
        using var sourceJson = JsonDocument.Parse(PatientSourceJson);

        var suggestions = await service.SuggestMappingsAsync(
            SourceSystem, ResourceType, DestinationTable, sourceJson, PatientDestinationFields);

        var birthDate = suggestions.Single(s => s.DestinationField == "BirthDate");
        birthDate.SynonymScore.Should().Be(1.0);
        birthDate.ValueScore.Should().Be(1.0);
        birthDate.Confidence.Should().BeGreaterThan(0.60);
        birthDate.Reason.Should().Contain("synonym").And.Contain("date value pattern");
    }

    [Fact]
    public async Task SuggestMappingsAsync_returns_no_match_when_no_source_fields_exist()
    {
        var (service, _) = CreateSut();
        using var emptyJson = JsonDocument.Parse("{}");

        var suggestions = await service.SuggestMappingsAsync(
            SourceSystem, ResourceType, DestinationTable, emptyJson,
            [new FhirFieldMetadata("BirthDate", MappingValueType.Date)]);

        suggestions.Should().ContainSingle();
        suggestions[0].SourceField.Should().BeNull();
        suggestions[0].Confidence.Should().Be(0.0);
    }

    [Fact]
    public async Task SuggestMappingsAsync_short_circuits_on_an_approved_mapping()
    {
        var repository = new Mock<ISchemaMappingRepository>();
        var approved = new SchemaMapping(
            SourceSystem, ResourceType, DestinationTable, "$.mrn", "Identifier", 0.83, SchemaMappingStatus.Approved);
        repository
            .Setup(x => x.GetApprovedAsync(SourceSystem, ResourceType, DestinationTable, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SchemaMapping>)[approved]);

        var service = new SchemaMatchingService(repository.Object);
        using var sourceJson = JsonDocument.Parse(PatientSourceJson);

        var suggestions = await service.SuggestMappingsAsync(
            SourceSystem, ResourceType, DestinationTable, sourceJson,
            [new FhirFieldMetadata("Identifier", MappingValueType.String)]);

        var identifier = suggestions.Single();
        identifier.SourceField.Should().Be("$.mrn");
        identifier.Confidence.Should().Be(1.0);
        identifier.Reason.Should().Be("Approved mapping override");
    }

    [Fact]
    public async Task SaveApprovedMappingAsync_adds_a_new_row_when_none_exists()
    {
        var (service, repository) = CreateSut();
        repository
            .Setup(x => x.FindAsync(SourceSystem, ResourceType, DestinationTable, "Identifier", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SchemaMapping?)null);

        SchemaMapping? added = null;
        repository
            .Setup(x => x.AddAsync(It.IsAny<SchemaMapping>(), It.IsAny<CancellationToken>()))
            .Callback<SchemaMapping, CancellationToken>((m, _) => added = m)
            .Returns(Task.CompletedTask);

        await service.SaveApprovedMappingAsync(
            SourceSystem, ResourceType, DestinationTable,
            [new ApprovedMappingRequest("Identifier", "$.mrn", 0.83)]);

        added.Should().NotBeNull();
        added!.DestinationField.Should().Be("Identifier");
        added.SourceField.Should().Be("$.mrn");
        added.Status.Should().Be(SchemaMappingStatus.Approved);
        repository.Verify(x => x.UpdateAsync(It.IsAny<SchemaMapping>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveApprovedMappingAsync_updates_the_existing_row_in_place_instead_of_duplicating()
    {
        var (service, repository) = CreateSut();
        var existing = new SchemaMapping(SourceSystem, ResourceType, DestinationTable, "$.oldField", "Identifier", 0.5);
        repository
            .Setup(x => x.FindAsync(SourceSystem, ResourceType, DestinationTable, "Identifier", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await service.SaveApprovedMappingAsync(
            SourceSystem, ResourceType, DestinationTable,
            [new ApprovedMappingRequest("Identifier", "$.mrn", 0.95)]);

        existing.SourceField.Should().Be("$.mrn");
        existing.Confidence.Should().Be(0.95);
        existing.Status.Should().Be(SchemaMappingStatus.Approved);
        repository.Verify(x => x.AddAsync(It.IsAny<SchemaMapping>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.UpdateAsync(existing, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetApprovedMappingsAsync_delegates_to_the_repository()
    {
        var repository = new Mock<ISchemaMappingRepository>();
        var approved = new SchemaMapping(SourceSystem, ResourceType, DestinationTable, "$.mrn", "Identifier", 1.0, SchemaMappingStatus.Approved);
        repository
            .Setup(x => x.GetApprovedAsync(SourceSystem, ResourceType, DestinationTable, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SchemaMapping>)[approved]);

        var service = new SchemaMatchingService(repository.Object);

        var result = await service.GetApprovedMappingsAsync(SourceSystem, ResourceType, DestinationTable);

        result.Should().ContainSingle().Which.Should().Be(approved);
    }
}
