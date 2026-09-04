using System.Reflection;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Sources;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Sources;

/// <summary>
/// The page cap the runtime resolver hands the connectors used to be a bare "5". With the default 100-record page
/// that silently capped EVERY resource type at 500 records — no error, no PartialSuccess, just a short count, and
/// no setting anywhere to raise it (there is no MaxPages column). It is now derived from the two settings the
/// operator actually controls: Max Records Per Run and Page Size.
/// </summary>
public sealed class SourcePageCapDerivationTests
{
    private const int Backstop = 1000;

    private static int ResolveMaxPages(int pageSize, int? maxRecordsPerRun)
    {
        var method = typeof(SourceConnectionRuntimeResolver)
            .GetMethod("ResolveMaxPages", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (int)method.Invoke(null, [pageSize, maxRecordsPerRun])!;
    }

    [Fact]
    public void No_record_cap_means_page_to_completion_not_five_pages()
    {
        // The regression proper: an operator who set no record cap asked for everything, so nothing may be
        // dropped at 500 records.
        ResolveMaxPages(pageSize: 100, maxRecordsPerRun: null).Should().Be(Backstop);
        ResolveMaxPages(pageSize: 100, maxRecordsPerRun: null).Should().BeGreaterThan(5);
    }

    [Theory]
    // Exactly divisible: 500 records over 100-record pages needs 5 pages.
    [InlineData(100, 500, 5)]
    // Not divisible: the partial last page still has to be fetched, or the cap is unreachable.
    [InlineData(100, 250, 3)]
    [InlineData(100, 101, 2)]
    // A cap smaller than one page still needs that page.
    [InlineData(100, 1, 1)]
    // A larger page size reaches the same cap in fewer requests.
    [InlineData(500, 1000, 2)]
    public void Page_cap_is_the_pages_needed_to_reach_the_record_cap(int pageSize, int maxRecords, int expected)
    {
        ResolveMaxPages(pageSize, maxRecords).Should().Be(expected);
    }

    [Fact]
    public void A_record_cap_beyond_the_backstop_is_clamped_to_it()
    {
        // The backstop guards against a server handing out next links forever; it must not be exceeded even by a
        // very large operator cap.
        ResolveMaxPages(pageSize: 1, maxRecordsPerRun: 10_000_000).Should().Be(Backstop);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_nonsensical_page_size_falls_back_to_the_backstop_rather_than_dividing_by_it(int pageSize)
    {
        ResolveMaxPages(pageSize, maxRecordsPerRun: 500).Should().Be(Backstop);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_nonpositive_record_cap_is_treated_as_no_cap(int maxRecordsPerRun)
    {
        ResolveMaxPages(pageSize: 100, maxRecordsPerRun).Should().Be(Backstop);
    }

    [Fact]
    public void Retrieval_configuration_round_trips_the_settings_the_cap_is_derived_from()
    {
        // Guards the two properties the derivation reads, so a rename/removal fails here rather than silently
        // reverting the cap to its backstop.
        var retrieval = new SourceRetrievalConfiguration(
            "search-rest", ["Observation"], null, incrementalSyncEnabled: false,
            pageSize: 100, maxRecordsPerRun: 250);

        retrieval.PageSize.Should().Be(100);
        retrieval.MaxRecordsPerRun.Should().Be(250);
        ResolveMaxPages(retrieval.PageSize ?? 100, retrieval.MaxRecordsPerRun).Should().Be(3);
    }
}
