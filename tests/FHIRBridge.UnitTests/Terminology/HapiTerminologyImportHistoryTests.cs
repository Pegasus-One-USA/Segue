using FHIRBridge.Domain.Entities.Terminology;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Terminology;

/// <summary>
/// Guards the Version column's width. The publishers of these code systems choose their own version
/// strings and several are far longer than the 32-character column: CMS versions HCPCS by its release
/// filename. An overlong value threw a 22001 truncation error out of SaveChangesAsync *after* every
/// concept had already been imported, turning a successful run into a Failed one — and, because the
/// terminal status is what the portal's SignalR push carries, showing the user a failure for an import
/// that had in fact worked.
/// </summary>
public sealed class HapiTerminologyImportHistoryTests
{
    [Fact]
    public void Complete_truncates_a_version_longer_than_the_column()
    {
        var history = new HapiTerminologyImportHistory("Hcpcs");

        // The exact string CMS served in October 2026 — 41 characters into a varchar(32).
        history.Complete(9154, "october-2026-alpha-numeric-hcpcs-file.zip");

        history.Version.Should().HaveLength(32);
        history.Status.Should().Be("Succeeded", "truncating provenance text must never demote a real import");
        history.ImportedConceptCount.Should().Be(9154);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026AA")]
    public void Complete_leaves_a_version_that_already_fits_untouched(string? version)
    {
        var history = new HapiTerminologyImportHistory("RxNorm");

        history.Complete(10, version);

        history.Version.Should().Be(version);
    }

    /// <summary>
    /// The stored version is read back out of a varchar(32) column, so it has already been clipped. Anything
    /// deciding "is an update available" must clip the freshly fetched version the same way before comparing
    /// them — otherwise the 41-character upstream filename never equals the 32-character stored one, the
    /// answer is permanently "yes", and the portal re-downloads and re-imports the whole code system on every
    /// page load. That regression was observed: HCPCS and ICD-10-PCS each imported twice within three minutes,
    /// while MeSH (version "2026", short enough to survive intact) correctly imported once.
    /// </summary>
    [Fact]
    public void A_clipped_stored_version_compares_equal_to_its_own_clipped_source()
    {
        const string upstream = "october-2026-alpha-numeric-hcpcs-file.zip";
        var history = new HapiTerminologyImportHistory("Hcpcs");
        history.Complete(9154, upstream);

        HapiTerminologyImportHistory.ClipVersion(upstream).Should().Be(history.Version,
            "re-scanning an unchanged source must not look like a new version");
    }

    [Fact]
    public void ClipVersion_leaves_a_genuinely_new_version_different()
    {
        var history = new HapiTerminologyImportHistory("Hcpcs");
        history.Complete(9154, "october-2026-alpha-numeric-hcpcs-file.zip");

        // A real new release must still register as a change — the clip must not collapse distinct versions
        // into the same 32 characters by truncating away the part that differs.
        HapiTerminologyImportHistory.ClipVersion("january-2027-alpha-numeric-hcpcs-file.zip")
            .Should().NotBe(history.Version);
    }
}
