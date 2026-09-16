using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// A relational destination writes each resource type as its own group, ordered so a group another group's FK
/// resolves against goes first — otherwise MappedSqlServerDestinationWriter's lookup finds no row and the whole
/// write throws.
///
/// The table a group is written to comes from the destination node's own resourceMappings config, but each
/// record also carries a DestinationObject stamped by MappingNodeExecutor from the MappingProfiles row its
/// mappingProfileIds pins. Those are two different stores that drift: renaming a resource's target table updates
/// the node config at once and leaves the MappingProfiles row on the old name. Ordering on the record's stale
/// stamp therefore indexed "dbo.Patient" while both the write and the referencing group's LookupTable had moved
/// to "dbo.Patient_NewMapped" — the dependency silently vanished, Encounter was written first, and the run died
/// on "no row in [dbo].[Patient_NewMapped] has [PatientId] = ...".
/// </summary>
public sealed class DestinationGroupOrderingTests
{
    private static MappedDestinationRecord Record(
        string resourceType, string destinationObject, MappedReferenceLookup[]? lookups = null) =>
        new(Guid.NewGuid(), resourceType, destinationObject, $"{resourceType}-1",
            new Dictionary<string, object?>(), ReferenceLookups: lookups);

    private static MappingProfile Profile(string resourceType, string destinationObject) =>
        new(resourceType, resourceType, Guid.Empty, Guid.Empty, destinationObject, []);

    private static List<IGrouping<string, MappedDestinationRecord>> GroupsOf(params MappedDestinationRecord[] records) =>
        records.GroupBy(r => r.ResourceType, StringComparer.OrdinalIgnoreCase).ToList();

    [Fact]
    public void Referenced_group_is_ordered_first_when_the_records_carry_a_stale_table_name()
    {
        // Records still say "dbo.Patient" (stale MappingProfiles row); the node config — and so the write, and
        // Encounter's own lookup — say "dbo.Patient_NewMapped". This is the renamed-table case.
        var encounter = Record("Encounter", "dbo.Encounter",
            [new MappedReferenceLookup("PatientId", "Patient_NewMapped", "PatientId", "anon-1")]);
        var patient = Record("Patient", "dbo.Patient");

        // Encounter first in the input, so a no-op sort would leave it first and reproduce the failure.
        var groups = GroupsOf(encounter, patient);
        var profiles = new Dictionary<string, MappingProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Patient"] = Profile("Patient", "dbo.Patient_NewMapped;mode=upsert"),
            ["Encounter"] = Profile("Encounter", "dbo.Encounter;mode=upsert"),
        };

        var ordered = DestinationNodeExecutor.OrderGroupsByReferenceDependency(groups, profiles);

