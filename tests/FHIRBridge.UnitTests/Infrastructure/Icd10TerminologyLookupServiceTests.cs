using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Terminology;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Regression test: CMS's official ICD-10-CM "order file" (what Icd10ImportService parses) stores codes WITHOUT
/// the decimal point (e.g. "J181"), while every clinical/FHIR source sends the dotted convention ("J18.1") — a
/// plain string match between the two conventions always missed, silently falling back to no display text for
/// every single ICD-10 code regardless of import coverage.
/// </summary>
public sealed class Icd10TerminologyLookupServiceTests
{
    private static FHIRBridgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot())
            .Options;
        return new FHIRBridgeDbContext(options);
    }

    [Fact]
    public async Task Matches_a_dotted_clinical_code_against_a_dot_free_stored_CMS_code()
    {
        using var db = CreateContext();
        db.Icd10Codes.Add(new Icd10Code("J181", 1, true, "Lobar pneumonia NOS", "Lobar pneumonia, unspecified organism", "2026"));
        await db.SaveChangesAsync();

        var result = await new Icd10TerminologyLookupService(db)
            .LookupAsync(Icd10TerminologyLookupService.CanonicalSystem, "J18.1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Display.Should().Be("Lobar pneumonia, unspecified organism");
    }

    [Fact]
    public async Task Matches_when_the_stored_code_already_has_a_dot()
    {
        using var db = CreateContext();
        db.Icd10Codes.Add(new Icd10Code("J18.1", 1, true, "Lobar pneumonia NOS", "Lobar pneumonia, unspecified organism", "2026"));
        await db.SaveChangesAsync();

        var result = await new Icd10TerminologyLookupService(db)
            .LookupAsync(Icd10TerminologyLookupService.CanonicalSystem, "J18.1", CancellationToken.None);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Returns_null_for_a_code_that_genuinely_is_not_in_the_table()
    {
        using var db = CreateContext();
        db.Icd10Codes.Add(new Icd10Code("J181", 1, true, "Lobar pneumonia NOS", "Lobar pneumonia, unspecified organism", "2026"));
        await db.SaveChangesAsync();

        var result = await new Icd10TerminologyLookupService(db)
            .LookupAsync(Icd10TerminologyLookupService.CanonicalSystem, "Z99.99", CancellationToken.None);

        result.Should().BeNull();
    }
}
