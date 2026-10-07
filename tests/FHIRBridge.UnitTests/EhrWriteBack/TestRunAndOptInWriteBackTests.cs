using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Tabular;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.GenericFhir;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Healow;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>
/// Phase 7 through the shared writer: a test run sends a vendor's exact writes to a Generic FHIR test server (contract
/// switch counted as on, patients by identifier only, ledger kept apart); Epic's extra APIs go only where the destination
/// enabled them, and those that name target records only from a CSV / SQL Table source; a Generic FHIR target takes the
/// record as plain FHIR.
/// </summary>
public sealed class TestRunAndOptInWriteBackTests
{
    private const string FhirSource = "https://source.example.com/fhir";
    private static readonly string TabularSource = TabularSourceSettings.SourceBaseUrlFor("questionnaire-answers")!;

    private const string Patient = """
        {"resourceType":"Patient","id":"p1","identifier":[{"system":"urn:oid:2.16.840.1.113883.19.5","value":"MRN-1"}],
         "name":[{"family":"Powell","given":["Desiree"]}],"gender":"female","birthDate":"2014-11-14"}
        """;

    private const string Allergy = """
        {"resourceType":"AllergyIntolerance","id":"a1",
         "code":{"coding":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"7980"}],"text":"Penicillin G"},
         "patient":{"reference":"Patient/p1"}}
        """;

    private const string LinesDrainsAirways = """
        {"resourceType":"Observation","id":"lda1","status":"final",
         "category":[{"coding":[{"system":"http://open.epic.com/FHIR/StructureDefinition/observation-category","code":"LDA"}]}],
         "code":{"coding":[{"system":"http://loinc.org","code":"8693-4"}],"text":"Peripheral IV"},
         "subject":{"reference":"Patient/p1"},"effectivePeriod":{"start":"2026-10-01T08:00:00Z"}}
        """;

    private const string Questionnaire = """
        {"resourceType":"QuestionnaireResponse","id":"q1","status":"completed",
         "subject":{"identifier":{"system":"urn:oid:1.2.840.114350.1.13.5325.1.7.2.728165","value":"106731"}},
         "questionnaire":"Questionnaire/eU7pqmsZY1Mzn5Q6N3sr5",
         "item":[{"linkId":"64407","answer":[{"valueInteger":7}]}]}
        """;

    private static MappedEhrWriteBackDestinationWriter Writer(InMemoryEhrWriteLedgerRepository? ledger = null) =>
        new(
            new EhrWriteProfileRegistry(
            [
                new EpicAllergyIntoleranceWriteProfile(), new EpicVitalSignWriteProfile(), new EpicPatientWriteProfile(),
                new EpicLinesDrainsAirwaysWriteProfile(), new EpicImagingCharacteristicsWriteProfile(),
                new EpicPatientEnteredQuestionnaireWriteProfile(),
                new HealowAllergyIntoleranceWriteProfile(), new HealowPatientWriteProfile(),
                new GenericFhirWriteProfile("AllergyIntolerance"), new GenericFhirWriteProfile("Patient"),
            ]),
            ledger ?? new InMemoryEhrWriteLedgerRepository(),
            Mock.Of<IEhrCloneModePolicy>(),
            NullLogger<MappedEhrWriteBackDestinationWriter>.Instance,
            new NoMpi());

    private static MappedDestinationRecord Record(string json)
    {
        var node = JsonNode.Parse(json)!;
        var type = node["resourceType"]!.GetValue<string>();
        return new MappedDestinationRecord(Guid.NewGuid(), type, type, node["id"]!.GetValue<string>(), new Dictionary<string, object?>(), json);
    }

