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
/// live-capable types, once, and records each outcome in the ledger.
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
        bool cloneModeEnabled = false) =>
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
            new FixedCloneModePolicy(cloneModeEnabled),
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
    public async Task A_live_run_needs_no_installation_wide_release()
    {
        // No release list exists any more: unticking Dry run on a destination that selects a live-capable type is
        // enough. Who may do that, and run it, is decided by the EHR Write-Back permissions.
        var channel = new FakeEhrWriteChannel(dryRun: false);

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Allergy)], Context(channel), CancellationToken.None);

        channel.Creates.Should().Equal(["AllergyIntolerance"]);
        result.EhrWrite!.DryRun.Should().BeFalse();
        var allergy = result.EhrWrite.Resources.Single(r => r.ResourceType == "AllergyIntolerance");
        allergy.Written.Should().Be(1);
        allergy.Reasons.Should().NotContainKey("live-write-not-supported");
    }

    [Fact]
    public async Task Dry_run_sends_nothing_even_for_a_live_capable_type()
    {
        var channel = new FakeEhrWriteChannel(dryRun: true);

        var result = await CreateWriter(null).WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        channel.Creates.Should().BeEmpty();
        result.EhrWrite!.DryRun.Should().BeTrue();
        result.EhrWrite.Resources.Single().Reasons.Should().NotContainKey("live-write-not-supported");
    }

    [Fact]
    public async Task Live_run_sends_a_live_capable_type_once_and_records_it_as_written()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var channel = new FakeEhrWriteChannel(dryRun: false);

        var first = await CreateWriter(ledger).WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);
        var second = await CreateWriter(ledger).WriteAsync(
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
    public async Task Only_selected_types_are_sent_in_a_live_run()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false, resourceTypes: ["AllergyIntolerance"])
        {
            Encounters = ["""{"resourceType":"Encounter","id":"eOpen","status":"in-progress"}"""],
        };

        var result = await CreateWriter(null).WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Allergy), Record(Vital)], Context(channel), CancellationToken.None);

        channel.Creates.Should().Equal(["AllergyIntolerance"]);
        var vital = result.EhrWrite!.Resources.Single(r => r.ResourceType == "Observation");
        vital.Skipped.Should().Be(1);
        vital.Reasons.Should().ContainKey("not-selected");
    }

    [Fact]
    public async Task Every_selected_live_capable_type_is_sent_in_a_live_run()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false)
        {
            Encounters = ["""{"resourceType":"Encounter","id":"eOpen","status":"in-progress"}"""],
        };

        var result = await CreateWriter(null).WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Allergy), Record(Vital)], Context(channel), CancellationToken.None);

        channel.Creates.Should().Equal(["AllergyIntolerance", "Observation"]);
        result.EhrWrite!.DryRun.Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_outcome_is_never_resent_until_a_reviewer_releases_it()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var channel = new FakeEhrWriteChannel(dryRun: false) { CreateResult = new EhrCreateOutcome(EhrCreateKind.Unknown, 504, null, []) };

        var first = await CreateWriter(ledger).WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);
        var second = await CreateWriter(ledger).WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        first.EhrWrite!.Resources.Single().Unknown.Should().Be(1);
        second.EhrWrite!.Resources.Single().Reasons.Should().ContainKey("awaiting-review");
        channel.Creates.Should().HaveCount(1);

        var row = (await ledger.ListNeedingReviewAsync(null, DateTime.UtcNow, 0, 10, CancellationToken.None)).Items.Single();
        row.ReleaseForResend("reviewer@example.com", DateTime.UtcNow);
        channel.CreateResult = new EhrCreateOutcome(EhrCreateKind.Created, 201, "eAllergy", []);

        var third = await CreateWriter(ledger).WriteAsync(
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

        var result = await CreateWriter(ledger).WriteAsync(
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

        var result = await CreateWriter(null).WriteAsync(
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

    // ---- Notes whose text is a link to a Binary on the source (eClinicalWorks, Epic) ----

    private static string LinkedNote(string url) => $$$"""
        {"resourceType":"DocumentReference","id":"n1","status":"current",
         "type":{"coding":[{"system":"http://loinc.org","code":"11506-3"}]},
         "category":[{"coding":[{"code":"clinical-note"}]}],
         "subject":{"reference":"Patient/p1"},
         "content":[{"attachment":{"contentType":"text/html","url":"{{{url}}}"}}]}
        """;

    private static readonly string HtmlBinary =
        $$"""{"resourceType":"Binary","id":"b1","contentType":"text/html","data":"{{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("<p>Seen today.</p>"))}}"}""";

    /// <summary>A source that serves the patient and, through <paramref name="binary"/>, Binary reads.</summary>
    private static PipelineWriteContext ContextWithBinary(IEhrWriteChannel channel, Func<string, string?> binary, List<string>? reads = null) =>
        new(false, "Workflow", DateTimeOffset.UtcNow,
            FetchMissingReferenceAsync: (type, id, _) =>
            {
                reads?.Add($"{type}/{id}");
                return Task.FromResult(type switch
                {
                    "Patient" when id == "p1" => SourcePatient,
                    "Binary" => binary(id),
                    _ => null,
                });
            },
            SourceBaseUrl: SourceBaseUrl, EhrWriteChannel: channel);

    private static FakeEhrWriteChannel NoteChannel() => new(dryRun: false)
    {
        Encounters = ["""{"resourceType":"Encounter","id":"eVisit","status":"finished"}"""],
    };

    [Theory]
    [InlineData("Binary/b1")]
    [InlineData(SourceBaseUrl + "/Binary/b1")]
    public async Task A_note_linked_to_a_source_binary_is_read_from_the_source_and_filed_with_its_text(string url)
    {
        var channel = NoteChannel();
        var reads = new List<string>();

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(LinkedNote(url))],
            ContextWithBinary(channel, id => id == "b1" ? HtmlBinary : null, reads), CancellationToken.None);

        reads.Should().Contain("Binary/b1");
        channel.Creates.Should().Equal(["DocumentReference"]);
        var attachment = channel.Sent.Single().Body["content"]![0]!["attachment"]!.AsObject();
        attachment.ContainsKey("url").Should().BeFalse();
        attachment["contentType"]!.GetValue<string>().Should().Be("text/plain");
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(attachment["data"]!.GetValue<string>()))
            .Should().Contain("Seen today.");
        result.EhrWrite!.Resources.Single(r => r.ResourceType == "DocumentReference").Written.Should().Be(1);
    }

    [Theory]
    [InlineData("https://elsewhere.example.com/fhir/Binary/b1")]
    [InlineData(SourceBaseUrl + "/Binary/b1?download=true")]
    [InlineData("DocumentReference/b1")]
    public async Task A_note_linked_anywhere_but_a_source_binary_is_never_followed(string url)
    {
        var channel = NoteChannel();
        var reads = new List<string>();

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(LinkedNote(url))],
            ContextWithBinary(channel, _ => HtmlBinary, reads), CancellationToken.None);

        reads.Should().NotContain(r => r.StartsWith("Binary/"));
        channel.Creates.Should().BeEmpty();
        result.RecordErrors.Should().ContainSingle().Which.Should().Be("DocumentReference #2: note-content-url-not-on-source");
    }

    [Fact]
    public async Task A_note_takes_its_text_from_a_binary_the_bulk_export_delivered_without_reading_the_source()
    {
        // eCW "Backend - Bulk API" tokens are refused on Binary/{id} reads, so the export's own Binary file is used.
        var channel = NoteChannel();
        var reads = new List<string>();

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(LinkedNote("Binary/b1")), Record(HtmlBinary)],
            ContextWithBinary(channel, _ => throw new InvalidOperationException("401 No valid token found"), reads), CancellationToken.None);

        reads.Should().NotContain(r => r.StartsWith("Binary/"));
        channel.Creates.Should().Equal(["DocumentReference"]);
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
                channel.Sent.Single().Body["content"]![0]!["attachment"]!["data"]!.GetValue<string>()))
            .Should().Contain("Seen today.");
        result.EhrWrite!.RecordsReceived.Should().Be(2, because: "a Binary is the text of a note, not a record to write");
        result.EhrWrite.Resources.Should().NotContain(r => r.ResourceType == "Binary");
    }

    [Fact]
    public async Task A_ccda_summary_document_is_skipped_before_its_text_is_read()
    {
        // eCW serves its Continuity of Care Document as a DocumentReference (LOINC 34133-9, C-CDA XML in a Binary).
        // It is a structured summary of the chart, not a note, so it is never filed, and never fetched either.
        var ccd = LinkedNote("Binary/b1")
            .Replace("11506-3", "34133-9")
            .Replace("\"contentType\":\"text/html\"", "\"contentType\":\"application/xml\"")
            .Replace("}}]}", "},\"format\":{\"code\":\"urn:hl7-org:sdwg:ccda-structuredBody:2.1\"}}]}");
        var channel = NoteChannel();
        var reads = new List<string>();

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(ccd)],
            ContextWithBinary(channel, _ => throw new InvalidOperationException("401"), reads), CancellationToken.None);

        reads.Should().NotContain(r => r.StartsWith("Binary/"));
        channel.Creates.Should().BeEmpty();
        result.RecordErrors.Should().BeNull();
        result.EhrWrite!.Resources.Single(r => r.ResourceType == "DocumentReference").Reasons.Should().ContainKey("ccda-document");
    }

    [Fact]
    public async Task A_note_whose_binary_cannot_be_read_waits_for_the_next_run()
    {
        var channel = NoteChannel();

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(LinkedNote("Binary/b1"))],
            ContextWithBinary(channel, _ => throw new InvalidOperationException("source returned 503")), CancellationToken.None);

        channel.Creates.Should().BeEmpty();
        result.RecordErrors.Should().BeNull(because: "a failed read is not the note's fault");
        result.EhrWrite!.Resources.Single(r => r.ResourceType == "DocumentReference").Reasons.Should().ContainKey("note-content-fetch-failed");
    }

    [Fact]
    public async Task A_note_whose_binary_is_missing_is_rejected()
    {
        var channel = NoteChannel();

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(LinkedNote("Binary/b1"))],
            ContextWithBinary(channel, _ => null), CancellationToken.None);

        channel.Creates.Should().BeEmpty();
        result.RecordErrors.Should().ContainSingle().Which.Should().Be("DocumentReference #2: note-content-not-found");
    }

    [Fact]
    public async Task A_linked_note_from_a_source_with_no_server_is_rejected_as_not_inline()
    {
        var channel = NoteChannel();
        var context = new PipelineWriteContext(false, "Workflow", DateTimeOffset.UtcNow,
            SourceBaseUrl: "urn:fhirbridge:tabular:notes", EhrWriteChannel: channel);

        var result = await CreateWriter().WriteAsync(
            Destination(), Profile(), [Record(LinkedNote("Binary/b1"))], context, CancellationToken.None);

        result.RecordErrors.Should().ContainSingle().Which.Should().Be("DocumentReference #1: note-content-not-inline");
    }

    [Theory]
    [InlineData("Binary/b1", "b1")]
    [InlineData("/Binary/e.Ab-3", "e.Ab-3")]
    [InlineData("HTTPS://SOURCE.example.com/api/FHIR/R4/Binary/b1", "b1")]
    [InlineData("https://source.example.com/api/FHIR/R4/Binary/b1/extra", null)]
    [InlineData("https://source.example.com/other/Binary/b1", null)]
    [InlineData("Binary/", null)]
    [InlineData("ftp://source.example.com/api/FHIR/R4/Binary/b1", null)]
    public void Only_a_binary_on_the_source_is_followed(string url, string? expected)
    {
        EhrNoteContent.BinaryIdOnSource(url, SourceBaseUrl).Should().Be(expected);
    }

    // ---- Phase 3: patients created in the run, and QA clone mode ----

    private static readonly EhrPatientMatchOutcome NoMatch = new(EhrPatientMatchKind.None, null, 200, []);

    private static string PatientReferenceOf(System.Text.Json.Nodes.JsonObject body) =>
        body["patient"]!["reference"]!.GetValue<string>();

    [Fact]
    public async Task A_patient_created_in_the_run_has_its_records_filed_against_it()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false, createPatient: true) { MatchResult = NoMatch };
        channel.CreateResults["Patient"] = new EhrCreateOutcome(EhrCreateKind.Created, 201, "eNewPatient", []);

        var result = await CreateWriter(null).WriteAsync(
            Destination(), Profile(), [Record(Allergy), Record(SourcePatient)], Context(channel), CancellationToken.None);

        channel.Creates.Should().Equal(["Patient", "AllergyIntolerance"], because: "patients go first");
        PatientReferenceOf(channel.Sent[1].Body).Should().Be("Patient/eNewPatient");
        result.EhrWrite!.Resources.Single(r => r.ResourceType == "Patient").Written.Should().Be(1);
        result.EhrWrite.Resources.Single(r => r.ResourceType == "AllergyIntolerance").Written.Should().Be(1);
    }

    [Fact]
    public async Task A_patient_missing_from_the_batch_is_created_for_its_record()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false, createPatient: true) { MatchResult = NoMatch };
        channel.CreateResults["Patient"] = new EhrCreateOutcome(EhrCreateKind.Created, 201, "eNewPatient", []);

        var result = await CreateWriter(null).WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        channel.Creates.Should().Equal(["Patient", "AllergyIntolerance"]);
        PatientReferenceOf(channel.Sent[1].Body).Should().Be("Patient/eNewPatient");
        result.EhrWrite!.Resources.Single(r => r.ResourceType == "Patient").Written.Should().Be(1);
    }

    [Fact]
    public async Task A_refused_patient_create_stops_that_patients_records()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false, createPatient: true) { MatchResult = NoMatch };
        channel.CreateResults["Patient"] = new EhrCreateOutcome(EhrCreateKind.Rejected, 400, null, [new EhrOutcomeIssue("error", "required", "59108", "identifier (ssn)")]);
        var second = Allergy.Replace("\"id\":\"a1\"", "\"id\":\"a2\"");

        var result = await CreateWriter(null).WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Allergy), Record(second)], Context(channel), CancellationToken.None);

        channel.Creates.Should().Equal(["Patient"], because: "the patient is tried once, not once per record");
        result.EhrWrite!.Resources.Single(r => r.ResourceType == "AllergyIntolerance").Reasons
            .Should().Contain(new KeyValuePair<string, int>("patient-not-created", 2));
    }

    [Fact]
    public async Task Records_are_skipped_with_a_clear_reason_when_patient_is_not_selected()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false, resourceTypes: ["AllergyIntolerance"], createPatient: true) { MatchResult = NoMatch };

        var result = await CreateWriter(null).WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Allergy)], Context(channel), CancellationToken.None);

        channel.Creates.Should().BeEmpty();
        result.EhrWrite!.Resources.Single(r => r.ResourceType == "Patient").Reasons.Should().ContainKey("not-selected");
        // No run could ever create the patient without Patient selected, so the reason says that, not "not yet".
        result.EhrWrite.Resources.Single(r => r.ResourceType == "AllergyIntolerance").Reasons.Should().ContainKey("patient-not-selected");
    }

    [Fact]
    public async Task Clone_mode_is_refused_while_its_system_setting_is_off()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false, cloneMode: true);

        var act = () => CreateWriter(null, cloneModeEnabled: false).WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*EhrWriteBack:CloneModeEnabled*");
        channel.Creates.Should().BeEmpty();
    }

    [Fact]
    public async Task Clone_mode_writes_the_ehrs_own_records_against_a_synthetic_patient_and_never_matches()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false, cloneMode: true);
        channel.CreateResults["Patient"] = new EhrCreateOutcome(EhrCreateKind.Created, 201, "eClone", []);

        var result = await CreateWriter(null, cloneModeEnabled: true).WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Allergy)], Context(channel, sourceBaseUrl: TargetBaseUrl), CancellationToken.None);

        channel.MatchCalls.Should().Be(0, because: "the real patient is exactly what must not be found");
        channel.IdentifierSearches.Should().Be(0);
        channel.Creates.Should().Equal(["Patient", "AllergyIntolerance"]);
        var clone = channel.Sent[0].Body;
        clone["name"]![0]!["family"]!.GetValue<string>().Should().Be("ZztestPowell");
        clone["birthDate"]!.GetValue<string>().Should().NotBe("2014-11-14");
        clone.ContainsKey("telecom").Should().BeFalse();
        var ssn = clone["identifier"]!.AsArray().Single()!;
        ssn["system"]!.GetValue<string>().Should().Be(EhrClonePatient.SsnSystem);
        ssn["value"]!.GetValue<string>().Should().MatchRegex(@"^9\d{2}-\d{2}-\d{4}$");
        PatientReferenceOf(channel.Sent[1].Body).Should().Be("Patient/eClone");
        result.EhrWrite!.CloneMode.Should().BeTrue();
    }

    [Fact]
    public async Task A_clone_that_comes_back_as_the_original_patient_is_refused_and_nothing_is_filed()
    {
        var channel = new FakeEhrWriteChannel(dryRun: false, cloneMode: true);
        channel.CreateResults["Patient"] = new EhrCreateOutcome(EhrCreateKind.Created, 201, "p1", []);

        var result = await CreateWriter(null, cloneModeEnabled: true).WriteAsync(
            Destination(), Profile(), [Record(SourcePatient), Record(Allergy)], Context(channel, sourceBaseUrl: TargetBaseUrl), CancellationToken.None);

        channel.Creates.Should().Equal(["Patient"]);
        result.RecordErrors.Should().Contain("Patient #1: clone-matched-original");
        result.EhrWrite!.Resources.Single(r => r.ResourceType == "AllergyIntolerance").Reasons.Should().ContainKey("patient-not-created");
    }

    [Fact]
    public void Clone_writes_are_keyed_apart_from_real_writes()
    {
        EhrWriteKeys.SourceKey(SourceBaseUrl, "AllergyIntolerance", "a1", clone: true)
            .Should().NotBe(EhrWriteKeys.SourceKey(SourceBaseUrl, "AllergyIntolerance", "a1"));
    }

    [Fact]
    public void A_clone_is_the_same_on_every_run()
    {
        var source = System.Text.Json.Nodes.JsonNode.Parse(SourcePatient)!.AsObject();

        EhrClonePatient.Build(source, "p1").ToJsonString().Should().Be(EhrClonePatient.Build(source, "p1").ToJsonString());
        EhrClonePatient.Build(source, "p1")["identifier"]!.ToJsonString()
            .Should().NotBe(EhrClonePatient.Build(source, "p2")["identifier"]!.ToJsonString());
    }

    // ---- PR #240 review: the ledger first, and a cancellation before the send is not an unknown outcome ----

    [Fact]
    public async Task A_record_written_before_is_reported_without_reading_the_source_or_searching_the_ehr_again()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var channel = NoteChannel();
        await CreateWriter(ledger).WriteAsync(
            Destination(), Profile(), [Record(LinkedNote("Binary/b1"))], ContextWithBinary(channel, _ => HtmlBinary), CancellationToken.None);
        var matchesAfterFirstRun = channel.MatchCalls;

        // The source cannot serve the Binary any more: before, the rerun reported the note as note-content-fetch-failed.
        var reads = new List<string>();
        var rerun = await CreateWriter(ledger).WriteAsync(
            Destination(), Profile(), [Record(LinkedNote("Binary/b1"))], ContextWithBinary(channel, _ => null, reads), CancellationToken.None);

        var note = rerun.EhrWrite!.Resources.Single();
        note.AlreadyWritten.Should().Be(1);
        note.Reasons.Should().ContainKey("already-written").And.NotContainKey("note-content-fetch-failed");
        reads.Should().BeEmpty(because: "neither the note text nor the patient is read for a record already written");
        channel.MatchCalls.Should().Be(matchesAfterFirstRun);
        channel.Creates.Should().ContainSingle();
    }

    [Fact]
    public async Task A_create_cancelled_before_it_was_sent_is_retried_next_run_not_left_for_review()
    {
        var ledger = new InMemoryEhrWriteLedgerRepository();
        var channel = new FakeEhrWriteChannel(dryRun: false)
        {
            CreateResult = new EhrCreateOutcome(
                EhrCreateKind.Rejected, null, null, [new EhrOutcomeIssue("error", "transient", EhrWriteOutcomeCodes.CancelledBeforeSend, null)]),
        };

        var cancelled = () => CreateWriter(ledger).WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        var row = (await ledger.ListNeedingReviewAsync(null, DateTime.UtcNow, 0, 10, CancellationToken.None)).Items.Single();
        row.State.Should().Be(EhrWriteLedgerState.Rejected, because: "nothing reached the EHR, so it is not an unknown outcome");
        row.HttpStatus.Should().BeNull(because: "a refusal with no HTTP status is retried as is");
        row.OutcomeCodes.Should().Be(EhrWriteOutcomeCodes.CancelledBeforeSend);

        channel.CreateResult = new EhrCreateOutcome(EhrCreateKind.Created, 201, "eAllergy", []);
        var next = await CreateWriter(ledger).WriteAsync(
            Destination(), Profile(), [Record(Allergy)], Context(channel), CancellationToken.None);

        next.EhrWrite!.Resources.Single().Written.Should().Be(1);
    }

    private sealed class FakeEhrWriteChannel : IEhrWriteChannel
    {
        public FakeEhrWriteChannel(
            bool dryRun = true,
            int maxWrites = 500,
            IReadOnlyList<string>? resourceTypes = null,
            bool createPatient = false,
            bool cloneMode = false)
        {
            Options = new EhrWriteBackRunOptions(
                dryRun, createPatient, maxWrites, "preliminary",
                resourceTypes ?? ["AllergyIntolerance", "Condition", "DocumentReference", "Observation", "Patient"],
                cloneMode);
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

        /// <summary>Per-type outcomes that win over <see cref="CreateResult"/>.</summary>
        public Dictionary<string, EhrCreateOutcome> CreateResults { get; } = new(StringComparer.Ordinal);

        public List<(string ResourceType, System.Text.Json.Nodes.JsonObject Body)> Sent { get; } = [];

        public Task<EhrCreateOutcome> CreateAsync(string resourceType, string resourceJson, CancellationToken cancellationToken)
        {
            Creates.Add(resourceType);
            Sent.Add((resourceType, System.Text.Json.Nodes.JsonNode.Parse(resourceJson)!.AsObject()));
            return Task.FromResult(CreateResults.GetValueOrDefault(resourceType) ?? CreateResult);
        }
    }

    private sealed class FixedCloneModePolicy : IEhrCloneModePolicy
    {
        private readonly bool _cloneModeEnabled;

        public FixedCloneModePolicy(bool cloneModeEnabled)
        {
            _cloneModeEnabled = cloneModeEnabled;
        }

        public Task<bool> IsCloneModeEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(_cloneModeEnabled);
    }
}
