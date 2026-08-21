using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Rbac.NodeCatalog;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// Exercises the exact join logic <c>RoleManagementService.GetNodeCatalogAsync</c> performs at
/// request time — given the same shape of data <c>GetPermissionCatalogAsync</c> returns (this test
/// hand-builds it rather than hitting a real DB, since the join itself, not persistence, is what
/// could silently drift). See the Node Catalog consolidation's own design notes for why this
/// endpoint must never fall back to <c>sourceconnections.*</c> for a type with no dedicated group.
/// </summary>
public sealed class NodeCatalogBuilderTests
{
    /// <summary>
    /// Builds a Pipelines-category catalog slice containing exactly the groups given — mirrors what
    /// <c>GetPermissionCatalogAsync</c> would return for those groups, including Epic's legacy
    /// <c>.read</c>/<c>.assign</c> actions when asked, to prove the builder excludes them itself
    /// rather than depending on the DB never returning them.
    /// </summary>
    private static IReadOnlyList<PermissionCatalogCategoryDto> PipelinesCatalog(
        params (string GroupName, string[] Actions)[] groups)
    {
        var groupDtos = groups.Select(g => new PermissionCatalogGroupDto(
            Guid.NewGuid(),
            g.GroupName,
            g.GroupName,
            g.Actions.Select(a => new PermissionDto(
                Guid.NewGuid(),
                $"{g.GroupName.ToLowerInvariant()}.{a}",
                a,
                $"{a} {g.GroupName}",
                Guid.NewGuid(),
                IsVisible: true)).ToArray())).ToArray();

        return new[]
        {
            new PermissionCatalogCategoryDto(Guid.NewGuid(), "Pipelines", "Workflows", groupDtos),
        };
    }

    [Fact]
    public void NodeCatalogMetadata_covers_every_SourceSystemType_and_DestinationType_value()
    {
        // Guards the exact scenario the Node Catalog consolidation was told to prevent: a new enum
        // value silently producing an incomplete node. See NodeCatalogMetadata.ValidateCompleteness.
        var act = NodeCatalogMetadata.ValidateCompleteness;

        act.Should().NotThrow(
            "every SourceSystemType/DestinationType member needs a NodeCatalogMetadata entry — " +
            "add one before adding the enum member, not after");
    }

    [Fact]
    public void Build_produces_exactly_one_entry_per_enum_value()
    {
        var entries = NodeCatalogBuilder.Build(PipelinesCatalog());

        var expectedCount = Enum.GetValues<SourceSystemType>().Length + Enum.GetValues<DestinationType>().Length;
        entries.Should().HaveCount(expectedCount);
    }

    [Fact]
    public void Epic_resolves_its_own_dedicated_group_and_excludes_legacy_read_and_assign()
    {
        var catalog = PipelinesCatalog(("Epic", new[] { "view", "create", "edit", "delete", "execute", "read", "assign" }));

        var epic = NodeCatalogBuilder.Build(catalog).Single(e => e.Kind == "Source" && e.Type == "Epic");

        epic.PermissionGroup.Should().Be("Epic");
        epic.Actions.Select(a => a.Code).Should().BeEquivalentTo(
            "epic.view", "epic.create", "epic.edit", "epic.delete", "epic.execute");
        epic.Actions.Should().NotContain(a => a.Code == "epic.read" || a.Code == "epic.assign");
        epic.Implemented.Should().BeTrue("Epic has a real, working connection wizard and save path");
    }

    [Fact]
    public void FhirRepository_and_Medplum_resolve_their_own_dedicated_groups_not_sourceconnections()
    {
        var catalog = PipelinesCatalog(
            ("FhirRepository", new[] { "view", "create", "edit", "delete", "execute" }),
            ("Medplum", new[] { "view", "create", "edit", "delete", "execute" }));

        var entries = NodeCatalogBuilder.Build(catalog);
        var aidbox = entries.Single(e => e.Kind == "Destination" && e.Type == "FhirRepository");
        var medplum = entries.Single(e => e.Kind == "Destination" && e.Type == "Medplum");

        aidbox.PermissionGroup.Should().Be("FhirRepository");
        aidbox.Actions.Select(a => a.Code).Should().BeEquivalentTo(
            "fhirrepository.view", "fhirrepository.create", "fhirrepository.edit", "fhirrepository.delete", "fhirrepository.execute");
        aidbox.DisplayName.Should().Be("Aidbox");
        aidbox.Implemented.Should().BeTrue("FhirRepository/Aidbox has a real, working save path");

        medplum.PermissionGroup.Should().Be("Medplum");
        medplum.Actions.Select(a => a.Code).Should().BeEquivalentTo(
            "medplum.view", "medplum.create", "medplum.edit", "medplum.delete", "medplum.execute");
        medplum.Implemented.Should().BeTrue("Medplum has a real, working save path");
    }

