using System.Net;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// Regression coverage for the Node Catalog consolidation's authorization bug: GET /api/v1/permissions/
/// node-catalog used to inherit PermissionsController's class-level [Authorize(UnifiedAdmin)] attribute,
/// rejecting any non-SuperAdmin/Admin role with 403 regardless of what permissions it held — breaking
/// the Workflow Builder Node Library for every ordinary role. GetAll/GetCatalog must stay admin-only.
/// </summary>
[Collection("ApiTests")]
public sealed class NodeCatalogAuthorizationTests(ApiFixture f)
{
    [Fact]
    public async Task GET_permissions_catalog__admin__still_returns_200()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/permissions/catalog");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GET_permissions_catalog__non_admin_role_with_epic_view__still_returns_403()
    {
        // Preserves GetCatalog's existing admin-only behavior — a non-admin role must NOT be able to
        // reach it just because it holds an unrelated node permission.
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("epic.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync("/api/v1/permissions/catalog");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task GET_node_catalog__non_admin_role_with_only_epic_view__returns_200()
    {
        // The actual regression: a non-admin role (e.g. "test") must be able to reach the Node Catalog
        // regardless of the UnifiedAdmin policy that still gates GetAll/GetCatalog.
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("epic.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync("/api/v1/permissions/node-catalog");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GET_node_catalog__non_admin_role_with_no_permissions_at_all__still_returns_200()
    {
        // Confirms the endpoint describes availability regardless of the caller's own node permissions
        // (per the explicit requirement: the catalog must be reachable even without epic.view/medplum.view —
        // Angular applies the user's permissions client-side to decide visibility).
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync("user.view");
        using var client = f.CreateAuthenticatedClient(jwt);

        var resp = await client.GetAsync("/api/v1/permissions/node-catalog");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GET_node_catalog__unauthenticated__returns_401_not_403()
    {
        var resp = await f.AnonClient.GetAsync("/api/v1/permissions/node-catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task GET_node_catalog__response_contains_Epic_and_Medplum_with_their_own_permission_group()
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/permissions/node-catalog");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        var epic = doc.EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "Source" && e.GetProperty("type").GetString() == "Epic");
        Assert.True(epic.GetProperty("implemented").GetBoolean());
        Assert.Equal("Epic", epic.GetProperty("permissionGroup").GetString());
        var epicActions = epic.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToArray();
        Assert.Contains("epic.view", epicActions);

        var medplum = doc.EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "Destination" && e.GetProperty("type").GetString() == "Medplum");
        Assert.True(medplum.GetProperty("implemented").GetBoolean());
        Assert.Equal("Medplum", medplum.GetProperty("permissionGroup").GetString());
        var medplumActions = medplum.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToArray();
        Assert.Contains("medplum.view", medplumActions);
    }

    [Theory]
    [InlineData("Source", "Sample")]
    public async Task GET_node_catalog__real_vendor_sources_are_implemented(string kind, string type)
    {
        // Sample has a dedicated SOURCE_FORM_REGISTRY form, a real save path, and real runtime
        // execution support (FhirSourceClientFactory.DefaultRegistrations) — the complete lifecycle.
        var resp = await f.AdminClient.GetAsync("/api/v1/permissions/node-catalog");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        var entry = doc.EnumerateArray().Single(e => e.GetProperty("kind").GetString() == kind && e.GetProperty("type").GetString() == type);
        Assert.True(entry.GetProperty("implemented").GetBoolean());
    }

    [Theory]
    [InlineData("Source", "NewEHR")]
    [InlineData("Source", "NewEHRTwo")]
    [InlineData("Source", "Cerner")]
    [InlineData("Source", "Allscripts")]
    [InlineData("Source", "Healow")]
    [InlineData("Source", "MeditechGreenfield")]
    [InlineData("Source", "Hl7v2")]
    [InlineData("Destination", "AzureSql")]
    [InlineData("Destination", "Sftp")]
    public async Task GET_node_catalog__incomplete_lifecycle_types_are_not_implemented(string kind, string type)
    {
        // NewEHR/NewEHRTwo have no real form at all. AzureSql/Sftp have a form reachable via the
        // existing-connection picker, but the wizard's save path always persists SqlServer/Csv instead.
        // Cerner/Allscripts/Healow/MeditechGreenfield configure/save/reload correctly (the
        // buildSource() fix persists their real SourceSystemType) but cannot execute —
        // FhirSourceClientFactory.DefaultRegistrations has no client registered for any of them. Hl7v2
        // fails earlier still: its MLLP host/port configuration has no representation in the
        // SourceConnection model at all. Implemented must stay false for all nine even though each
        // carries real RBAC permissions (e.g. cerner.view/create/edit/delete/execute) — a permission
        // group existing, or even a working save, is never sufficient on its own to mark a node
        // implemented; the complete lifecycle (Configure -> Save -> Reload -> Execute) must hold.
        var resp = await f.AdminClient.GetAsync("/api/v1/permissions/node-catalog");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        var entry = doc.EnumerateArray().Single(e => e.GetProperty("kind").GetString() == kind && e.GetProperty("type").GetString() == type);
        Assert.False(entry.GetProperty("implemented").GetBoolean());
    }

    [Theory]
    [InlineData("Cerner")]
    [InlineData("Allscripts")]
    [InlineData("Healow")]
    [InlineData("MeditechGreenfield")]
    [InlineData("Hl7v2")]
    public async Task GET_node_catalog__permission_group_and_actions_survive_Implemented_false(string type)
    {
        // The requirement this test guards explicitly: Implemented=false must never remove or hide a
        // node's own RBAC permissions from the catalog — Permission Catalog and Node Catalog are
        // separate axes. The full view/create/edit/delete/execute set must still be present and
        // attributed to this node's own permission group.
        var resp = await f.AdminClient.GetAsync("/api/v1/permissions/node-catalog");
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        var entry = doc.EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "Source" && e.GetProperty("type").GetString() == type);
        Assert.False(entry.GetProperty("implemented").GetBoolean());
        Assert.Equal(type, entry.GetProperty("permissionGroup").GetString());
        var actions = entry.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToArray();
        var prefix = type.ToLowerInvariant();
        Assert.Contains($"{prefix}.view", actions);
        Assert.Contains($"{prefix}.create", actions);
        Assert.Contains($"{prefix}.edit", actions);
        Assert.Contains($"{prefix}.delete", actions);
        Assert.Contains($"{prefix}.execute", actions);
    }
}
