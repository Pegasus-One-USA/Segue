using FHIRBridge.Application.Security;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Auth;

public sealed class PingGroupRoleMapperTests
{
    private static readonly PingGroupRoleMapping[] Mappings =
    [
        new("group-admins", "Admin"),
        new("Clinical Viewers", "Audit"),
        new("group-ops", "Operations")
    ];

    [Fact]
    public void MapInOrder_returns_roles_in_mapping_order_not_group_order()
    {
        // The user's groups come back from Ping in an arbitrary order; mapping order decides priority.
        var roles = PingGroupRoleMapper.MapInOrder(["group-ops", "clinical viewers", "group-admins"], Mappings);

        roles.Should().Equal("Admin", "Audit", "Operations");
    }

    [Fact]
    public void MapInOrder_ignores_unmapped_groups_so_one_mapped_group_gives_one_role()
    {
        var roles = PingGroupRoleMapper.MapInOrder(["random-1", "group-ops", "random-2"], Mappings);

        roles.Should().Equal("Operations");
    }

    [Fact]
    public void MapInOrder_returns_a_role_once_even_if_two_groups_map_to_it()
    {
        IReadOnlyList<PingGroupRoleMapping> mappings = [new("g1", "Admin"), new("g2", "Admin")];

        PingGroupRoleMapper.MapInOrder(["g2", "g1"], mappings).Should().Equal("Admin");
    }

    [Fact]
    public void Parse_reads_the_ordered_array_form()
    {
        var rows = PingGroupRoleMapper.Parse("""[{"group":"a","role":"Admin"},{"group":"b","role":"Audit"}]""");

        rows.Should().Equal(new PingGroupRoleMapping("a", "Admin"), new PingGroupRoleMapping("b", "Audit"));
    }

    [Fact]
    public void Parse_still_reads_the_earlier_object_forms_keeping_order_and_taking_the_first_role()
    {
        var rows = PingGroupRoleMapper.Parse("""{"b":["Audit","Admin"],"a":"Admin"}""");

        rows.Should().Equal(new PingGroupRoleMapping("b", "Audit"), new PingGroupRoleMapping("a", "Admin"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("42")]
    public void Parse_returns_null_for_missing_or_malformed_values_so_callers_fall_back(string? json)
    {
        PingGroupRoleMapper.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Serialize_then_Parse_round_trips_the_order()
    {
        var json = PingGroupRoleMapper.Serialize(Mappings);

        PingGroupRoleMapper.Parse(json).Should().Equal(Mappings);
    }
}
