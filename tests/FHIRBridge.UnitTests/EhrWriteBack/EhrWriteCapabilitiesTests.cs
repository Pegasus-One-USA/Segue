using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>
/// The write-capability table is deny-by-default and holds exactly what Phase 0 verified for Epic: five create-only
/// APIs, Backend tokens only, plus eClinicalWorks' contracted APIs, dry-run-only until a practice sandbox verifies
/// them. A vendor added by accident, or an Update slipping in, would let the portal offer writes the EHR refuses
/// (or, worse, accepts in a way nobody verified).
/// </summary>
public sealed class EhrWriteCapabilitiesTests
{
    [Fact]
    public void Only_epic_eclinicalworks_and_athenahealth_accept_writes()
    {
        foreach (var vendor in Enum.GetValues<SourceSystemType>())
        {
            EhrWriteCapabilities.HasAnyWriteCapability(vendor)
                .Should().Be(vendor is SourceSystemType.Epic or SourceSystemType.Healow or SourceSystemType.Athenahealth, because: vendor.ToString());
        }
    }

    [Fact]
    public void Athenahealth_writes_clinical_types_through_athenaone_once_activated()
    {
        var athena = EhrWriteCapabilities.VendorProfile(SourceSystemType.Athenahealth)!;

        athena.Capabilities.Select(c => (c.ResourceType, c.Variant)).Should().BeEquivalentTo(new (string, string?)[]
        {
            ("AllergyIntolerance", null),
            ("Condition", EhrWriteVariants.ProblemListItem),
            ("DocumentReference", EhrWriteVariants.ClinicalNote),
            ("Immunization", EhrWriteVariants.HistoricalImmunization),
            ("MedicationRequest", EhrWriteVariants.MedicationList),
            ("MedicationStatement", EhrWriteVariants.MedicationList),
            ("Observation", EhrWriteVariants.VitalSigns),
            ("Observation", EhrWriteVariants.LaboratoryResult),
            ("Patient", null),
            ("QuestionnaireResponse", null),
        });
        athena.Capabilities.Where(c => c.ResourceType != "QuestionnaireResponse")
            .Should().OnlyContain(c => c.LiveWriteSupported && c.RequiresVendorActivation && c.VendorApiId.StartsWith("athenaone-"));
        EhrWriteCapabilities.Find(SourceSystemType.Athenahealth, "QuestionnaireResponse")!.LiveWriteSupported.Should().BeFalse(
            because: "the certified FHIR create is not verified yet");
        athena.SupportsPatientMatch.Should().BeFalse();
        athena.ProprietaryApiScope.Should().Be("athena/service/Athenanet.MDP.*");
        athena.IsAlreadyAtTarget("athena-409", null).Should().BeTrue();
        EhrWriteCapabilities.FindAll(SourceSystemType.Athenahealth, "Observation").Should().HaveCount(2);
        EhrWriteCapabilities.Find(SourceSystemType.Athenahealth, "Observation")!.Variant.Should().Be(EhrWriteVariants.VitalSigns);
    }

