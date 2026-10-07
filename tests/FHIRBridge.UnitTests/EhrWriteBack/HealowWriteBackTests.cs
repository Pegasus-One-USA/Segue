using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Healow;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>
/// eClinicalWorks (Healow) write-back through the shared writer: patients resolve by identifier, then through the MPI
/// (eCW has no $match); the contracted Create APIs send only once the connection says they are activated; Conditions
/// go to the right one of eCW's three Condition APIs; medical history goes on one telephone encounter per patient,
/// created only when the destination opts in and a record is really sent.
/// </summary>
public sealed class HealowWriteBackTests
{
    private const string IdentifierHit =
        """{"resourceType":"Patient","id":"eCW-42","identifier":[{"system":"urn:oid:2.16.840.1.113883.19.5","value":"MRN-1"}]}""";

    private const string Patient = """
        {"resourceType":"Patient","id":"p1","identifier":[{"system":"urn:oid:2.16.840.1.113883.19.5","value":"MRN-1"}],
         "name":[{"family":"Powell","given":["Desiree"]}],"gender":"female","birthDate":"2014-11-14"}
        """;

    private const string Allergy = """
        {"resourceType":"AllergyIntolerance","id":"a1",
         "code":{"coding":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"7980"}],"text":"Penicillin G"},
         "patient":{"reference":"Patient/p1"}}
        """;

    private static string Condition(string id, string category, string? clinical = "active") => $$$"""
        {"resourceType":"Condition","id":"{{{id}}}","subject":{"reference":"Patient/p1"},
         "clinicalStatus":{"coding":[{"code":"{{{clinical}}}"}]},
         "category":[{"coding":[{"code":"{{{category}}}"}]}],
         "code":{"coding":[{"system":"http://hl7.org/fhir/sid/icd-10-cm","code":"E11.9","display":"Type 2 diabetes"}],"text":"Type 2 diabetes"}}
        """;

    private static MappedEhrWriteBackDestinationWriter Writer(IEhrTargetPatientMatcher? matcher = null) =>
        new(
            new EhrWriteProfileRegistry(
            [
                new HealowAllergyIntoleranceWriteProfile(), new HealowConditionWriteProfile(),
                new HealowEncounterDiagnosisWriteProfile(), new HealowMedicalHistoryWriteProfile(),
                new HealowVitalSignWriteProfile(), new HealowPatientWriteProfile(),
            ]),
            new InMemoryEhrWriteLedgerRepository(),
            Mock.Of<IEhrCloneModePolicy>(),
            NullLogger<MappedEhrWriteBackDestinationWriter>.Instance,
            matcher);

    private static MappedDestinationRecord Record(string json)
    {
        var node = JsonNode.Parse(json)!;
        var type = node["resourceType"]!.GetValue<string>();
        return new MappedDestinationRecord(Guid.NewGuid(), type, type, node["id"]!.GetValue<string>(), new Dictionary<string, object?>(), json);
    }

