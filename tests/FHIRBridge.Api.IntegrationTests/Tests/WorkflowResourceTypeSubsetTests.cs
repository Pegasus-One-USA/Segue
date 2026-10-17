using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// The source node declares the resource types it reads; a destination only chooses from them. Saving a workflow
/// (build or PUT) whose destination writes a type its source does not read is refused, naming the destination and
/// the types; a subset is accepted, and a legacy source with no declared list is not checked.
/// </summary>
[Collection("ApiTests")]
public sealed class WorkflowResourceTypeSubsetTests(ApiFixture f)
{
    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static object Node(string id, string nodeType, string category, string displayName, Dictionary<string, string> fields) => new
    {
        Id = id,
        NodeType = nodeType,
        Category = category,
        Rank = category == "Source" ? 0 : 30,
        SubRank = 0,
        DisplayName = displayName,
        ConfigurationJson = JsonSerializer.Serialize(fields),
        PositionX = 0,
        PositionY = 0,
        IsEnabled = true,
    };

    private static object[] Nodes(string? sourceResources, string destResources)
    {
        var sourceFields = sourceResources is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["Resource types declared"] = "true", ["Resources"] = sourceResources };
        return
        [
            Node("n-src", "GenericFhirSourceNode", "Source", "Hospital FHIR", sourceFields),
            Node("n-dst", "CsvDestinationNode", "Destination", "Nightly CSV",
                new Dictionary<string, string> { ["dest_resources"] = destResources }),
        ];
    }

    private static object[] Edges => [new { FromNodeId = "n-src", ToNodeId = "n-dst" }];

    private Task<HttpResponseMessage> BuildAsync(string? sourceResources, string destResources) =>
        f.AdminClient.PostAsJsonAsync("/api/v1/workflows/build", new
        {
            Name = NewName("subset"),
            IsEnabled = false,
            Nodes = Nodes(sourceResources, destResources),
            Edges,
        });

    private static object Definition(string name, string? sourceResources, string destResources) => new
    {
        Name = name,
        IsEnabled = false,
        Nodes = Nodes(sourceResources, destResources),
        Edges,
    };

    private static async Task<string> MessageOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("message").GetString()!;
    }

    [Fact]
    public async Task Build_rejects_a_destination_type_the_source_does_not_read()
    {
        var response = await BuildAsync("Patient,Observation", "Patient,Procedure");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = await MessageOfAsync(response);
        Assert.Contains("Destination 'Nightly CSV' writes Procedure", message);
        Assert.Contains("its source 'Hospital FHIR' does not read", message);
    }

    [Fact]
    public async Task Build_accepts_a_destination_that_writes_a_subset_of_the_source()
    {
        var response = await BuildAsync("Patient,Observation", "Observation");

        await ApiFixture.EnsureOkAsync(response);
    }

    [Fact]
    public async Task Build_does_not_check_a_legacy_source_without_a_declared_list()
    {
        var response = await BuildAsync(sourceResources: null, "Patient,Procedure");

        await ApiFixture.EnsureOkAsync(response);
    }

    [Fact]
    public async Task Create_rejects_a_destination_type_the_source_does_not_read()
    {
        var response = await f.AdminClient.PostAsJsonAsync(
            "/api/v1/workflows", Definition(NewName("subset-create"), "Patient", "Patient,Condition"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("writes Condition", await MessageOfAsync(response));
    }

    [Fact]
    public async Task Put_rejects_a_destination_type_the_source_does_not_read_and_accepts_a_subset()
    {
        var name = NewName("subset-put");
        var created = await f.AdminClient.PostAsJsonAsync("/api/v1/workflows", Definition(name, "Patient", "Patient"));
        await ApiFixture.EnsureOkAsync(created);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var refused = await f.AdminClient.PutAsJsonAsync(
            $"/api/v1/workflows/{id}", Definition(name, "Patient", "Patient,Condition"));
        var accepted = await f.AdminClient.PutAsJsonAsync(
            $"/api/v1/workflows/{id}", Definition(name, "Patient,Condition", "Patient,Condition"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("writes Condition", await MessageOfAsync(refused));
        await ApiFixture.EnsureOkAsync(accepted);
    }

    [Fact]
    public async Task Put_re_saves_a_legacy_ehr_node_whose_hidden_retrieval_list_is_narrower_than_its_destination()
    {
        // Saved before sources declared their types: the EHR form wrote only its hidden (here stale, cloned)
        // "Retrieval resource type" and no "Resources"; the destination later added Observation. That list is not
        // the source's declared list, so re-saving the unchanged workflow is accepted.
        var name = NewName("subset-legacy");
        var definition = new
        {
            Name = name,
            IsEnabled = false,
            Nodes = new[]
            {
                Node("n-src", "AthenahealthSourceNode", "Source", "athena",
                    new Dictionary<string, string> { ["Retrieval resource type"] = "Patient,Encounter" }),
                Node("n-dst", "CsvDestinationNode", "Destination", "Nightly CSV",
                    new Dictionary<string, string> { ["dest_resources"] = "Patient,Encounter,Observation" }),
            },
            Edges,
        };
        var created = await f.AdminClient.PostAsJsonAsync("/api/v1/workflows", definition);
        await ApiFixture.EnsureOkAsync(created);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var resaved = await f.AdminClient.PutAsJsonAsync($"/api/v1/workflows/{id}", definition);

        await ApiFixture.EnsureOkAsync(resaved);
    }

    [Fact]
    public async Task Put_re_saves_a_legacy_generic_fhir_node_whose_silent_12_type_list_lacks_its_destinations_type()
    {
        // Saved before sources declared their types: the Generic FHIR form filled "Resources" with its 12 defaults
        // and wrote no "Resource types declared" marker. The destination writes Organization, which is not among
        // them; the list was never the admin's choice, so building and re-saving the unchanged workflow succeed.
        const string silentDefault =
            "Patient,Practitioner,Encounter,AllergyIntolerance,Observation,Condition,Procedure,ServiceRequest," +
            "DiagnosticReport,MedicationRequest,MedicationAdministration,Provenance";
        var name = NewName("subset-legacy-fhir");
        var definition = new
        {
            Name = name,
            IsEnabled = false,
            Nodes = new[]
            {
                Node("n-src", "GenericFhirSourceNode", "Source", "Hospital FHIR",
                    new Dictionary<string, string>
                    {
                        ["Connector"] = "Generic FHIR R4",
                        ["Resources"] = silentDefault,
                        ["Retrieval resource type"] = silentDefault,
                    }),
                Node("n-dst", "CsvDestinationNode", "Destination", "Nightly CSV",
                    new Dictionary<string, string> { ["dest_resources"] = "Patient,Organization" }),
            },
            Edges,
        };
        var created = await f.AdminClient.PostAsJsonAsync("/api/v1/workflows", definition);
        await ApiFixture.EnsureOkAsync(created);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var resaved = await f.AdminClient.PutAsJsonAsync($"/api/v1/workflows/{id}", definition);
        var built = await f.AdminClient.PostAsJsonAsync("/api/v1/workflows/build", new
        {
            Name = NewName("subset-legacy-fhir-build"),
            definition.IsEnabled,
            definition.Nodes,
            definition.Edges,
        });

        await ApiFixture.EnsureOkAsync(resaved);
        await ApiFixture.EnsureOkAsync(built);
    }
}
