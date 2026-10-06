using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Workflows;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

public sealed class WorkflowDebugConfigTests
{
    [Fact]
    public void DescribeSource_ShowsSettings_ButNeverSecretsOrSearchValues()
    {
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Prod", "https://fhir.example.org/api/FHIR/R4/?token=abc",
            "https://auth.example.org/token", "client-id-123", "key-1", "-----BEGIN PRIVATE KEY-----SECRET", ["system/Patient.read"],
            SearchParameters: "family=Smith&birthdate=1990-01-01", ClientSecret: "s3cret",
            PatientIds: ["p1", "p2"], TargetPatientId: "p9", PatientSearchCriteria: "identifier=MRN12345");

        var text = WorkflowDebugConfig.DescribeSource(source);

        text.Should().Contain("host=fhir.example.org").And.Contain("search parameter names=[family, birthdate]")
            .And.Contain("cohort restricted to 2 patient(s)").And.Contain("private key yes").And.Contain("client secret yes");
        text.Should().NotContain("SECRET").And.NotContain("s3cret").And.NotContain("client-id-123")
            .And.NotContain("Smith").And.NotContain("1990-01-01").And.NotContain("MRN12345").And.NotContain("token=abc")
            .And.NotContain("/api/FHIR");
    }

    [Fact]
    public void DescribeMapping_ListsFieldsWithoutDefaultValues()
    {
        var fields = new[]
        {
            new MappingFieldDto("FamilyName", "$.name[0].family", MappingValueType.String, true, "SECRET-DEFAULT", null),
        };

        var text = WorkflowDebugConfig.DescribeMapping("Patient", "dbo.Patient", fields);

        text.Should().Contain("$.name[0].family -> FamilyName").And.Contain("required").And.Contain("default set");
        text.Should().NotContain("SECRET-DEFAULT");
    }

    [Theory]
    [InlineData("https://fhir.example.org/base/path?x=1", "fhir.example.org")]
    [InlineData("a@b.com,c@d.com", "(withheld)")]
    [InlineData("dbo.Patient;mode=upsert", "dbo.Patient")]
    [InlineData(null, "(none)")]
    public void SafeTarget_ReducesToSomethingHarmless(string? input, string expected) =>
        WorkflowDebugConfig.SafeTarget(input).Should().Be(expected);
}
