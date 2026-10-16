using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

public sealed class EhrEndpointSourceOverrideTests
{
    private static FhirSourceConfiguration Source(
        RuntimeSourceType type = RuntimeSourceType.Epic,
        ApplicationType? applicationType = ApplicationType.Backend,
        string? retrievalMethod = null) =>
        new(
            type, "Saved source", "https://saved.example.org/fhir", "https://saved.example.org/token", "saved-client",
            "saved-key", null, ["system/Patient.read"],
            ApplicationType: applicationType, RetrievalMethod: retrievalMethod, PracticeId: "111");

    private static EhrEndpointOverride Endpoint(
        string vendor = "Epic", string? tokenEndpoint = null, string? clientId = null, string? keyId = null, string? practiceId = null) =>
        new(Guid.NewGuid(), "Acme Hospital", vendor, "https://acme.example.org/fhir", tokenEndpoint, clientId, keyId, null, practiceId);

    [Fact]
    public void Apply_ReplacesProvidedValues_AndKeepsTheRest()
    {
        var endpoint = Endpoint(tokenEndpoint: "https://acme.example.org/token", clientId: "acme-client");

        var result = EhrEndpointSourceOverride.Apply(Source(), endpoint);

        result.BaseUrl.Should().Be("https://acme.example.org/fhir");
        result.TokenEndpoint.Should().Be("https://acme.example.org/token");
        result.ClientId.Should().Be("acme-client");
        result.KeyId.Should().Be("saved-key");
        result.PracticeId.Should().Be("111");
        result.EhrEndpointId.Should().Be(endpoint.Id);
    }

    [Fact]
    public void Apply_DoesNotModifyTheOriginalSource()
    {
        var original = Source();

        EhrEndpointSourceOverride.Apply(original, Endpoint(clientId: "acme-client"));

        original.BaseUrl.Should().Be("https://saved.example.org/fhir");
        original.ClientId.Should().Be("saved-client");
        original.EhrEndpointId.Should().BeNull();
    }

    [Fact]
    public void Apply_UsesEndpointPracticeId_ForAthena()
    {
        var result = EhrEndpointSourceOverride.Apply(
            Source(RuntimeSourceType.Athenahealth), Endpoint("Athenahealth", practiceId: "195900"));

        result.PracticeId.Should().Be("195900");
    }

    [Fact]
    public void Apply_RejectsAnEndpointOfAnotherVendor()
    {
        var act = () => EhrEndpointSourceOverride.Apply(Source(RuntimeSourceType.Epic), Endpoint("Cerner"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Cerner*Epic*");
    }

    [Fact]
    public void Apply_RejectsBulkExport()
    {
        var act = () => EhrEndpointSourceOverride.Apply(Source(retrievalMethod: "bulk-export"), Endpoint());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Bulk export*");
    }

    [Fact]
    public void Apply_LeavesInteractiveSourcesUntouched()
    {
        var source = Source(applicationType: ApplicationType.Standalone);

        var result = EhrEndpointSourceOverride.Apply(source, Endpoint(clientId: "acme-client"));

        result.Should().BeSameAs(source);
    }
}