    [Fact]
    public void A_type_with_no_dedicated_group_has_null_permission_group_and_no_actions()
    {
        // Snowflake deliberately has no PermissionGroupCode member — the catalog passed in below has
        // no "Snowflake" group at all, exactly as GetPermissionCatalogAsync would return today.
        var entries = NodeCatalogBuilder.Build(PipelinesCatalog());

        var snowflake = entries.Single(e => e.Kind == "Destination" && e.Type == "Snowflake");

        snowflake.PermissionGroup.Should().BeNull();
        snowflake.Actions.Should().BeEmpty();
        snowflake.Implemented.Should().BeFalse("Snowflake has no real form or save path — not in the implemented set");
    }

    [Fact]
    public void Implemented_set_matches_the_investigated_evidence()
    {
        // See the Node Catalog investigation report: Implemented means the COMPLETE lifecycle —
        // Configure -> Save -> Reload -> Execute — not merely a form that saves successfully. Cerner/
        // Allscripts/Healow/MeditechGreenfield/Hl7v2 were removed from this set once the runtime
        // execution gap was confirmed (FhirSourceClientFactory.DefaultRegistrations has no client for
        // any of them) — see NodeCatalogMetadata.cs's own per-entry comments for each one's specific
        // reason. This is deliberately NOT a phase/rollout allowlist — see
        // NodeCatalogMetadata.Entry.Implemented's doc comment.
        var entries = NodeCatalogBuilder.Build(PipelinesCatalog());

        var implemented = entries.Where(e => e.Implemented).Select(e => e.Type).ToArray();

        implemented.Should().BeEquivalentTo(new[]
        {
            "Epic", "Athenahealth", "GenericFhir", "Sample",
            "SqlServer", "Csv", "MySql", "Mongo", "PostgreSql", "FhirRepository", "Medplum", "BlobStorage",
        });
    }

    [Fact]
    public void NewEHR_and_AzureSql_and_Sftp_are_not_implemented_despite_having_permission_groups()
    {
        // Guards exactly the distinction the investigation flagged: a permission group (and therefore
        // real RBAC permissions like newehr.view) existing is never sufficient on its own to mark a
        // node implemented. AzureSql/Sftp additionally have a form component reachable via the
        // existing-connection picker, but the wizard's own save path always persists SqlServer/Csv
        // instead — so neither is genuinely creatable yet either.
        var catalog = PipelinesCatalog(
            ("NewEHR", new[] { "view", "create", "edit", "delete", "execute" }),
            ("AzureSql", new[] { "view", "create", "edit", "delete", "execute" }),
            ("Sftp", new[] { "view", "create", "edit", "delete", "execute" }));

        var entries = NodeCatalogBuilder.Build(catalog);

        entries.Single(e => e.Kind == "Source" && e.Type == "NewEHR").Implemented.Should().BeFalse();
        entries.Single(e => e.Kind == "Source" && e.Type == "NewEHRTwo").Implemented.Should().BeFalse();
        entries.Single(e => e.Kind == "Destination" && e.Type == "AzureSql").Implemented.Should().BeFalse();
        entries.Single(e => e.Kind == "Destination" && e.Type == "Sftp").Implemented.Should().BeFalse();
    }

    [Fact]
    public void Cerner_Allscripts_Healow_MeditechGreenfield_Hl7v2_are_not_implemented_despite_having_full_permission_groups()
    {
        // Configure/Save/Reload genuinely work for the first four (the buildSource() fix persists the
        // correct SourceSystemType for each) — but Execute does not, since FhirSourceClientFactory has
        // no registered client for any of them. Hl7v2 fails earlier still: its MLLP configuration has
        // no representation in the SourceConnection model at all. All five keep their full
        // view/create/edit/delete/execute permission groups regardless — Implemented is a separate axis
        // from the Permission Catalog and must never cause a permission to be removed.
        var catalog = PipelinesCatalog(
            ("Cerner", new[] { "view", "create", "edit", "delete", "execute" }),
            ("Allscripts", new[] { "view", "create", "edit", "delete", "execute" }),
            ("Healow", new[] { "view", "create", "edit", "delete", "execute" }),
            ("MeditechGreenfield", new[] { "view", "create", "edit", "delete", "execute" }),
            ("Hl7v2", new[] { "view", "create", "edit", "delete", "execute" }));

        var entries = NodeCatalogBuilder.Build(catalog);

        foreach (var type in new[] { "Cerner", "Allscripts", "Healow", "MeditechGreenfield", "Hl7v2" })
        {
            var entry = entries.Single(e => e.Kind == "Source" && e.Type == type);
            entry.Implemented.Should().BeFalse($"{type} lacks complete lifecycle support");
            entry.PermissionGroup.Should().Be(type, $"{type}'s permission group must survive Implemented=false unchanged");
            entry.Actions.Select(a => a.Code).Should().BeEquivalentTo(
                $"{type.ToLowerInvariant()}.view", $"{type.ToLowerInvariant()}.create",
                $"{type.ToLowerInvariant()}.edit", $"{type.ToLowerInvariant()}.delete", $"{type.ToLowerInvariant()}.execute");
        }
    }
}