    private static Task<DestinationWriteResult> RunAsync(
        MappedEhrWriteBackDestinationWriter writer, ScriptedChannel channel, string sourceBaseUrl, params string[] records) =>
        writer.WriteAsync(
            new DestinationConfiguration("Write-back", DestinationType.EhrWriteBack, new SecretReference("kv", "s"), null, "{}"),
            new MappingProfile("EHR", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient", []),
            records.Select(Record).ToList(),
            new PipelineWriteContext(false, "Workflow", DateTimeOffset.UtcNow, SourceBaseUrl: sourceBaseUrl, EhrWriteChannel: channel),
            CancellationToken.None);

    private static EhrWriteResourceSummary Summary(DestinationWriteResult result, string type) =>
        result.EhrWrite!.Resources.Single(r => r.ResourceType == type);

    // ---- Test runs ----

    [Fact]
    public async Task A_test_run_sends_a_contracted_vendors_writes_to_the_test_server_without_activation_or_mpi()
    {
        var channel = new ScriptedChannel(SourceSystemType.Healow, testAs: SourceSystemType.Healow, createPatient: true, types: ["Patient", "AllergyIntolerance"]);

        var result = await RunAsync(Writer(), channel, FhirSource, Patient, Allergy);

        channel.VendorWriteApisActivated.Should().BeFalse();
        channel.Creates.Should().Equal("Patient", "AllergyIntolerance");
        channel.MatchCalls.Should().Be(0, because: "a test server has no $match and the MPI is not asked");
        result.EhrWrite!.TestRun.Should().BeTrue();
        result.EhrWrite.TargetVendor.Should().Be("Healow");
        result.EhrWrite.ScopeStatus.Should().Be("test-server");
        Summary(result, "AllergyIntolerance").Written.Should().Be(1);
    }

    [Fact]
    public async Task A_patient_the_test_server_does_not_have_waits_unless_the_destination_creates_patients()
    {
        var channel = new ScriptedChannel(SourceSystemType.Epic, testAs: SourceSystemType.Epic, types: ["AllergyIntolerance"]);

        var result = await RunAsync(Writer(), channel, FhirSource, Patient, Allergy);

        channel.Creates.Should().BeEmpty();
        Summary(result, "AllergyIntolerance").Reasons.Should().ContainKey("patient-not-in-ehr");
    }

    [Fact]
    public async Task Tests_as_different_vendors_on_one_server_are_kept_apart_in_the_ledger()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var asEpic = new ScriptedChannel(SourceSystemType.Epic, testAs: SourceSystemType.Epic, types: ["AllergyIntolerance"]) { IdentifierHit = "t-1" };
        var asEcw = new ScriptedChannel(SourceSystemType.Healow, testAs: SourceSystemType.Healow, types: ["AllergyIntolerance"]) { IdentifierHit = "t-1" };

        await RunAsync(Writer(ledger), asEpic, FhirSource, Patient, Allergy);
        await RunAsync(Writer(ledger), asEcw, FhirSource, Patient, Allergy);
        var again = await RunAsync(Writer(ledger), asEpic, FhirSource, Patient, Allergy);

        asEpic.Creates.Should().Equal("AllergyIntolerance");
        asEcw.Creates.Should().Equal("AllergyIntolerance");
        Summary(again, "AllergyIntolerance").Reasons.Should().ContainKey("already-written");
    }

    [Fact]
    public async Task Outside_a_test_run_a_contracted_vendor_still_needs_activation()
    {
        var channel = new ScriptedChannel(SourceSystemType.Healow, testAs: null, types: ["AllergyIntolerance"]) { IdentifierHit = "t-1" };

        var result = await RunAsync(Writer(), channel, FhirSource, Patient, Allergy);

        channel.Creates.Should().BeEmpty();
        Summary(result, "AllergyIntolerance").Reasons.Should().ContainKey("vendor-activation-required");
        result.EhrWrite!.TestRun.Should().BeFalse();
    }

    // ---- Variants a destination enables ----

