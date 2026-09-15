using FHIRBridge.Infrastructure.Terminology.Hapi;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Terminology;

/// <summary>
/// Guards the HCPCS release zip's data-file lookup. CMS renames this file between quarters, and an
/// earlier exact "_ANWEB.txt" suffix match broke the whole sync the first time a quarter shipped a
/// revision suffix — the entry names below are the real contents of the October 2026 release.
/// </summary>
public sealed class HapiHcpcsDataFileSelectionTests
{
    private static readonly string[] October2026ReleaseEntries =
    {
        "HCPC2026_OCT_ANWEB_v2.txt",
        "HCPC2026_OCT_ANWEB_v2.xlsx",
        "HCPC2026_OCT_Corrections_09102026.xlsx",
        "HCPC2026_recordlayout.txt",
        "NOC codes_OCT2026.xlsx",
        "proc_notes_OCT2026.txt",
        "HCPC2026_OCT_ANWEB_Transaction Report_v2.xlsx",
    };

    [Fact]
    public void Selects_the_revised_data_file_from_the_real_october_2026_release()
    {
        var selected = HapiHcpcsTerminologySyncService.SelectDataFileName(October2026ReleaseEntries);

        selected.Should().Be("HCPC2026_OCT_ANWEB_v2.txt");
    }

    [Fact]
    public void Selects_the_data_file_when_the_quarter_ships_no_revision_suffix()
    {
        var selected = HapiHcpcsTerminologySyncService.SelectDataFileName(
            new[] { "HCPC2026_JUL_ANWEB.txt", "HCPC2026_recordlayout.txt", "proc_notes_JUL2026.txt" });

        selected.Should().Be("HCPC2026_JUL_ANWEB.txt");
    }

    [Fact]
    public void Prefers_the_highest_revision_when_several_ship_together()
    {
        var selected = HapiHcpcsTerminologySyncService.SelectDataFileName(
            new[] { "HCPC2026_OCT_ANWEB_v1.txt", "HCPC2026_OCT_ANWEB_v2.txt" });

        selected.Should().Be("HCPC2026_OCT_ANWEB_v2.txt");
    }

    [Fact]
    public void Ignores_the_record_layout_document_and_non_text_copies()
    {
        var selected = HapiHcpcsTerminologySyncService.SelectDataFileName(
            new[] { "HCPC2026_recordlayout.txt", "HCPC2026_OCT_ANWEB_v2.xlsx", "NOC codes_OCT2026.xlsx" });

        selected.Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_no_data_file_is_present()
    {
        var selected = HapiHcpcsTerminologySyncService.SelectDataFileName(Array.Empty<string>());

        selected.Should().BeNull();
    }
}
