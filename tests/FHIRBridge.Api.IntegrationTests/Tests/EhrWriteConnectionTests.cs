using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// Sources only read, destinations only write. EHR write connections are still source connections with write access,
/// listed under Destination Connections: the lists filter on the side a connection serves, giving a connection write
/// access needs the EHR Write-Back right on top of the vendor's own, and a workflow's source node can never change
/// what a connection may write.
/// </summary>
[Collection("ApiTests")]
public sealed class EhrWriteConnectionTests(ApiFixture f)
{
    private static object EpicBody(string name, string? access, bool? activated = null, string? departmentId = null) => new
    {
        Name = name,
        SourceSystemType = "Epic",
        BaseUrl = "https://example.test/fhir",
        ApplicationType = "Backend",
        Access = access,
        VendorWriteApisActivated = activated,
        DepartmentId = departmentId,
        Authentication = new
        {
            AuthenticationType = "SmartBackendServices",
            ClientId = "test-client-id",
            TokenEndpoint = "https://example.test/oauth2/token",
            Scopes = new[] { "system/*.read" },
            KeyId = "test-key-id",
            PrivateKeyKeyVaultName = "test-vault",
            PrivateKeySecretName = "test-secret",
        },
    };

    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<HttpClient> ClientWithAsync(params string[] permissions)
    {
        var (_, _, jwt) = await f.CreateUserWithPermissionsAndLoginAsync(permissions);
        return f.CreateAuthenticatedClient(jwt);
    }