        ordered.Select(g => g.Key).Should().Equal("Patient", "Encounter");
    }

    [Fact]
    public void Referenced_group_is_ordered_first_when_the_names_already_agree()
    {
        // The unchanged-table case must keep working exactly as before.
        var observation = Record("Observation", "dbo.Observations",
            [new MappedReferenceLookup("PatientId", "Patient", "PatientId", "p-1")]);
        var patient = Record("Patient", "dbo.Patient");

        var groups = GroupsOf(observation, patient);
        var profiles = new Dictionary<string, MappingProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Patient"] = Profile("Patient", "dbo.Patient;mode=upsert"),
            ["Observation"] = Profile("Observation", "dbo.Observations;mode=upsert"),
        };

        var ordered = DestinationNodeExecutor.OrderGroupsByReferenceDependency(groups, profiles);

        ordered.Select(g => g.Key).Should().Equal("Patient", "Observation");
    }

    [Fact]
    public void Falls_back_to_the_records_own_table_when_a_resource_type_has_no_node_config_profile()
    {
        // The legacy single-profile path: resourceMappings carries nothing for this resource type, so the
        // record's own stamp is the only table name available and must still order the groups.
        var encounter = Record("Encounter", "dbo.Encounter",
            [new MappedReferenceLookup("PatientId", "Patient", "PatientId", "p-1")]);
        var patient = Record("Patient", "dbo.Patient");

        var ordered = DestinationNodeExecutor.OrderGroupsByReferenceDependency(
            GroupsOf(encounter, patient),
            new Dictionary<string, MappingProfile>(StringComparer.OrdinalIgnoreCase));

        ordered.Select(g => g.Key).Should().Equal("Patient", "Encounter");
    }

    [Fact]
    public void Every_group_is_returned_exactly_once_when_the_referenced_table_is_absent()
    {
        // Nothing in this write targets the referenced table — there is no ordering to apply (the writer will
        // fail on the lookup instead, and the dropped edge is logged), but no group may be lost or duplicated.
        var encounter = Record("Encounter", "dbo.Encounter",
            [new MappedReferenceLookup("PatientId", "SomeOtherTable", "PatientId", "p-1")]);
        var patient = Record("Patient", "dbo.Patient");

        var ordered = DestinationNodeExecutor.OrderGroupsByReferenceDependency(
            GroupsOf(encounter, patient),
            new Dictionary<string, MappingProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Patient"] = Profile("Patient", "dbo.Patient"),
                ["Encounter"] = Profile("Encounter", "dbo.Encounter"),
            });

        ordered.Select(g => g.Key).Should().BeEquivalentTo(["Patient", "Encounter"]);
        ordered.Should().HaveCount(2);
    }

    /// <summary>
    /// The dropped edge has to be reported to the CALLER, not just logged: every destination executor builds its
    /// base with no logger factory, so Logger is NullLogger and the warning reached nobody. Without it the run
    /// surfaces only the writer's "The referenced resource must be written before this one, in the same
    /// destination write" — which describes an ordering bug and sends you looking for one, when the actual
    /// condition is that the prerequisite resource type is not in this write at all. That is a different fix
    /// (include the resource type, or accept that its rows must pre-exist), and nothing in the run said so.
    /// </summary>
    [Fact]
    public void A_reference_to_a_table_outside_this_write_is_reported_to_the_caller()
    {
        var observation = Record("Observation", "dbo.Observation",
            [new MappedReferenceLookup("EncounterId", "Encounter", "EncounterId", "anon-625e79d324a4ff7b")]);
        var patient = Record("Patient", "dbo.Patient");

        var gaps = new List<string>();
        DestinationNodeExecutor.OrderGroupsByReferenceDependency(
            GroupsOf(observation, patient),
            new Dictionary<string, MappingProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Patient"] = Profile("Patient", "dbo.Patient;mode=upsert"),
                ["Observation"] = Profile("Observation", "dbo.Observation;mode=upsert"),
            },
            logger: null,
            unresolvedReferences: gaps);

        gaps.Should().ContainSingle().Which.Should().Contain("Observation").And.Contain("Encounter");
    }

    [Fact]
    public void A_write_whose_references_all_resolve_reports_no_gaps()
    {
        var encounter = Record("Encounter", "dbo.Encounter",
            [new MappedReferenceLookup("PatientId", "Patient", "PatientId", "anon-1")]);
        var patient = Record("Patient", "dbo.Patient");

        var gaps = new List<string>();
        DestinationNodeExecutor.OrderGroupsByReferenceDependency(
            GroupsOf(encounter, patient),
            new Dictionary<string, MappingProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Patient"] = Profile("Patient", "dbo.Patient;mode=upsert"),
                ["Encounter"] = Profile("Encounter", "dbo.Encounter;mode=upsert"),
            },
            logger: null,
            unresolvedReferences: gaps);

        gaps.Should().BeEmpty("a healthy write must not be flagged");
    }

    /// <summary>
    /// Three levels deep (Patient <- Encounter <- Observation) is the shape the real workflow writes, and a
    /// shallow "move referenced first" pass gets it wrong. Nothing covered more than two groups before.
    /// </summary>
    [Fact]
    public void A_transitive_chain_is_ordered_from_the_root_outwards()
    {
        var observation = Record("Observation", "dbo.Observation",
            [new MappedReferenceLookup("EncounterId", "Encounter", "EncounterId", "e-1")]);
        var encounter = Record("Encounter", "dbo.Encounter",
            [new MappedReferenceLookup("PatientId", "Patient", "PatientId", "p-1")]);
        var patient = Record("Patient", "dbo.Patient");

        // Worst-case input order: exactly the reverse of the required write order.
        var ordered = DestinationNodeExecutor.OrderGroupsByReferenceDependency(
            GroupsOf(observation, encounter, patient),
            new Dictionary<string, MappingProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Patient"] = Profile("Patient", "dbo.Patient;mode=upsert"),
                ["Encounter"] = Profile("Encounter", "dbo.Encounter;mode=upsert"),
                ["Observation"] = Profile("Observation", "dbo.Observation;mode=upsert"),
            });

        ordered.Select(g => g.Key).Should().Equal("Patient", "Encounter", "Observation");
    }

    [Fact]
    public void A_self_reference_does_not_stall_the_sort()
    {
        // Patient.link.other → Patient. The group must not depend on itself, and must still be returned.
        var patient = Record("Patient", "dbo.Patient",
            [new MappedReferenceLookup("LinkedPatientId", "Patient", "PatientId", "p-2")]);

        var ordered = DestinationNodeExecutor.OrderGroupsByReferenceDependency(
            GroupsOf(patient),
            new Dictionary<string, MappingProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Patient"] = Profile("Patient", "dbo.Patient;mode=upsert"),
            });

        ordered.Select(g => g.Key).Should().Equal("Patient");
    }
}