    [Fact]
    public async Task An_epic_line_drain_or_airway_is_skipped_until_the_destination_enables_it()
    {
        var off = new ScriptedChannel(SourceSystemType.Epic, types: ["Observation"]) { IdentifierHit = "e-1", Encounters = [OpenEncounter] };
        var on = new ScriptedChannel(SourceSystemType.Epic, types: ["Observation"], variants: [EhrWriteVariants.LinesDrainsAirways])
        {
            IdentifierHit = "e-1",
            Encounters = [OpenEncounter],
        };

        var skipped = await RunAsync(Writer(), off, FhirSource, Patient, LinesDrainsAirways);
        var sent = await RunAsync(Writer(), on, FhirSource, Patient, LinesDrainsAirways);

        off.Creates.Should().BeEmpty();
        Summary(skipped, "Observation").Reasons.Should().ContainKey("variant-not-enabled");
        on.Creates.Should().Equal("Observation");
        var body = on.Sent.Single().Body;
        body["category"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("LDA");
        body["encounter"]!["reference"]!.GetValue<string>().Should().Be("Encounter/enc-open");
        body["subject"]!["reference"]!.GetValue<string>().Should().Be("Patient/e-1");
    }

    [Fact]
    public async Task An_api_that_names_target_records_takes_no_records_from_another_ehr()
    {
        var channel = new ScriptedChannel(SourceSystemType.Epic, types: ["QuestionnaireResponse"], variants: [EhrWriteVariants.PatientEnteredQuestionnaire]);

        var result = await RunAsync(Writer(), channel, FhirSource, Questionnaire);

        channel.Creates.Should().BeEmpty();
        Summary(result, "QuestionnaireResponse").Reasons.Should().ContainKey("target-references-unmappable");
    }

    [Fact]
    public async Task A_questionnaire_from_a_tabular_source_is_sent_against_its_assignment_with_no_patient_lookup()
    {
        var channel = new ScriptedChannel(SourceSystemType.Epic, types: ["QuestionnaireResponse"], variants: [EhrWriteVariants.PatientEnteredQuestionnaire]);

        var result = await RunAsync(Writer(), channel, TabularSource, Questionnaire);

        channel.Creates.Should().Equal("QuestionnaireResponse");
        channel.IdentifierSearches.Should().Be(0);
        channel.MatchCalls.Should().Be(0);
        var body = channel.Sent.Single().Body;
        body["subject"]!["identifier"]!["value"]!.GetValue<string>().Should().Be("106731");
        body["item"]![0]!["answer"]![0]!["valueDecimal"]!.GetValue<decimal>().Should().Be(7);
        Summary(result, "QuestionnaireResponse").Written.Should().Be(1);
    }

    // ---- Generic FHIR target ----

    [Fact]
    public async Task A_generic_fhir_target_gets_the_record_as_fhir_bound_to_its_own_patient()
    {
        var allergy = """
            {"resourceType":"AllergyIntolerance","id":"a1","meta":{"versionId":"3"},
             "code":{"text":"Penicillin G"},"patient":{"reference":"Patient/p1"},
             "recorder":{"reference":"Practitioner/doc-9","display":"Dr Who"},"encounter":{"reference":"Encounter/e9"}}
            """;
        var channel = new ScriptedChannel(SourceSystemType.GenericFhir, types: ["AllergyIntolerance"]) { IdentifierHit = "h-7" };

        await RunAsync(Writer(), channel, FhirSource, Patient, allergy);

        var body = channel.Sent.Single().Body;
        body.ContainsKey("id").Should().BeFalse();
        body.ContainsKey("meta").Should().BeFalse();
        body.ContainsKey("encounter").Should().BeFalse();
        body["patient"]!["reference"]!.GetValue<string>().Should().Be("Patient/h-7");
        body["recorder"]!.ToJsonString().Should().Be("""{"display":"Dr Who"}""");
    }

    private const string OpenEncounter = """{"resourceType":"Encounter","id":"enc-open","status":"in-progress"}""";

    private sealed class NoMpi : IEhrTargetPatientMatcher
    {
        public int Calls { get; private set; }

        public Task<EhrPatientMatchOutcome?> MatchAsync(EhrTargetPatientMatchRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<EhrPatientMatchOutcome?>(null);
        }
    }

    private sealed class ScriptedChannel : IEhrWriteChannel
    {
        public ScriptedChannel(
            SourceSystemType vendor,
            SourceSystemType? testAs = null,
            bool createPatient = false,
            string[]? types = null,
            string[]? variants = null)
        {
            TargetVendor = vendor;
            Options = new EhrWriteBackRunOptions(
                DryRun: false, createPatient, 500, "preliminary", types ?? ["AllergyIntolerance"],
                TestAsVendor: testAs, EnabledVariants: variants);
        }

        public Guid TargetConnectionId { get; } = Guid.NewGuid();
        public SourceSystemType TargetVendor { get; }
        public string TargetBaseUrl => "http://localhost:8090/fhir";
        public Guid? DestinationId => null;
        public EhrWriteBackRunOptions Options { get; }
        public bool VendorWriteApisActivated => false;

        /// <summary>The id an identifier search finds, or null for none.</summary>
        public string? IdentifierHit { get; init; }
        public IReadOnlyList<string> Encounters { get; init; } = [];
        public List<string> Creates { get; } = [];
        public List<(string ResourceType, JsonObject Body)> Sent { get; } = [];
        public int MatchCalls { get; private set; }
        public int IdentifierSearches { get; private set; }

        public Task<string?> GetGrantedScopeAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<EhrSearchOutcome> SearchByIdentifierAsync(string resourceType, string system, string value, CancellationToken cancellationToken)
        {
            IdentifierSearches++;
            IReadOnlyList<string> hits = IdentifierHit is null
                ? []
                : [$$"""{"resourceType":"Patient","id":"{{IdentifierHit}}","identifier":[{"system":"{{system}}","value":"{{value}}"}]}"""];
            return Task.FromResult(new EhrSearchOutcome(true, 200, hits, []));
        }

        public Task<EhrSearchOutcome> SearchForPatientAsync(string resourceType, string targetPatientId, CancellationToken cancellationToken) =>
            Task.FromResult(new EhrSearchOutcome(true, 200, Encounters, []));

        public Task<EhrPatientMatchOutcome> MatchPatientAsync(string patientJson, CancellationToken cancellationToken)
        {
            MatchCalls++;
            return Task.FromResult(new EhrPatientMatchOutcome(EhrPatientMatchKind.Failed, null, 404, []));
        }

        public Task<EhrCreateOutcome> CreateAsync(string resourceType, string resourceJson, CancellationToken cancellationToken)
        {
            Creates.Add(resourceType);
            Sent.Add((resourceType, JsonNode.Parse(resourceJson)!.AsObject()));
            return Task.FromResult(new EhrCreateOutcome(EhrCreateKind.Created, 201, $"new-{Creates.Count}", []));
        }
    }
}