    private async Task<JsonElement> CreateAsAdminAsync(object body)
    {
        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/source-connections", body);
        await ApiFixture.EnsureOkAsync(resp);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Creating_a_connection_with_write_access_needs_the_ehr_write_back_create_right()
    {
        using var vendorOnly = await ClientWithAsync("epic.create");
        using var withWriteBack = await ClientWithAsync("epic.create", "ehrwriteback.create");

        var refused = await vendorOnly.PostAsJsonAsync("/api/v1/source-connections", EpicBody(NewName("w"), "Write"));
        var readOnly = await vendorOnly.PostAsJsonAsync("/api/v1/source-connections", EpicBody(NewName("r"), null));
        var allowed = await withWriteBack.PostAsJsonAsync("/api/v1/source-connections", EpicBody(NewName("w"), "Write"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, readOnly.StatusCode);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task Turning_write_access_on_needs_the_ehr_write_back_edit_right_but_keeping_it_does_not()
    {
        var readName = NewName("r");
        var read = await CreateAsAdminAsync(EpicBody(readName, null));
        var writeName = NewName("rw");
        var write = await CreateAsAdminAsync(EpicBody(writeName, "ReadWrite"));
        using var vendorOnly = await ClientWithAsync("epic.edit");
        using var withWriteBack = await ClientWithAsync("epic.edit", "ehrwriteback.edit");

        var refused = await vendorOnly.PutAsJsonAsync(
            $"/api/v1/source-connections/{read.GetProperty("id").GetGuid()}", EpicBody(readName, "ReadWrite"));
        var kept = await vendorOnly.PutAsJsonAsync(
            $"/api/v1/source-connections/{write.GetProperty("id").GetGuid()}", EpicBody(writeName, "ReadWrite"));
        var allowed = await withWriteBack.PutAsJsonAsync(
            $"/api/v1/source-connections/{read.GetProperty("id").GetGuid()}", EpicBody(readName, "ReadWrite"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var updated = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ReadWrite", updated.GetProperty("access").GetString());
    }

    [Fact]
    public async Task The_lists_filter_on_the_side_a_connection_serves()
    {
        var read = (await CreateAsAdminAsync(EpicBody(NewName("r"), "Read"))).GetProperty("id").GetGuid();
        var write = (await CreateAsAdminAsync(EpicBody(NewName("w"), "Write"))).GetProperty("id").GetGuid();
        var both = (await CreateAsAdminAsync(EpicBody(NewName("rw"), "ReadWrite"))).GetProperty("id").GetGuid();

        async Task<HashSet<Guid>> IdsAsync(string url)
        {
            var resp = await f.AdminClient.GetAsync(url);
            await ApiFixture.EnsureOkAsync(resp);
            var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("items");
            return items.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
        }

        var readable = await IdsAsync("/api/v1/source-connections?access=read");
        var writable = await IdsAsync("/api/v1/source-connections?access=write");
        var all = await IdsAsync("/api/v1/source-connections");
        var writablePaged = await IdsAsync("/api/v1/source-connections/paged?access=write&pageSize=200");
        var readablePaged = await IdsAsync("/api/v1/source-connections/paged?access=read&pageSize=200");

        Assert.Contains(read, readable);
        Assert.Contains(both, readable);
        Assert.DoesNotContain(write, readable);
        Assert.Contains(write, writable);
        Assert.Contains(both, writable);
        Assert.DoesNotContain(read, writable);
        Assert.Superset(new HashSet<Guid> { read, write, both }, all);
        Assert.Contains(write, writablePaged);
        Assert.DoesNotContain(read, writablePaged);
        Assert.Contains(read, readablePaged);
        Assert.DoesNotContain(write, readablePaged);
    }

    [Fact]
    public async Task A_source_node_save_never_changes_what_the_connection_may_write()
    {
        var name = NewName("rw");
        var connection = await CreateAsAdminAsync(EpicBody(name, "ReadWrite", activated: true));
        var connectionId = connection.GetProperty("id").GetGuid();
        var newName = NewName("new");
        var created = await BuildAsync(EpicBody(newName, "Write", activated: true), existingId: null);
        await ApiFixture.EnsureOkAsync(created);

        // An older canvas node still carries Access=Read and no activation.
        var resaved = await BuildAsync(EpicBody(name, "Read", activated: false), connectionId);
        await ApiFixture.EnsureOkAsync(resaved);

        var after = await f.AdminClient.GetAsync("/api/v1/source-connections?access=write");
        var row = JsonDocument.Parse(await after.Content.ReadAsStringAsync()).RootElement
            .EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == connectionId);
        Assert.Equal("ReadWrite", row.GetProperty("access").GetString());
        Assert.True(row.GetProperty("vendorWriteApisActivated").GetBoolean());

        // A connection created from a source node is read-only whatever the node says.
        var all = await f.AdminClient.GetAsync("/api/v1/source-connections");
        var fromNode = JsonDocument.Parse(await all.Content.ReadAsStringAsync()).RootElement
            .EnumerateArray().Single(item => item.GetProperty("name").GetString() == newName);
        Assert.Equal("Read", fromNode.GetProperty("access").GetString());
        Assert.False(fromNode.GetProperty("vendorWriteApisActivated").GetBoolean());
    }

    private static object AthenaBody(
        string name, string? access, string? departmentId = null, bool? activated = null, string baseUrl = "https://athena.example.test/fhir") => new
    {
        Name = name,
        SourceSystemType = "Athenahealth",
        BaseUrl = baseUrl,
        ApplicationType = "Backend",
        Access = access,
        VendorWriteApisActivated = activated,
        DepartmentId = departmentId,
        Authentication = new
        {
            AuthenticationType = "OAuthClientCredentials",
            ClientId = "athena-client-id",
            TokenEndpoint = "https://athena.example.test/oauth2/token",
            Scopes = new[] { "system/Patient.read" },
            ClientSecretKeyVaultName = "test-vault",
            ClientSecretName = "athena-secret",
            PracticeId = "195900",
        },
    };

    private async Task<JsonElement> GetConnectionAsync(Guid id)
    {
        var resp = await f.AdminClient.GetAsync("/api/v1/source-connections");
        await ApiFixture.EnsureOkAsync(resp);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement
            .EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == id).Clone();
    }

    [Fact]
    public async Task Changing_a_write_connections_write_side_settings_needs_the_ehr_write_back_edit_right()
    {
        var name = NewName("rw");
        var id = (await CreateAsAdminAsync(AthenaBody(name, "ReadWrite", departmentId: "1", activated: true))).GetProperty("id").GetGuid();
        using var vendorOnly = await ClientWithAsync("athenahealth.edit");
        using var withWriteBack = await ClientWithAsync("athenahealth.edit", "ehrwriteback.edit");
        var url = $"/api/v1/source-connections/{id}";

        var department = await vendorOnly.PutAsJsonAsync(url, AthenaBody(name, null, departmentId: "2"));
        var deactivate = await vendorOnly.PutAsJsonAsync(url, AthenaBody(name, null, activated: false));
        var downgrade = await vendorOnly.PutAsJsonAsync(url, AthenaBody(name, "Read"));
        // Endpoint and credentials stay under the vendor's own edit right, as for any connection.
        var baseUrl = await vendorOnly.PutAsJsonAsync(url, AthenaBody(name, null, baseUrl: "https://elsewhere.example.test/fhir"));
        var unchanged = await vendorOnly.PutAsJsonAsync(url, AthenaBody(name, null));
        var allowed = await withWriteBack.PutAsJsonAsync(url, AthenaBody(name, null, departmentId: "2"));

        Assert.Equal(HttpStatusCode.Forbidden, department.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deactivate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, downgrade.StatusCode);
        Assert.Equal(HttpStatusCode.OK, baseUrl.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var saved = await GetConnectionAsync(id);
        Assert.Equal("2", saved.GetProperty("departmentId").GetString());
        Assert.Equal("ReadWrite", saved.GetProperty("access").GetString());
        Assert.True(saved.GetProperty("vendorWriteApisActivated").GetBoolean());
    }

    [Fact]
    public async Task Updating_a_missing_connection_with_write_access_answers_not_found()
    {
        using var vendorOnly = await ClientWithAsync("epic.edit");

        var resp = await vendorOnly.PutAsJsonAsync($"/api/v1/source-connections/{Guid.NewGuid()}", EpicBody(NewName("w"), "Write"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_connection_that_can_write_needs_the_ehr_write_back_delete_right()
    {
        var write = (await CreateAsAdminAsync(EpicBody(NewName("w"), "Write"))).GetProperty("id").GetGuid();
        var read = (await CreateAsAdminAsync(EpicBody(NewName("r"), null))).GetProperty("id").GetGuid();
        using var vendorOnly = await ClientWithAsync("sourceconnections.delete", "epic.delete");
        using var withWriteBack = await ClientWithAsync("sourceconnections.delete", "epic.delete", "ehrwriteback.delete");

        var refused = await vendorOnly.DeleteAsync($"/api/v1/source-connections/{write}");
        var readDeleted = await vendorOnly.DeleteAsync($"/api/v1/source-connections/{read}");
        var allowed = await withWriteBack.DeleteAsync($"/api/v1/source-connections/{write}");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, readDeleted.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task A_source_node_resave_over_a_read_write_connection_needs_no_ehr_write_back_right()
    {
        // Saved as older writers did: no AuthPlacement, no JWKS URL.
        var name = NewName("rw");
        var id = (await CreateAsAdminAsync(AthenaBody(name, "ReadWrite", departmentId: "150", activated: true))).GetProperty("id").GetGuid();
        using var designer = await ClientWithAsync("workflow.view", "workflow.create", "workflow.edit", "athenahealth.edit");
        // Shaped as the canvas assembler sends it: authPlacement 'post', a JWKS URL, Access=Read on an older node.
        var body = JsonSerializer.SerializeToNode(AthenaBody(name, "Read", activated: false))!.AsObject();
        body["Authentication"]!["AuthPlacement"] = "post";
        body["Authentication"]!["JwksUrl"] = "https://example.test/.well-known/jwks.json";

        var resp = await BuildAsync(body, id, designer);

        await ApiFixture.EnsureOkAsync(resp);
        var saved = await GetConnectionAsync(id);
        Assert.Equal("ReadWrite", saved.GetProperty("access").GetString());
        Assert.Equal("150", saved.GetProperty("departmentId").GetString());
        Assert.True(saved.GetProperty("vendorWriteApisActivated").GetBoolean());
    }

    [Fact]
    public async Task An_edit_needs_the_saved_vendors_right_not_just_the_one_the_request_names()
    {
        var name = NewName("epic");
        var id = (await CreateAsAdminAsync(EpicBody(name, null))).GetProperty("id").GetGuid();
        using var cernerOnly = await ClientWithAsync("cerner.edit");
        var body = JsonSerializer.SerializeToNode(EpicBody(name, null))!.AsObject();
        body["SourceSystemType"] = "Cerner";

        var resp = await cernerOnly.PutAsJsonAsync($"/api/v1/source-connections/{id}", body);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Dropping_write_access_clears_the_department_and_the_activation()
    {
        var name = NewName("rw");
        var id = (await CreateAsAdminAsync(AthenaBody(name, "ReadWrite", departmentId: "1", activated: true))).GetProperty("id").GetGuid();

        var resp = await f.AdminClient.PutAsJsonAsync($"/api/v1/source-connections/{id}", AthenaBody(name, "Read"));

        await ApiFixture.EnsureOkAsync(resp);
        var saved = await GetConnectionAsync(id);
        Assert.Equal("Read", saved.GetProperty("access").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("departmentId").ValueKind);
        Assert.False(saved.GetProperty("vendorWriteApisActivated").GetBoolean());
    }

    [Fact]
    public async Task A_read_only_connection_ignores_a_department_instead_of_rejecting_it()
    {
        var created = await CreateAsAdminAsync(AthenaBody(NewName("r"), null, departmentId: "not a valid id"));

        Assert.Equal("Read", created.GetProperty("access").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("departmentId").ValueKind);
    }

    [Fact]
    public async Task A_source_node_save_keeps_an_athena_write_connections_department()
    {
        var name = NewName("rw");
        var id = (await CreateAsAdminAsync(AthenaBody(name, "ReadWrite", departmentId: "150"))).GetProperty("id").GetGuid();

        var resaved = await BuildAsync(AthenaBody(name, "Read"), id);

        await ApiFixture.EnsureOkAsync(resaved);
        var saved = await GetConnectionAsync(id);
        Assert.Equal("ReadWrite", saved.GetProperty("access").GetString());
        Assert.Equal("150", saved.GetProperty("departmentId").GetString());
    }

    [Fact]
    public async Task A_workflow_whose_source_node_is_bound_to_a_write_only_connection_still_saves_and_leaves_it_untouched()
    {
        var name = NewName("w");
        var id = (await CreateAsAdminAsync(EpicBody(name, "Write", activated: true))).GetProperty("id").GetGuid();

        var resp = await BuildAsync(EpicBody(name, null), id, baseUrl: "https://elsewhere.example.test/fhir");

        await ApiFixture.EnsureOkAsync(resp);
        var saved = await GetConnectionAsync(id);
        Assert.Equal("Write", saved.GetProperty("access").GetString());
        Assert.Equal("https://example.test/fhir", saved.GetProperty("baseUrl").GetString());
        Assert.Contains(id.ToString(), await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Copying_a_workflow_never_clones_a_write_only_connection()
    {
        var name = NewName("w");
        var id = (await CreateAsAdminAsync(EpicBody(name, "Write"))).GetProperty("id").GetGuid();
        var built = await BuildAsync(EpicBody(name, null), id);
        await ApiFixture.EnsureOkAsync(built);
        var workflowId = JsonDocument.Parse(await built.Content.ReadAsStringAsync()).RootElement.GetProperty("workflowId").GetGuid();
        using var creator = await ClientWithAsync("workflow.view", "workflow.create", "epic.create");

        var copied = await creator.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/copy", new { Name = NewName("copy") });

        await ApiFixture.EnsureOkAsync(copied);
        var all = await f.AdminClient.GetAsync("/api/v1/source-connections");
        var clones = JsonDocument.Parse(await all.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
            .Where(item => item.GetProperty("name").GetString()!.StartsWith($"{name} (copy ", StringComparison.Ordinal));
        Assert.Empty(clones);
    }

    [Fact]
    public async Task Copying_a_workflow_clones_its_source_connection_read_only()
    {
        var name = NewName("rw");
        var originalId = (await CreateAsAdminAsync(AthenaBody(name, "ReadWrite", departmentId: "150", activated: true)))
            .GetProperty("id").GetGuid();
        var built = await BuildAsync(AthenaBody(name, null), originalId);
        await ApiFixture.EnsureOkAsync(built);
        var workflowId = JsonDocument.Parse(await built.Content.ReadAsStringAsync()).RootElement.GetProperty("workflowId").GetGuid();
        // No ehrwriteback.create: the copy has no write-back node and mints no write connection.
        using var creator = await ClientWithAsync("workflow.view", "workflow.create", "athenahealth.create");

        var copied = await creator.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/copy", new { Name = NewName("copy") });

        await ApiFixture.EnsureOkAsync(copied);
        var all = await f.AdminClient.GetAsync("/api/v1/source-connections");
        var clone = JsonDocument.Parse(await all.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString()!.StartsWith($"{name} (copy ", StringComparison.Ordinal));
        Assert.Equal("Read", clone.GetProperty("access").GetString());
        Assert.Equal(JsonValueKind.Null, clone.GetProperty("departmentId").ValueKind);
        Assert.False(clone.GetProperty("vendorWriteApisActivated").GetBoolean());
        Assert.Equal("ReadWrite", (await GetConnectionAsync(originalId)).GetProperty("access").GetString());
    }

    private Task<HttpResponseMessage> BuildAsync(object source, Guid? existingId, HttpClient? client = null, string? baseUrl = null)
    {
        if (baseUrl is not null)
        {
            var body = JsonSerializer.SerializeToNode(source)!.AsObject();
            body["BaseUrl"] = baseUrl;
            source = body;
        }

        return (client ?? f.AdminClient).PostAsJsonAsync("/api/v1/workflows/build", new
        {
            Name = NewName("source-only"),
            IsEnabled = false,
            Nodes = new[]
            {
                new
                {
                    Id = "n-src",
                    NodeType = "EpicSourceNode",
                    Category = "Source",
                    Rank = 0,
                    SubRank = 0,
                    DisplayName = "Epic",
                    ConfigurationJson = existingId is { } id
                        ? JsonSerializer.Serialize(new Dictionary<string, string> { ["sourceConnectionId"] = id.ToString() })
                        : "{}",
                    PositionX = 0,
                    PositionY = 0,
                    IsEnabled = true,
                },
            },
            Edges = Array.Empty<object>(),
            Sources = new[] { new { NodeId = "n-src", Source = source, ExistingId = existingId } },
        });
    }
}
