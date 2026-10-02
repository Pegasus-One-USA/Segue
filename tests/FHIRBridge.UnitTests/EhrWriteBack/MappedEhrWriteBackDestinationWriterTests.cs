using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>
/// The write-back writer end to end over a fake EHR channel. A dry run must send nothing; a live run sends only the
/// types released for live writes, once, and records each outcome in the ledger.
/// </summary>
public sealed class MappedEhrWriteBackDestinationWriterTests
{
    private const string SourceBaseUrl = "https://source.example.com/api/FHIR/R4";
    private const string TargetBaseUrl = "https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4";

    private const string SourcePatient = """
        {"resourceType":"Patient","id":"p1",
         "name":[{"use":"official","family":"Powell","given":["Desiree"]}],
         "telecom":[{"system":"phone","value":"608-555-0142"}],
         "gender":"female","birthDate":"2014-11-14",
         "address":[{"use":"home","line":["1 Probe Way"],"city":"Verona"}]}
        """;

    private const string Allergy = """
        {"resourceType":"AllergyIntolerance","id":"a1",
         "code":{"coding":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"7980"}],"text":"Penicillin G"},
         "patient":{"reference":"Patient/p1"}}
        """;

    private const string Vital = """
        {"resourceType":"Observation","id":"o1","status":"final",
         "category":[{"coding":[{"code":"vital-signs"}]}],
         "code":{"coding":[{"system":"http://loinc.org","code":"29463-7"}]},
         "subject":{"reference":"Patient/p1"},"effectiveDateTime":"2026-09-30T11:47:21Z",
         "valueQuantity":{"value":36.2,"unit":"kg"}}
        """;

    private static MappedEhrWriteBackDestinationWriter CreateWriter(
        InMemoryEhrWriteLedgerRepository? ledger = null,
        params string[] released) =>
        new(
            new EhrWriteProfileRegistry(
            [
                new EpicAllergyIntoleranceWriteProfile(),
                new EpicConditionWriteProfile(),
                new EpicClinicalNoteWriteProfile(),
                new EpicVitalSignWriteProfile(),
                new EpicPatientWriteProfile(),
            ]),
            ledger ?? new InMemoryEhrWriteLedgerRepository(),
            new FixedReleasePolicy(released),
            NullLogger<MappedEhrWriteBackDestinationWriter>.Instance);

    private static DestinationConfiguration Destination() =>
        new("Epic write-back", DestinationType.EhrWriteBack, new SecretReference("kv", "secret"), null, "{}");

    private static MappingProfile Profile() => new("EHR", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient", []);

    private static MappedDestinationRecord Record(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var type = node["resourceType"]!.GetValue<string>();
        return new MappedDestinationRecord(Guid.NewGuid(), type, type, node["id"]?.GetValue<string>(), new Dictionary<string, object?>(), json);
    }

    /// <summary>The upstream source can be asked for the patient, as the Runtime executor wires it.</summary>
    private static PipelineWriteContext Context(IEhrWriteChannel? channel, string? sourceBaseUrl = SourceBaseUrl) =>
        new(false, "Workflow", DateTimeOffset.UtcNow,
            FetchMissingReferenceAsync: (type, id, _) => Task.FromResult<string?>(type == "Patient" && id == "p1" ? SourcePatient : null),
            SourceBaseUrl: sourceBaseUrl, EhrWriteChannel: channel);

    [Fact]
    public async Task Without_a_channel_the_writer_refuses_to_run()
    {
        var act = () => CreateWriter().WriteAsync(Destination(), Profile(), [Record(Allergy)], Context(channel: null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*only run inside a workflow*");
    }

    [Fact]
    public async Task Dry_run_resolves_the_patient_by_match_and_reports_what_it_would_write_without_sending()
    {
        var channel = new FakeEhrWriteChannel { MatchResult = new EhrPatientMatchOutcome(EhrPatientMatchKind.Certain, "eTarget", 200, []) };

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Allergy)], Context(channel), CancellationToken.None);

        channel.Creates.Should().BeEmpty();
        channel.MatchCalls.Should().Be(1, because: "the patient is resolved once and cached for every record");
        result.Count.Should().Be(0);
        result.WrittenResourceIds.Should().BeEmpty();
        result.EhrWrite!.DryRun.Should().BeTrue();
        var allergy = result.EhrWrite.Resources.Single(r => r.ResourceType == "AllergyIntolerance");
        allergy.WouldWrite.Should().Be(1);
        var patient = result.EhrWrite.Resources.Single(r => r.ResourceType == "Patient");
        patient.Reasons.Should().ContainKey("patient-already-in-ehr");
    }

