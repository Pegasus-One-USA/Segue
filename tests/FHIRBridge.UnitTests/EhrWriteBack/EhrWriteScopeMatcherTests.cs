using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FluentAssertions;

namespace FHIRBridge.UnitTests.EhrWriteBack;

public sealed class EhrWriteScopeMatcherTests
{
    // The exact scope string the Epic sandbox granted the write app on 2026-09-30 (v1 spelling, for a SMART v2 app).
    private const string EpicSandboxGrant =
        "system/AllergyIntolerance.write system/Condition.write system/DocumentReference.write system/Encounter.read " +
        "system/Observation.write system/Patient.read system/Patient.write";

    [Theory]
    [InlineData("AllergyIntolerance", true)]
    [InlineData("Condition", true)]
    [InlineData("DocumentReference", true)]
    [InlineData("Observation", true)]
    [InlineData("Patient", true)]
    [InlineData("Encounter", false)]
    [InlineData("Immunization", false)]
    public void Epic_sandbox_grant(string resourceType, bool expected)
    {
        EhrWriteScopeMatcher.AllowsCreate(EpicSandboxGrant, resourceType).Should().Be(expected);
    }

    [Theory]
    [InlineData("system/Observation.c", true)]
    [InlineData("system/Observation.cruds", true)]
    [InlineData("system/Observation.cu", true)]
    [InlineData("system/Observation.rs", false)]
    [InlineData("system/Observation.read", false)]
    [InlineData("system/*.write", true)]
    [InlineData("system/*.c", true)]
    [InlineData("system/Observation.*", true)]
    [InlineData("system/observation.write", false)]
    [InlineData("system/Observation.c?category=vital-signs", true)]
    [InlineData("system/Observation.c?category=http://terminology.hl7.org/CodeSystem/observation-category|vital-signs", true)]
    [InlineData("launch/patient openid", false)]
    [InlineData("", false)]
    public void Scope_spellings(string granted, bool expected)
    {
        EhrWriteScopeMatcher.AllowsCreate(granted, "Observation").Should().Be(expected);
    }
}
