using FHIRBridge.Domain.Fhir;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Sources;

/// <summary>
/// A type on this list is searched with the patient's id (`patient=`); one missing from it is searched unscoped, which
/// Epic refuses for payer and goal data.
/// </summary>
public sealed class PatientCompartmentResourceTypesTests
{
    [Theory]
    [InlineData("Coverage")]
    [InlineData("ExplanationOfBenefit")]
    [InlineData("Goal")]
    [InlineData("Observation")]
    public void A_patient_compartment_type_is_searched_for_the_patient(string resourceType)
    {
        PatientCompartmentResourceTypes.IsSupported(resourceType).Should().BeTrue();
    }

    [Theory]
    [InlineData("Practitioner")]
    [InlineData("Organization")]
    [InlineData("Medication")]
    public void A_type_outside_the_compartment_is_not(string resourceType)
    {
        PatientCompartmentResourceTypes.IsSupported(resourceType).Should().BeFalse();
    }
}