    [Fact]
    public async Task Live_option_without_a_released_type_is_still_a_dry_run()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false);

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Allergy)], Context(channel), CancellationToken.None);

        channel.Creates.Should().BeEmpty();
        result.EhrWrite!.DryRun.Should().BeTrue();
        var allergy = result.EhrWrite.Resources.Single(r => r.ResourceType == "AllergyIntolerance");
        allergy.WouldWrite.Should().Be(1);
        allergy.Reasons.Should().ContainKey("live-write-not-released");
    }

    [Fact]
    public async Task Dry_run_sends_nothing_even_when_the_type_is_released()
    {
        var channel = new FakeEhrWriteChannel(dryRun: true);

        var result = await CreateWriter(null, "AllergyIntolerance").WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        channel.Creates.Should().BeEmpty();
        result.EhrWrite!.DryRun.Should().BeTrue();
        result.EhrWrite.Resources.Single().Reasons.Should().NotContainKey("live-write-not-released");
    }

    [Fact]
    public async Task Live_run_sends_a_released_type_once_and_records_it_as_written()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var channel = new FakeEhrWriteChannel(dryRun: false);

        var first = await CreateWriter(ledger, "AllergyIntolerance").WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);
        var second = await CreateWriter(ledger, "AllergyIntolerance").WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        channel.Creates.Should().Equal(["AllergyIntolerance"], because: "the ledger stops the replay");
        first.EhrWrite!.DryRun.Should().BeFalse();
        first.Count.Should().Be(1);
        first.WrittenResourceIds.Should().Equal(["a1"]);
        first.EhrWrite.Resources.Single().Written.Should().Be(1);
        second.EhrWrite!.Resources.Single().Reasons.Should().ContainKey("already-written");
        var row = (await ledger.FindAsync(
            EhrWriteKeys.TargetKey(TargetBaseUrl),
            "AllergyIntolerance",
            [EhrWriteKeys.SourceKey(SourceBaseUrl, "AllergyIntolerance", "a1")],
            CancellationToken.None)).Values.Single();
        row.State.Should().Be(EhrWriteLedgerState.Written);
        row.TargetResourceId.Should().Be("new-id");
    }

    [Fact]
    public async Task Only_released_types_are_sent_in_a_live_run()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false)
        {
            Encounters = ["""{"resourceType":"Encounter","id":"eOpen","status":"in-progress"}"""],
        };

        var result = await CreateWriter(null, "AllergyIntolerance").WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Allergy), Record(Vital)], Context(channel), CancellationToken.None);

        channel.Creates.Should().Equal(["AllergyIntolerance"]);
        var vital = result.EhrWrite!.Resources.Single(r => r.ResourceType == "Observation");
        vital.WouldWrite.Should().Be(1);
        vital.Reasons.Should().ContainKey("live-write-not-released");
    }

    [Fact]
    public async Task An_unknown_outcome_is_never_resent_until_a_reviewer_releases_it()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var channel = new FakeEhrWriteChannel(dryRun: false) { CreateResult = new EhrCreateOutcome(EhrCreateKind.Unknown, 504, null, []) };

        var first = await CreateWriter(ledger, "AllergyIntolerance").WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);
        var second = await CreateWriter(ledger, "AllergyIntolerance").WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        first.EhrWrite!.Resources.Single().Unknown.Should().Be(1);
        second.EhrWrite!.Resources.Single().Reasons.Should().ContainKey("awaiting-review");
        channel.Creates.Should().HaveCount(1);

        var row = (await ledger.ListNeedingReviewAsync(null, DateTime.UtcNow, 0, 10, CancellationToken.None)).Items.Single();
        row.ReleaseForResend("reviewer@example.com", DateTime.UtcNow);
        channel.CreateResult = new EhrCreateOutcome(EhrCreateKind.Created, 201, "eAllergy", []);

        var third = await CreateWriter(ledger, "AllergyIntolerance").WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        channel.Creates.Should().HaveCount(2);
        third.EhrWrite!.Resources.Single().Written.Should().Be(1);
        row.State.Should().Be(EhrWriteLedgerState.Written);
        row.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task A_refused_create_is_recorded_as_rejected_with_its_vendor_code_only()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var channel = new FakeEhrWriteChannel(dryRun: false)
        {
            CreateResult = new EhrCreateOutcome(EhrCreateKind.Rejected, 422, null, [new EhrOutcomeIssue("error", "processing", "59012", null)]),
        };

        var result = await CreateWriter(ledger, "AllergyIntolerance").WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        result.EhrWrite!.Resources.Single().Rejected.Should().Be(1);
        result.RecordErrors.Should().ContainSingle().Which.Should().Be("AllergyIntolerance #1: rejected by the EHR (422, 59012)");
        var row = (await ledger.ListNeedingReviewAsync(null, DateTime.UtcNow, 0, 10, CancellationToken.None)).Items.Single();
        row.State.Should().Be(EhrWriteLedgerState.Rejected);
        row.OutcomeCodes.Should().Be("59012");
    }

    [Fact]
    public async Task A_duplicate_record_outcome_counts_as_already_in_the_ehr()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false)
        {
            CreateResult = new EhrCreateOutcome(EhrCreateKind.Rejected, 400, null, [new EhrOutcomeIssue("error", "duplicate", "59141", null)]),
        };

        var result = await CreateWriter(null, "AllergyIntolerance").WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        var allergy = result.EhrWrite!.Resources.Single();
        allergy.AlreadyWritten.Should().Be(1);
        allergy.Rejected.Should().Be(0);
        result.RecordErrors.Should().BeNull();
    }

    [Fact]
    public async Task Records_read_from_the_same_ehr_are_already_there_and_never_sent_back()
    {
        var channel = new FakeEhrWriteChannel();

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel, sourceBaseUrl: TargetBaseUrl + "/"), CancellationToken.None);

        channel.MatchCalls.Should().Be(0);
        var allergy = result.EhrWrite!.Resources.Single();
        allergy.WouldWrite.Should().Be(0);
        allergy.AlreadyWritten.Should().Be(1);
        allergy.Reasons.Should().ContainKey("already-in-ehr");
    }

    [Fact]
    public async Task Without_one_known_source_the_writer_refuses_to_run()
    {
        var act = () => CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(new FakeEhrWriteChannel(), sourceBaseUrl: null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exactly one upstream FHIR source*");
    }

    [Fact]
    public async Task A_record_without_an_id_is_rejected_rather_than_keyed_by_position()
    {
        var noId = Allergy.Replace("\"id\":\"a1\",", string.Empty);
        var record = new MappedDestinationRecord(Guid.NewGuid(), "AllergyIntolerance", "AllergyIntolerance", null, new Dictionary<string, object?>(), noId);

        var result = await CreateWriter().WriteAsync(Destination(), Profile(), [record], Context(new FakeEhrWriteChannel()), CancellationToken.None);

        result.RecordErrors.Should().ContainSingle().Which.Should().EndWith("missing-id");
    }

    [Fact]
    public async Task Nothing_selected_means_nothing_is_written()
    {
        var channel = new FakeEhrWriteChannel(resourceTypes: []);

        var result = await CreateWriter().WriteAsync(Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        result.EhrWrite!.Resources.Single().Reasons.Should().ContainKey("not-selected");
    }

    [Fact]
    public async Task Ambiguous_match_is_left_for_a_person_and_never_written()
    {
        var channel = new FakeEhrWriteChannel { MatchResult = new EhrPatientMatchOutcome(EhrPatientMatchKind.Ambiguous, null, 400, []) };

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Allergy)], Context(channel), CancellationToken.None);

        var allergy = result.EhrWrite!.Resources.Single(r => r.ResourceType == "AllergyIntolerance");
        allergy.WouldWrite.Should().Be(0);
        allergy.Reasons.Should().ContainKey("patient-match-needs-review");
    }

    [Fact]
    public async Task Vital_without_an_open_encounter_is_skipped()
    {
        var channel = new FakeEhrWriteChannel
        {
            MatchResult = new EhrPatientMatchOutcome(EhrPatientMatchKind.Certain, "eTarget", 200, []),
            Encounters = ["""{"resourceType":"Encounter","id":"eDone","status":"finished"}"""],
        };

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Vital)], Context(channel), CancellationToken.None);

        var vital = result.EhrWrite!.Resources.Single(r => r.ResourceType == "Observation");
        vital.WouldWrite.Should().Be(0);
        vital.Reasons.Should().ContainKey("no-eligible-encounter");
    }

    [Fact]
    public async Task Vital_with_an_open_encounter_would_be_written()
    {
        var channel = new FakeEhrWriteChannel
        {
            MatchResult = new EhrPatientMatchOutcome(EhrPatientMatchKind.Certain, "eTarget", 200, []),
            Encounters =
            [
                """{"resourceType":"Encounter","id":"eDone","status":"finished"}""",
                """{"resourceType":"Encounter","id":"eOpen","status":"in-progress"}""",
            ],
        };

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Vital)], Context(channel), CancellationToken.None);

        result.EhrWrite!.Resources.Single(r => r.ResourceType == "Observation").WouldWrite.Should().Be(1);
    }

    [Fact]
    public async Task A_record_already_in_the_ledger_is_not_planned_again()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var channel = new FakeEhrWriteChannel();
        var writer = CreateWriter(ledger);
        var context = Context(channel);

        // Shape the record once to learn the hash the writer will compute, then record it as written.
        var shaped = new EpicAllergyIntoleranceWriteProfile().Shape(System.Text.Json.Nodes.JsonNode.Parse(Allergy)!.AsObject(), channel.Options).Resource!;
        new EpicAllergyIntoleranceWriteProfile().BindReferences(shaped, "eTarget", null);
        var entry = new EhrWriteLedgerEntry(
            EhrWriteKeys.TargetKey(TargetBaseUrl), channel.TargetConnectionId, "AllergyIntolerance",
            EhrWriteKeys.SourceKey(SourceBaseUrl, "AllergyIntolerance", "a1"), EhrWriteKeys.ContentHash(shaped),
            EhrWriteOperation.Create, null, null, DateTime.UtcNow);
        entry.MarkWritten("eAllergy", 201, DateTime.UtcNow);
        await ledger.TryAddAsync(entry, CancellationToken.None);

        var result = await writer.WriteAsync(Destination(), Profile(), [Record(Allergy)], context, CancellationToken.None);

        var allergy = result.EhrWrite!.Resources.Single();
        allergy.WouldWrite.Should().Be(0);
        allergy.AlreadyWritten.Should().Be(1);
        allergy.Reasons.Should().ContainKey("already-written");
    }

    [Fact]
    public async Task Unselected_and_unwritable_types_are_skipped_with_their_reason()
    {
        var channel = new FakeEhrWriteChannel(resourceTypes: ["Condition"]);
        var immunization = """{"resourceType":"Immunization","id":"i1","patient":{"reference":"Patient/p1"}}""";

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(Allergy), Record(immunization)], Context(channel), CancellationToken.None);

        result.EhrWrite!.Resources.Single(r => r.ResourceType == "AllergyIntolerance").Reasons.Should().ContainKey("not-selected");
        result.EhrWrite.Resources.Single(r => r.ResourceType == "Immunization").Reasons.Should().ContainKey("not-selected");
    }

    [Fact]
    public async Task Write_cap_limits_what_one_call_plans()
    {
        var channel = new FakeEhrWriteChannel(maxWrites: 1);
        var second = Allergy.Replace("\"id\":\"a1\"", "\"id\":\"a2\"");

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(Allergy), Record(second)], Context(channel), CancellationToken.None);

        var allergy = result.EhrWrite!.Resources.Single();
        allergy.WouldWrite.Should().Be(1);
        allergy.Reasons.Should().ContainKey("write-cap-reached");
    }

    [Fact]
    public async Task Rejected_records_become_phi_free_record_errors()
    {
        var channel = new FakeEhrWriteChannel();
        var noCode = """{"resourceType":"AllergyIntolerance","id":"a9","patient":{"reference":"Patient/p1"}}""";

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(noCode)], Context(channel), CancellationToken.None);

        result.RecordErrors.Should().ContainSingle().Which.Should().Be("AllergyIntolerance #1: missing-code");
    }

    [Theory]
    [InlineData("system/AllergyIntolerance.write system/Patient.read", "verified")]
    [InlineData("system/Patient.read", "missing:AllergyIntolerance")]
    [InlineData(null, "unknown")]
    public async Task Granted_scope_is_checked_against_what_would_be_written(string? granted, string expected)
    {
        var channel = new FakeEhrWriteChannel { GrantedScope = granted };

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        result.EhrWrite!.ScopeStatus.Should().Be(expected);
    }

    private sealed class FakeEhrWriteChannel : IEhrWriteChannel
    {
        public FakeEhrWriteChannel(bool dryRun = true, int maxWrites = 500, IReadOnlyList<string>? resourceTypes = null)
        {
            Options = new EhrWriteBackRunOptions(
                dryRun, false, maxWrites, "preliminary",
                resourceTypes ?? ["AllergyIntolerance", "Condition", "DocumentReference", "Observation", "Patient"]);
        }

        public Guid TargetConnectionId { get; } = Guid.NewGuid();
        public SourceSystemType TargetVendor => SourceSystemType.Epic;
        public string TargetBaseUrl => MappedEhrWriteBackDestinationWriterTests.TargetBaseUrl;
        public Guid? DestinationId => null;
        public EhrWriteBackRunOptions Options { get; }

        public string? GrantedScope { get; init; } = "system/AllergyIntolerance.write system/Observation.write system/Patient.write";
        public EhrPatientMatchOutcome MatchResult { get; init; } = new(EhrPatientMatchKind.Certain, "eTarget", 200, []);
        public IReadOnlyList<string> Encounters { get; init; } = [];
        public List<string> Creates { get; } = [];
        public int MatchCalls { get; private set; }
        public int IdentifierSearches { get; private set; }

        public Task<string?> GetGrantedScopeAsync(CancellationToken cancellationToken) => Task.FromResult(GrantedScope);

        public Task<EhrSearchOutcome> SearchByIdentifierAsync(string resourceType, string system, string value, CancellationToken cancellationToken)
        {
            IdentifierSearches++;
            return Task.FromResult(new EhrSearchOutcome(true, 200, [], []));
        }

        public Task<EhrSearchOutcome> SearchForPatientAsync(string resourceType, string targetPatientId, CancellationToken cancellationToken) =>
            Task.FromResult(new EhrSearchOutcome(true, 200, Encounters, []));

        public Task<EhrPatientMatchOutcome> MatchPatientAsync(string patientJson, CancellationToken cancellationToken)
        {
            MatchCalls++;
            return Task.FromResult(MatchResult);
        }

        public EhrCreateOutcome CreateResult { get; set; } = new(EhrCreateKind.Created, 201, "new-id", []);

        public Task<EhrCreateOutcome> CreateAsync(string resourceType, string resourceJson, CancellationToken cancellationToken)
        {
            Creates.Add(resourceType);
            return Task.FromResult(CreateResult);
        }
    }

    private sealed class FixedReleasePolicy : IEhrWriteReleasePolicy
    {
        private readonly IReadOnlySet<string> _released;

        public FixedReleasePolicy(IEnumerable<string> released)
        {
            _released = new HashSet<string>(released, StringComparer.Ordinal);
        }

        public Task<IReadOnlySet<string>> GetReleasedResourceTypesAsync(SourceSystemType vendor, CancellationToken cancellationToken) =>
            Task.FromResult(_released);
    }
}