    private static Task<DestinationWriteResult> RunAsync(MappedEhrWriteBackDestinationWriter writer, HealowChannel channel, params string[] records) =>
        writer.WriteAsync(
            new DestinationConfiguration("eCW", DestinationType.EhrWriteBack, new SecretReference("kv", "s"), null, "{}"),
            new MappingProfile("EHR", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient", []),
            (records.Length == 0 ? [Patient, Allergy] : records).Select(Record).ToList(),
            new PipelineWriteContext(false, "Workflow", DateTimeOffset.UtcNow, SourceBaseUrl: "https://source.example.com/fhir", EhrWriteChannel: channel),
            CancellationToken.None);

    private static EhrWriteResourceSummary Summary(DestinationWriteResult result, string type) =>
        result.EhrWrite!.Resources.Single(r => r.ResourceType == type);

    [Fact]
    public async Task An_ecw_patient_is_found_by_identifier_and_never_matched()
    {
        var channel = new HealowChannel(dryRun: true) { IdentifierHit = IdentifierHit };

        var result = await RunAsync(Writer(), channel);

        channel.MatchCalls.Should().Be(0, because: "eCW has no Patient/$match");
        Summary(result, "AllergyIntolerance").WouldWrite.Should().Be(1);
        Summary(result, "Patient").Reasons.Should().ContainKey("patient-already-in-ehr");
    }

    [Fact]
    public async Task Without_an_identifier_hit_or_an_mpi_the_record_waits_for_the_mpi()
    {
        var result = await RunAsync(Writer(), new HealowChannel(dryRun: true));

        Summary(result, "AllergyIntolerance").Reasons.Should().ContainKey("patient-awaiting-mpi");
    }

    [Fact]
    public async Task The_mpi_resolves_a_patient_the_identifier_search_could_not()
    {
        var matcher = new Mock<IEhrTargetPatientMatcher>();
        matcher.Setup(m => m.MatchAsync(It.IsAny<EhrTargetPatientMatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EhrPatientMatchOutcome(EhrPatientMatchKind.Certain, "eCW-77", null, []));
        var channel = new HealowChannel(dryRun: false, activated: true);

        var result = await RunAsync(Writer(matcher.Object), channel);

        Summary(result, "AllergyIntolerance").Written.Should().Be(1);
        channel.Sent.Should().ContainSingle().Which["patient"]!["reference"]!.GetValue<string>().Should().Be("Patient/eCW-77");
        matcher.Verify(m => m.MatchAsync(
            It.Is<EhrTargetPatientMatchRequest>(r => r.TargetVendor == SourceSystemType.Healow && r.SourcePatientId == "p1"
                                                     && r.SourceBaseUrl == "https://source.example.com/fhir"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_mpi_saying_no_record_lets_an_opted_in_destination_create_the_patient()
    {
        var matcher = new Mock<IEhrTargetPatientMatcher>();
        matcher.Setup(m => m.MatchAsync(It.IsAny<EhrTargetPatientMatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EhrPatientMatchOutcome(EhrPatientMatchKind.None, null, null, []));
        var channel = new HealowChannel(dryRun: false, activated: true, createPatient: true);

        var result = await RunAsync(Writer(matcher.Object), channel);

        Summary(result, "Patient").Written.Should().Be(1);
        Summary(result, "AllergyIntolerance").Written.Should().Be(1);
        channel.Sent.Select(r => r["resourceType"]!.GetValue<string>()).Should().Equal("Patient", "AllergyIntolerance");
    }

    [Fact]
    public async Task A_live_run_sends_nothing_until_the_contracted_apis_are_activated()
    {
        var channel = new HealowChannel(dryRun: false, activated: false) { IdentifierHit = IdentifierHit };

        var result = await RunAsync(Writer(), channel);

        channel.Sent.Should().BeEmpty();
        result.EhrWrite!.DryRun.Should().BeTrue();
        Summary(result, "AllergyIntolerance").Reasons.Should().ContainKey("vendor-activation-required");
    }

    [Fact]
    public async Task Once_activated_a_live_run_sends_the_ecw_shape()
    {
        var channel = new HealowChannel(dryRun: false, activated: true) { IdentifierHit = IdentifierHit };

        var result = await RunAsync(Writer(), channel);

        result.EhrWrite!.DryRun.Should().BeFalse();
        Summary(result, "AllergyIntolerance").Written.Should().Be(1);
        var sent = channel.Sent.Should().ContainSingle().Subject;
        sent["code"]!["coding"]![0]!["display"]!.GetValue<string>().Should().Be("Penicillin G", because: "eCW matches the allergen by display");
        sent["clinicalStatus"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("active");
    }

    [Theory]
    [InlineData("problem-list-item", "problem-list-item")]
    [InlineData("encounter-diagnosis", "encounter-diagnosis")]
    public async Task Each_condition_goes_to_its_own_ecw_api(string sourceCategory, string sentCategory)
    {
        var channel = new HealowChannel(dryRun: false, activated: true, "Condition") { IdentifierHit = IdentifierHit };

        var result = await RunAsync(Writer(), channel, Patient, Condition("c1", sourceCategory));

        Summary(result, "Condition").Written.Should().Be(1);
        channel.Sent.Single()["category"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be(sentCategory);
    }

    [Fact]
    public async Task A_condition_no_ecw_api_takes_is_skipped_with_the_first_variants_reason()
    {
        var channel = new HealowChannel(dryRun: true, activated: true, "Condition") { IdentifierHit = IdentifierHit };

        var result = await RunAsync(Writer(), channel, Patient, Condition("c1", "health-concern"));

        Summary(result, "Condition").Reasons.Should().ContainKey("not-a-problem-list-item");
    }

    [Fact]
    public async Task Medical_history_is_skipped_unless_the_destination_opts_into_a_telephone_encounter()
    {
        var channel = new HealowChannel(dryRun: false, activated: true, "Condition") { IdentifierHit = IdentifierHit };

        var result = await RunAsync(Writer(), channel, Patient, Condition("h1", "medical-history", "resolved"));

        Summary(result, "Condition").Reasons.Should().ContainKey("holder-encounter-not-enabled");
        channel.HolderEncounters.Should().Be(0);
        channel.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Medical_history_shares_one_telephone_encounter_per_patient()
    {
        var channel = new HealowChannel(dryRun: false, activated: true, "Condition") { IdentifierHit = IdentifierHit, CreateHolderEncounter = true };

        var result = await RunAsync(Writer(), channel, Patient, Condition("h1", "medical-history", "resolved"), Condition("h2", "medical-history", "resolved"));

        Summary(result, "Condition").Written.Should().Be(2);
        channel.HolderEncounters.Should().Be(1);
        channel.Sent.Should().OnlyContain(r => r["encounter"]!["reference"]!.GetValue<string>() == "Encounter/tel-1");
        channel.Sent.Should().OnlyContain(r => r["category"]![0]!["coding"]![0]!["code"]!.GetValue<string>() == "435871000124102");
    }

    [Fact]
    public async Task A_dry_run_never_creates_a_telephone_encounter()
    {
        var channel = new HealowChannel(dryRun: true, activated: true, "Condition") { IdentifierHit = IdentifierHit, CreateHolderEncounter = true };

        var result = await RunAsync(Writer(), channel, Patient, Condition("h1", "medical-history", "resolved"));

        Summary(result, "Condition").WouldWrite.Should().Be(1);
        channel.HolderEncounters.Should().Be(0);
    }

    [Fact]
    public async Task Without_a_telephone_encounter_the_history_item_is_not_sent_and_is_retried_next_run()
    {
        var channel = new HealowChannel(dryRun: false, activated: true, "Condition")
        {
            IdentifierHit = IdentifierHit, CreateHolderEncounter = true, HolderEncounterFails = true,
        };

        var result = await RunAsync(Writer(), channel, Patient, Condition("h1", "medical-history", "resolved"));

        Summary(result, "Condition").Reasons.Should().ContainKey("holder-encounter-not-created");
        channel.Sent.Should().BeEmpty();
    }

    private sealed class HealowChannel : IEhrWriteChannel
    {
        private readonly bool _dryRun;
        private readonly bool _createPatient;
        private readonly string[] _types;

        public HealowChannel(bool dryRun, bool activated = false, params string[] types)
            : this(dryRun, activated, createPatient: false, types)
        {
        }

        public HealowChannel(bool dryRun, bool activated, bool createPatient, params string[] types)
        {
            _dryRun = dryRun;
            _createPatient = createPatient;
            _types = types.Length == 0 ? ["AllergyIntolerance"] : types;
            VendorWriteApisActivated = activated;
        }

        public Guid TargetConnectionId { get; } = Guid.NewGuid();
        public SourceSystemType TargetVendor => SourceSystemType.Healow;
        public string TargetBaseUrl => "https://fhir4.healow.com/fhir/r4/JAFJCD";
        public Guid? DestinationId => null;
        public bool VendorWriteApisActivated { get; }
        public bool CreateHolderEncounter { get; init; }
        public bool HolderEncounterFails { get; init; }

        public EhrWriteBackRunOptions Options => new(
            _dryRun, _createPatient, 500, "preliminary", ["Patient", .. _types], CreateHolderEncounter: CreateHolderEncounter);

        public string? IdentifierHit { get; init; }
        public int MatchCalls { get; private set; }
        public int HolderEncounters { get; private set; }
        public List<JsonObject> Sent { get; } = [];

        public Task<string?> GetGrantedScopeAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("system/AllergyIntolerance.c system/Condition.c system/Patient.c system/Patient.r");

        public Task<EhrSearchOutcome> SearchByIdentifierAsync(string resourceType, string system, string value, CancellationToken cancellationToken) =>
            Task.FromResult(new EhrSearchOutcome(true, 200, IdentifierHit is null ? [] : [IdentifierHit], []));

        public Task<EhrSearchOutcome> SearchForPatientAsync(string resourceType, string targetPatientId, CancellationToken cancellationToken) =>
            Task.FromResult(new EhrSearchOutcome(true, 200, [], []));

        public Task<EhrPatientMatchOutcome> MatchPatientAsync(string patientJson, CancellationToken cancellationToken)
        {
            MatchCalls++;
            return Task.FromResult(new EhrPatientMatchOutcome(EhrPatientMatchKind.Certain, "wrong", 200, []));
        }

        public Task<EhrCreateOutcome> CreateAsync(string resourceType, string resourceJson, CancellationToken cancellationToken)
        {
            Sent.Add((JsonObject)JsonNode.Parse(resourceJson)!);
            return Task.FromResult(new EhrCreateOutcome(EhrCreateKind.Created, 200, $"new-{Sent.Count}", []));
        }

        public Task<EhrCreateOutcome> CreateHolderEncounterAsync(string targetPatientId, CancellationToken cancellationToken)
        {
            HolderEncounters++;
            return Task.FromResult(HolderEncounterFails
                ? new EhrCreateOutcome(EhrCreateKind.Rejected, 400, null, [new EhrOutcomeIssue("error", "processing", "123", null)])
                : new EhrCreateOutcome(EhrCreateKind.Created, 200, $"tel-{HolderEncounters}", []));
        }
    }
}
