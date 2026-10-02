using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Healow;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>
/// eClinicalWorks (Healow) write-back: only vendor data and profiles were added, so the same writer must resolve
/// eCW patients by identifier (eCW has no $match) and keep every eCW write a dry run until one is verified.
/// </summary>
public sealed class HealowWriteBackTests
{
    private const string Patient = """
        {"resourceType":"Patient","id":"p1","identifier":[{"system":"urn:oid:2.16.840.1.113883.19.5","value":"MRN-1"}],
         "name":[{"family":"Powell","given":["Desiree"]}],"gender":"female","birthDate":"2014-11-14"}
        """;

    private const string Allergy = """
        {"resourceType":"AllergyIntolerance","id":"a1",
         "code":{"coding":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"7980"}],"text":"Penicillin G"},
         "patient":{"reference":"Patient/p1"}}
        """;

    private static MappedEhrWriteBackDestinationWriter Writer(params string[] released)
    {
        var policy = new Mock<IEhrWriteReleasePolicy>();
        policy.Setup(p => p.GetReleasedResourceTypesAsync(It.IsAny<SourceSystemType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceSystemType vendor, CancellationToken _) =>
                EhrWriteBackSettings.ReleasedResourceTypes(string.Join(',', released.Select(r => $"{vendor}:{r}")), vendor));
        return new MappedEhrWriteBackDestinationWriter(
            new EhrWriteProfileRegistry(
            [
                new EpicAllergyIntoleranceWriteProfile(), new EpicPatientWriteProfile(),
                new HealowAllergyIntoleranceWriteProfile(), new HealowConditionWriteProfile(),
                new HealowVitalSignWriteProfile(), new HealowPatientWriteProfile(),
            ]),
            new InMemoryEhrWriteLedgerRepository(),
            policy.Object,
            NullLogger<MappedEhrWriteBackDestinationWriter>.Instance);
    }

    private static MappedDestinationRecord Record(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var type = node["resourceType"]!.GetValue<string>();
        return new MappedDestinationRecord(Guid.NewGuid(), type, type, node["id"]!.GetValue<string>(), new Dictionary<string, object?>(), json);
    }

    private static Task<DestinationWriteResult> RunAsync(MappedEhrWriteBackDestinationWriter writer, HealowChannel channel) =>
        writer.WriteAsync(
            new DestinationConfiguration("eCW", DestinationType.EhrWriteBack, new SecretReference("kv", "s"), null, "{}"),
            new MappingProfile("EHR", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient", []),
            [Record(Patient), Record(Allergy)],
            new PipelineWriteContext(false, "Workflow", DateTimeOffset.UtcNow, SourceBaseUrl: "https://source.example.com/fhir", EhrWriteChannel: channel),
            CancellationToken.None);

    [Fact]
    public async Task An_ecw_patient_is_found_by_identifier_and_never_matched()
    {
        var channel = new HealowChannel(dryRun: true) { IdentifierHit = """{"resourceType":"Patient","id":"eCW-42","identifier":[{"system":"urn:oid:2.16.840.1.113883.19.5","value":"MRN-1"}]}""" };

        var result = await RunAsync(Writer(), channel);

        channel.MatchCalls.Should().Be(0, because: "eCW has no Patient/$match");
        result.EhrWrite!.Resources.Single(r => r.ResourceType == "AllergyIntolerance").WouldWrite.Should().Be(1);
        result.EhrWrite.Resources.Single(r => r.ResourceType == "Patient").Reasons.Should().ContainKey("patient-already-in-ehr");
    }

    [Fact]
    public async Task Without_an_identifier_hit_an_ecw_record_is_left_unmatched()
    {
        var result = await RunAsync(Writer(), new HealowChannel(dryRun: true));

        result.EhrWrite!.Resources.Single(r => r.ResourceType == "AllergyIntolerance").Reasons.Should().ContainKey("patient-not-matched");
    }

    [Fact]
    public async Task An_ecw_live_run_sends_nothing_because_no_ecw_type_is_live_capable()
    {
        var channel = new HealowChannel(dryRun: false) { IdentifierHit = """{"resourceType":"Patient","id":"eCW-42","identifier":[{"system":"urn:oid:2.16.840.1.113883.19.5","value":"MRN-1"}]}""" };

        var result = await RunAsync(Writer("AllergyIntolerance", "Patient"), channel);

        channel.Creates.Should().Be(0);
        result.EhrWrite!.DryRun.Should().BeTrue();
        result.EhrWrite.Resources.Single(r => r.ResourceType == "AllergyIntolerance").Reasons.Should().ContainKey("live-write-not-released");
    }

    private sealed class HealowChannel : IEhrWriteChannel
    {
        public HealowChannel(bool dryRun)
        {
            Options = new EhrWriteBackRunOptions(dryRun, false, 500, "preliminary", ["Patient", "AllergyIntolerance"]);
        }

        public Guid TargetConnectionId { get; } = Guid.NewGuid();
        public SourceSystemType TargetVendor => SourceSystemType.Healow;
        public string TargetBaseUrl => "https://fhir4.healow.com/fhir/r4/JAFJCD";
        public Guid? DestinationId => null;
        public EhrWriteBackRunOptions Options { get; }
        public string? IdentifierHit { get; init; }
        public int MatchCalls { get; private set; }
        public int Creates { get; private set; }

        public Task<string?> GetGrantedScopeAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("system/AllergyIntolerance.c system/Patient.r");

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
            Creates++;
            return Task.FromResult(new EhrCreateOutcome(EhrCreateKind.Created, 201, "x", []));
        }
    }
}
