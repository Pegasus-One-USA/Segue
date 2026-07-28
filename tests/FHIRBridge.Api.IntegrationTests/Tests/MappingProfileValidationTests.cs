using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// End-to-end check that <c>CreateMappingProfileRequestValidator</c> (the server-side port of the destination
/// wizard's "upsert mode requires an upsert-key field" check — previously enforced only client-side in
/// <c>workflow-build-assembler.service.ts</c>'s <c>buildMappingSpec()</c>) actually runs when
/// <c>POST /api/v1/mapping-profiles</c> is hit directly, bypassing the portal entirely.
/// </summary>
[Collection("ApiTests")]
public sealed class MappingProfileValidationTests(ApiFixture f)
{
    private static object Body(string destinationObject, bool isUpsertKey) => new
    {
        Name = $"Patient-{Guid.NewGuid():N}",
        ResourceType = "Patient",
        SourceConnectionId = Guid.NewGuid(),
        DestinationId = Guid.NewGuid(),
        DestinationObject = destinationObject,
        Fields = new[]
        {
            new
            {
                TargetField = "PatientId",
                JsonPath = "$.id",
                ValueType = "String",
                IsRequired = true,
                IsUpsertKey = isUpsertKey,
            },
        },
    };

    [Fact]
    public async Task Upsert_destination_without_an_upsert_key_field_returns_400_with_field_errors()
    {
        var resp = await f.AdminClient.PostAsJsonAsync(
            "/api/v1/mapping-profiles", Body("dbo.Patient;mode=upsert", isUpsertKey: false));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.TryGetProperty("fieldErrors", out var fieldErrors));
        Assert.NotEqual(JsonValueKind.Null, fieldErrors.ValueKind);

        var messages = fieldErrors.EnumerateObject()
            .SelectMany(p => p.Value.EnumerateArray().Select(m => m.GetString()));
        Assert.Contains(messages, m => m != null && m.Contains("upsert key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Empty_mapping_name_returns_400_with_field_errors()
    {
        var body = Body("dbo.Patient", isUpsertKey: true);
        var withEmptyName = JsonSerializer.SerializeToNode(body)!.AsObject();
        withEmptyName["Name"] = "";

        var resp = await f.AdminClient.PostAsJsonAsync("/api/v1/mapping-profiles", withEmptyName);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.TryGetProperty("fieldErrors", out var fieldErrors));
        Assert.True(fieldErrors.TryGetProperty("Name", out _));
    }
}