    [Fact]
    public void Eclinicalworks_lists_every_published_create_api_live_only_once_contracted()
    {
        var healow = EhrWriteCapabilities.VendorProfile(SourceSystemType.Healow)!;

        healow.Capabilities.Select(c => (c.ResourceType, c.Variant)).Should().BeEquivalentTo(new (string, string?)[]
        {
            ("AllergyIntolerance", null),
            ("Condition", EhrWriteVariants.ProblemListItem),
            ("Condition", EhrWriteVariants.EncounterDiagnosis),
            ("Condition", EhrWriteVariants.MedicalHistory),
            ("DocumentReference", EhrWriteVariants.ClinicalNote),
            ("Immunization", EhrWriteVariants.HistoricalImmunization),
            ("MedicationRequest", EhrWriteVariants.MedicationList),
            ("MedicationStatement", EhrWriteVariants.MedicationList),
            ("Observation", EhrWriteVariants.VitalSigns),
            ("Patient", null),
            ("Procedure", EhrWriteVariants.SurgicalHistory),
            ("QuestionnaireResponse", null),
        });
        healow.Capabilities.Where(c => c.ResourceType != "QuestionnaireResponse")
            .Should().OnlyContain(c => c.LiveWriteSupported && c.RequiresVendorActivation, because: "eCW's clinical Create APIs are contracted");
        healow.Capabilities.Where(c => c.CreatesHolderEncounter).Select(c => c.Variant)
            .Should().BeEquivalentTo([EhrWriteVariants.MedicalHistory, EhrWriteVariants.SurgicalHistory]);
        healow.Capabilities.Where(c => c.CreatesHolderEncounter).Should().OnlyContain(c => c.OptInOnly);
        EhrWriteCapabilities.Find(SourceSystemType.Healow, "Observation")!.RequiresEncounter.Should().BeFalse(
            because: "eCW files vitals by effectiveDateTime, with no encounter reference");
        healow.Capabilities.Should().OnlyContain(c => c.Operations.SetEquals(new[] { EhrWriteOperation.Create }));
        healow.SupportsPatientMatch.Should().BeFalse();
        healow.RequestsScopeOnTokenRequest.Should().BeTrue();
        healow.IsAlreadyAtTarget("202", null).Should().BeTrue();
        healow.ProprietaryApiScope.Should().BeNull(because: "eCW's contracted APIs are FHIR, under FHIR scopes");
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, false)]
    public void A_type_is_live_only_when_supported_and_activated_where_activation_is_needed(
        bool liveWriteSupported, bool requiresActivation, bool activated, bool expected)
    {
        var capability = new EhrWriteCapability(
            "AllergyIntolerance", new HashSet<EhrWriteOperation> { EhrWriteOperation.Create }, "x", null, false, false,
            new HashSet<ApplicationType> { ApplicationType.Backend }, liveWriteSupported, requiresActivation);

        capability.IsLive(activated).Should().Be(expected);
    }

    [Fact]
    public void Epic_lists_the_five_verified_apis()
    {
        EhrWriteCapabilities.For(SourceSystemType.Epic)
            .Select(c => (c.ResourceType, c.VendorApiId))
            .Should().BeEquivalentTo(new[]
            {
                ("AllergyIntolerance", "945"),
                ("Condition", "949"),
                ("DocumentReference", "1046"),
                ("Observation", "963"),
                ("Patient", "930"),
            });
    }

    [Fact]
    public void Every_capability_is_create_only_backend_only_and_a_canonical_resource_type()
    {
        foreach (var capability in new[] { SourceSystemType.Epic, SourceSystemType.Healow, SourceSystemType.Athenahealth }.SelectMany(EhrWriteCapabilities.For))
        {
            capability.Operations.Should().BeEquivalentTo([EhrWriteOperation.Create], because: capability.ResourceType);
            capability.AllowedApplicationTypes.Should().BeEquivalentTo([ApplicationType.Backend], because: capability.ResourceType);
            SupportedFhirResourceTypes.Normalize(capability.ResourceType).Should().Be(capability.ResourceType);
        }
    }

    [Fact]
    public void Encounter_and_opt_in_rules_match_the_epic_apis()
    {
        EhrWriteCapabilities.Find(SourceSystemType.Epic, "DocumentReference")!.RequiresEncounter.Should().BeTrue();
        EhrWriteCapabilities.Find(SourceSystemType.Epic, "Observation")!.RequiresEncounter.Should().BeTrue();
        EhrWriteCapabilities.Find(SourceSystemType.Epic, "AllergyIntolerance")!.RequiresEncounter.Should().BeFalse();
        EhrWriteCapabilities.Find(SourceSystemType.Epic, "Patient")!.OptInOnly.Should().BeTrue();
        EhrWriteCapabilities.Find(SourceSystemType.Epic, "Condition")!.OptInOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bogus")]
    [InlineData("1")]
    [InlineData("99")]
    [InlineData("Epic,Healow")]
    [InlineData("Cerner")]
    public void Anything_but_a_write_capable_vendor_name_gets_nothing(string? vendor)
    {
        EhrWriteCapabilities.For(vendor).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Epic")]
    [InlineData("epic")]
    [InlineData(" EPIC ")]
    public void Vendor_names_parse_case_insensitively(string vendor)
    {
        EhrWriteCapabilities.For(vendor).Should().HaveCount(5);
    }

    [Fact]
    public void Writes_epic_does_not_support_are_refused()
    {
        EhrWriteCapabilities.Supports(SourceSystemType.Epic, "Condition", EhrWriteOperation.Create).Should().BeTrue();
        EhrWriteCapabilities.Supports(SourceSystemType.Epic, "Condition", EhrWriteOperation.Update).Should().BeFalse();
        EhrWriteCapabilities.Supports(SourceSystemType.Epic, "Goal", EhrWriteOperation.Create).Should().BeFalse();
        EhrWriteCapabilities.Supports(SourceSystemType.Epic, null, EhrWriteOperation.Create).Should().BeFalse();
    }

    [Theory]
    [InlineData("59141", null, true)]
    [InlineData("59189", "code/instant", true)]
    [InlineData("59189", null, false)]
    [InlineData("59189", "encounter", false)]
    [InlineData("59012", "verificationstatus", false)]
    [InlineData(null, null, false)]
    public void Already_at_target_codes_match_only_their_documented_meaning(string? code, string? expression, bool expected)
    {
        EhrWriteCapabilities.VendorProfile(SourceSystemType.Epic)!.IsAlreadyAtTarget(code, expression).Should().Be(expected);
    }

    [Fact]
    public void Epic_ignores_requested_scope_and_supports_patient_match()
    {
        var epic = EhrWriteCapabilities.VendorProfile(SourceSystemType.Epic)!;
        epic.RequestsScopeOnTokenRequest.Should().BeFalse();
        epic.SupportsPatientMatch.Should().BeTrue();
    }
}
