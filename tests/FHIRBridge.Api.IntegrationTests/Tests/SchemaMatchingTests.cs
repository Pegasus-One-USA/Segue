using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// Destination-driven schema matching endpoints: suggest (no persistence), approve (persists + overrides
/// future scoring), and get-approved. Uses the worked Patient example from the schema matching design —
/// dob/mrn/sex/firstName/lastName/mobile source fields against Identifier/GivenName/FamilyName/BirthDate/
/// Gender/Phone destination columns.
/// </summary>
[Collection("ApiTests")]
public sealed class SchemaMatchingTests(ApiFixture f)
{
    private static object PatientDestinationFields => new object[]
    {
        new { Name = "Identifier", DataType = "String", IsRequired = true },
        new { Name = "GivenName", DataType = "String" },
        new { Name = "FamilyName", DataType = "String" },
        new { Name = "BirthDate", DataType = "Date" },
        new { Name = "Gender", DataType = "String" },
        new { Name = "Phone", DataType = "String" },
    };

    private static object PatientSourceJson => new
    {
        firstName = "John",
        lastName = "Doe",
        dob = "1985-04-12",
        sex = "M",
        mrn = "E12345",
        mobile = "+1-555-123-4567",
    };

    [Fact]
    public async Task Suggest_matches_every_destination_field_for_the_worked_patient_example()
    {
        var sourceSystem = $"Epic-{Guid.NewGuid():N}";

        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/schema-mappings/suggest", new
        {
            SourceSystem = sourceSystem,
            ResourceType = "Patient",
            DestinationTableName = "Patient",
            SourceJson = PatientSourceJson,
            DestinationFields = PatientDestinationFields,
        });
        resp.EnsureSuccessStatusCode();

        var suggestions = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(6, suggestions.GetArrayLength());

        var bySourceField = suggestions.EnumerateArray()
            .ToDictionary(s => s.GetProperty("destinationField").GetString()!, s => s.GetProperty("sourceField").GetString());

        Assert.Equal("$.dob", bySourceField["BirthDate"]);
        Assert.Equal("$.mrn", bySourceField["Identifier"]);
        Assert.Equal("$.sex", bySourceField["Gender"]);
        Assert.Equal("$.firstName", bySourceField["GivenName"]);
        Assert.Equal("$.lastName", bySourceField["FamilyName"]);
        Assert.Equal("$.mobile", bySourceField["Phone"]);
    }

    [Fact]
    public async Task Approve_then_get_approved_returns_the_persisted_mapping()
    {
        var sourceSystem = $"Epic-{Guid.NewGuid():N}";

        var approveResp = await f.AdminClient.PostAsJsonAsync("/api/v1/schema-mappings/approve", new
        {
            SourceSystem = sourceSystem,
            ResourceType = "Patient",
            DestinationTableName = "Patient",
            Mappings = new[]
            {
                new { DestinationField = "Identifier", SourceField = "$.mrn", Confidence = 0.83, Approved = true },
            },
        });
        approveResp.EnsureSuccessStatusCode();

        var getResp = await f.AdminClient.GetAsync(
            $"/api/v1/schema-mappings?sourceSystem={sourceSystem}&resourceType=Patient&destinationTableName=Patient");
        getResp.EnsureSuccessStatusCode();

        var approved = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, approved.GetArrayLength());
        Assert.Equal("Identifier", approved[0].GetProperty("destinationField").GetString());
        Assert.Equal("$.mrn", approved[0].GetProperty("sourceField").GetString());
        Assert.Equal("Approved", approved[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Suggest_reuses_an_approved_mapping_at_full_confidence_instead_of_rescoring()
    {
        var sourceSystem = $"Epic-{Guid.NewGuid():N}";

        // Approve a deliberately low-confidence-looking match first.
        var approveResp = await f.AdminClient.PostAsJsonAsync("/api/v1/schema-mappings/approve", new
        {
            SourceSystem = sourceSystem,
            ResourceType = "Patient",
            DestinationTableName = "Patient",
            Mappings = new[]
            {
                new { DestinationField = "Identifier", SourceField = "$.mrn", Confidence = 0.83, Approved = true },
            },
        });
        approveResp.EnsureSuccessStatusCode();

        var suggestResp = await f.AdminClient.PostAsJsonAsync("/api/v1/schema-mappings/suggest", new
        {
            SourceSystem = sourceSystem,
            ResourceType = "Patient",
            DestinationTableName = "Patient",
            SourceJson = PatientSourceJson,
            DestinationFields = new object[] { new { Name = "Identifier", DataType = "String" } },
        });
        suggestResp.EnsureSuccessStatusCode();

        var suggestions = JsonDocument.Parse(await suggestResp.Content.ReadAsStringAsync()).RootElement;
        var identifier = suggestions[0];
        Assert.Equal("$.mrn", identifier.GetProperty("sourceField").GetString());
        Assert.Equal(1.0, identifier.GetProperty("confidence").GetDouble());
        Assert.Equal("Approved mapping override", identifier.GetProperty("reason").GetString());
    }
}
