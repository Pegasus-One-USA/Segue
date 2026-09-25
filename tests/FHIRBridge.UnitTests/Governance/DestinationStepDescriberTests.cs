using FHIRBridge.Application.Governance;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

/// <summary>
/// The Step column's wording, derived at read time. The distinction that carries the most weight here is a
/// partial write reading differently from a whole one — "33 of 50" versus "3 records written" — because that is
/// the difference between a healthy run and a silently lossy one at a glance.
/// </summary>
public sealed class DestinationStepDescriberTests
{
    [Fact]
    public void A_whole_write_states_the_count_once()
    {
        DestinationStepDescriber.Describe("Complete", "Succeeded", 3, 3, null)
            .Should().Be("Completed — 3 record(s) written");
    }

    [Fact]
    public void A_partial_write_names_both_halves()
    {
        DestinationStepDescriber.Describe("Complete", "PartialSuccess", 50, 33, null)
            .Should().Be("Completed — 33 of 50 record(s) written");
    }

    [Fact]
    public void An_empty_batch_says_so_rather_than_claiming_a_write()
    {
        DestinationStepDescriber.Describe("Complete", "NoData", 0, 0, null)
            .Should().Be("Completed — nothing to write");
    }

    [Fact]
    public void A_successful_connect_names_the_half_it_belongs_to()
    {
        // A Fabric Warehouse load connects twice, against different audiences; "Connected" alone would leave the
        // reader unable to tell which one a row refers to.
        DestinationStepDescriber.Describe("Connect", "Succeeded", null, null, "Warehouse SQL")
            .Should().Be("Connected — Warehouse SQL");
    }

    [Fact]
    public void A_connect_without_detail_stays_unadorned()
    {
        DestinationStepDescriber.Describe("Connect", "Succeeded", null, null, null)
            .Should().Be("Connected");
    }

    [Fact]
    public void A_failed_connect_reads_as_a_connection_problem_not_a_write_problem()
    {
        DestinationStepDescriber.Describe("Connect", "Failed", null, null, "Warehouse SQL")
            .Should().Be("Could not connect — Warehouse SQL");
    }

    [Fact]
    public void A_failed_write_reads_as_a_write_problem()
    {
        DestinationStepDescriber.Describe("Complete", "Failed", 50, 0, null)
            .Should().Be("Write failed");
    }

    [Fact]
    public void Unknown_values_fall_back_rather_than_throwing()
    {
        // Rows are append-only and long-lived: a stage or status written by a future version must still render.
        DestinationStepDescriber.Describe("Something", "Else", null, null, null)
            .Should().Be("Destination activity");
    }
}
