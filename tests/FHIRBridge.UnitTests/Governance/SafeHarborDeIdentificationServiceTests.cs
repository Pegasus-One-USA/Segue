using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Governance;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

/// <summary>
/// Provenance carries identifying <c>display</c> text on <c>agent.who</c> and <c>target</c> references — neither
/// is a typical PHI field, so the default Safe Harbor rules need explicit entries for them. Rules are now
/// profile-scoped: a resource routed through a destination with no profile (or a different profile than the
/// one carrying these rules) must pass through unredacted — that's the whole point of per-destination profiles.
/// </summary>
public sealed class SafeHarborDeIdentificationServiceTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();
    private static readonly Guid OtherProfileId = Guid.NewGuid();

    private static readonly TransformationRule[] ProvenanceRules =
    [
        new(TransformScope.ResourceType, TransformNodeType.HashingMasking, """{"mode":"remove"}""",
            resourceType: "Provenance", sourceField: "agent.who.display",
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId),
        new(TransformScope.ResourceType, TransformNodeType.HashingMasking, """{"mode":"remove"}""",
            resourceType: "Provenance", sourceField: "target.display",
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId),
    ];

    private static SafeHarborDeIdentificationService BuildSut()
    {
        var repository = new Mock<ITransformationRuleRepository>();
        // De-identification also asks for the rules of whatever type a reference points at (Patient and
        // Practitioner here), so it can keep those references in step with a redacted id. Default every type
        // to "no rules"; the specific setups below then override the ones this fixture cares about. Moq takes
        // the last matching setup, so this catch-all has to come first.
        repository
            .Setup(x => x.GetPreMappingRulesAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository
            .Setup(x => x.GetPreMappingRulesAsync(ProfileId, "Provenance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProvenanceRules);
        repository
            .Setup(x => x.GetPreMappingRulesAsync(OtherProfileId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        return new SafeHarborDeIdentificationService(repository.Object);
    }

    private const string ProvenanceJson = """
        {
          "resourceType": "Provenance",
          "id": "prov-1",
          "target": [ { "reference": "Patient/patient-42", "display": "Jane Doe" } ],
          "recorded": "2026-08-18T00:00:00Z",
          "agent": [ { "who": { "reference": "Practitioner/prac-1", "display": "Dr. Jane Smith" } } ]
        }
        """;

    [Fact]
    public async Task Removes_display_text_from_provenance_agent_and_target()
    {
        var sut = BuildSut();
        var request = new DeIdentificationRequest("Provenance", "prov-1", ProvenanceJson, [], ProfileId);

        var result = await sut.DeIdentifyAsync(request, CancellationToken.None);

        result.Json.Should().NotContain("Jane Doe");
        result.Json.Should().NotContain("Dr. Jane Smith");
        result.Json.Should().Contain("Patient/patient-42");
        result.Json.Should().Contain("Practitioner/prac-1");
        result.Hops.Should().HaveCount(2);
        result.Hops.Should().OnlyContain(hop => hop.Success);
    }

    [Fact]
    public async Task Leaves_resource_unchanged_when_no_profile_is_assigned()
    {
        var sut = BuildSut();
        var request = new DeIdentificationRequest("Provenance", "prov-1", ProvenanceJson, [], ProfileId: null);

        var result = await sut.DeIdentifyAsync(request, CancellationToken.None);

        result.Json.Should().Be(ProvenanceJson);
        result.Hops.Should().BeEmpty();
    }

    [Fact]
    public async Task Leaves_resource_unchanged_when_assigned_a_different_profile()
    {
        var sut = BuildSut();
        var request = new DeIdentificationRequest("Provenance", "prov-1", ProvenanceJson, [], OtherProfileId);

        var result = await sut.DeIdentifyAsync(request, CancellationToken.None);

        result.Json.Should().Contain("Jane Doe");
        result.Json.Should().Contain("Dr. Jane Smith");
        result.Hops.Should().BeEmpty();
    }
}

/// <summary>
/// A rule's SourceField is authored in three different conventions depending on which screen wrote it, and the
/// engine walks plain property-name segments. Before ParseSourceFieldPath normalized them, a "$." or
/// "ResourceType." prefix matched no property, the rule hit the `beforeValue is null` guard and was skipped —
/// silently. The UI showed an active masking rule while unredacted PHI was written to the destination, which is
/// the exact failure these cover: the de-identification tab's field picker emits the payload tree's jsonPath.
/// </summary>
public sealed class SafeHarborSourceFieldPathFormatTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();

    private const string PatientJson = """
    {
      "resourceType": "Patient",
      "id": "e63wRTbPfr1p8UW81d8Seiw3",
      "address": [ { "line": [ "123 Main St" ], "postalCode": "90210" } ]
    }
    """;

    private static SafeHarborDeIdentificationService BuildSut(params TransformationRule[] rules)
    {
        var repository = new Mock<ITransformationRuleRepository>();
        repository
            .Setup(x => x.GetPreMappingRulesAsync(ProfileId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rules);

        return new SafeHarborDeIdentificationService(repository.Object);
    }

    private static TransformationRule MaskRule(string sourceField) =>
        new(TransformScope.ResourceType, TransformNodeType.HashingMasking, """{"mode":"mask","keepLength":4}""",
            resourceType: "Patient", sourceField: sourceField,
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId);

    [Theory]
    [InlineData("$.id")]          // de-identification tab's field picker (payload tree jsonPath)
    [InlineData("id")]            // seeded Safe Harbor defaults
    [InlineData("Patient.id")]    // transformation-rule screens' "ResourceType.field" convention
    public async Task Mask_applies_for_every_source_field_convention(string sourceField)
    {
        var sut = BuildSut(MaskRule(sourceField));

        var result = await sut.DeIdentifyAsync(
            new DeIdentificationRequest("Patient", "p1", PatientJson, [], ProfileId), CancellationToken.None);

        result.Json.Should().NotContain("e63wRTbPfr1p8UW81d8Seiw3");
        result.Json.Should().Contain("********************eiw3", "keepLength=4 keeps only the last four characters");
        result.Hops.Should().ContainSingle().Which.SourceField.Should().Be(sourceField);
    }

    [Theory]
    [InlineData("$.address.line")]
    [InlineData("address[*].line")]
    [InlineData("address[0].line")]
    public async Task Nested_and_indexed_paths_resolve_to_the_same_field(string sourceField)
    {
        var sut = BuildSut(
            new TransformationRule(TransformScope.ResourceType, TransformNodeType.HashingMasking,
                """{"mode":"remove"}""", resourceType: "Patient", sourceField: sourceField,
                executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId));

        var result = await sut.DeIdentifyAsync(
            new DeIdentificationRequest("Patient", "p1", PatientJson, [], ProfileId), CancellationToken.None);

        result.Json.Should().NotContain("123 Main St");
        result.Json.Should().Contain("90210", "only the rule's own field is touched");
    }

    /// <summary>
    /// The rule-config form persists every field as a string (config[key] = String(value)), so a keepLength of 4
    /// arrives as "4", not 4. JsonElement.TryGetInt32 THROWS on a non-number rather than returning false, and the
    /// reader caught only JsonException — so a masking rule authored in the UI took down the whole
    /// de-identification node and failed the run. Both encodings must work, and nothing here may throw.
    /// </summary>
    [Theory]
    [InlineData("\"4\"", "********************eiw3")]  // as the UI writes it
    [InlineData("4", "********************eiw3")]      // as a JSON number
    [InlineData("\"not-a-number\"", "********************eiw3")] // unparseable -> documented default of 4
    [InlineData("null", "********************eiw3")]   // wrong type entirely -> default, never a crash
    public async Task Mask_reads_keepLength_whether_it_is_a_string_or_a_number(string keepLengthJson, string expected)
    {
        var rule = new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.HashingMasking,
            $$"""{"mode":"mask","keepLength":{{keepLengthJson}}}""",
            resourceType: "Patient", sourceField: "$.id",
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId);

        var sut = BuildSut(rule);

        var act = async () => await sut.DeIdentifyAsync(
            new DeIdentificationRequest("Patient", "p1", PatientJson, [], ProfileId), CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Json.Should().Contain(expected);
    }

    [Fact]
    public void ParseSourceFieldPath_strips_prefixes_and_indexers()
    {
        SafeHarborDeIdentificationService.ParseSourceFieldPath("$.address[*].line", "Patient")
            .Should().Equal("address", "line");
        SafeHarborDeIdentificationService.ParseSourceFieldPath("Patient.id", "Patient")
            .Should().Equal("id");
        // Only the leading resource-type prefix goes: a legitimately-named inner property must survive.
        SafeHarborDeIdentificationService.ParseSourceFieldPath("contact.Patient", "Patient")
            .Should().Equal("contact", "Patient");
    }
}

/// <summary>
/// Redacting an id has to carry through to every reference pointing at it, or the redaction silently breaks the
/// resource graph: Patient.id becomes "****eiw3" while Observation.subject.reference still says
/// "Patient/&lt;original&gt;" — a SQL join that no longer matches (no error, just orphaned rows) or a dangling
/// reference on a FHIR server. These pin the property that makes it work: the parent's new id and the child's
/// rewritten reference are produced by the same pure function, so they agree without any crosswalk.
/// </summary>
public sealed class SafeHarborReferenceRewriteTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();
    private const string PatientId = "e63wRTbPfr1p8UW81d8Seiw3";

    private static TransformationRule IdRule(string resourceType, string configJson) =>
        new(TransformScope.ResourceType, TransformNodeType.HashingMasking, configJson,
            resourceType: resourceType, sourceField: "$.id",
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId);

    private static SafeHarborDeIdentificationService BuildSut(params (string ResourceType, TransformationRule[] Rules)[] rulesByType)
    {
        var repository = new Mock<ITransformationRuleRepository>();
        repository
            .Setup(x => x.GetPreMappingRulesAsync(ProfileId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string resourceType, CancellationToken _) =>
                rulesByType.FirstOrDefault(entry => entry.ResourceType == resourceType).Rules ?? []);

        return new SafeHarborDeIdentificationService(repository.Object);
    }

    private static string PatientJson => $$"""{"resourceType":"Patient","id":"{{PatientId}}"}""";

    private static string ObservationJson => $$"""
        {
          "resourceType": "Observation",
          "id": "obs-1",
          "subject": { "reference": "Patient/{{PatientId}}", "display": "Jane Doe" },
          "performer": [ { "reference": "Practitioner/prac-9" } ]
        }
        """;

    private static async Task<string> RunAsync(SafeHarborDeIdentificationService sut, string resourceType, string json)
    {
        var result = await sut.DeIdentifyAsync(
            new DeIdentificationRequest(resourceType, "x", json, [], ProfileId), CancellationToken.None);
        return result.Json;
    }

    [Theory]
    [InlineData("""{"mode":"mask","keepLength":"4"}""")]
    [InlineData("""{"mode":"hash"}""")]
    public async Task Child_reference_lands_on_the_same_value_as_the_parents_new_id(string configJson)
    {
        var sut = BuildSut(("Patient", [IdRule("Patient", configJson)]));

        var patient = JsonNode.Parse(await RunAsync(sut, "Patient", PatientJson))!;
        var observation = JsonNode.Parse(await RunAsync(sut, "Observation", ObservationJson))!;

        var newPatientId = patient["id"]!.GetValue<string>();
        newPatientId.Should().NotBe(PatientId, "the parent id must actually have been redacted");

        observation["subject"]!["reference"]!.GetValue<string>()
            .Should().Be($"Patient/{newPatientId}", "the child must resolve to the parent's redacted id");
    }

    [Fact]
    public async Task References_to_types_with_no_id_rule_are_left_alone()
    {
        var sut = BuildSut(("Patient", [IdRule("Patient", """{"mode":"hash"}""")]));

        var observation = JsonNode.Parse(await RunAsync(sut, "Observation", ObservationJson))!;

        observation["performer"]![0]!["reference"]!.GetValue<string>().Should().Be("Practitioner/prac-9");
    }

    [Fact]
    public async Task Remove_leaves_references_untouched_because_there_is_no_replacement_id()
    {
        var sut = BuildSut(("Patient", [IdRule("Patient", """{"mode":"remove"}""")]));

        var observation = JsonNode.Parse(await RunAsync(sut, "Observation", ObservationJson))!;

        observation["subject"]!["reference"]!.GetValue<string>().Should().Be($"Patient/{PatientId}");
    }

    [Fact]
    public async Task Rewrite_is_reported_as_a_lineage_hop()
    {
        var sut = BuildSut(("Patient", [IdRule("Patient", """{"mode":"hash"}""")]));

        var result = await sut.DeIdentifyAsync(
            new DeIdentificationRequest("Observation", "obs-1", ObservationJson, [], ProfileId), CancellationToken.None);

        var hop = result.Hops.Should().ContainSingle(h => h.SourceField == "subject.reference").Subject;
        hop.Strategy.Should().Be("Hash:Reference");
        hop.BeforeValueJson.Should().Contain(PatientId);
        hop.AfterValueJson.Should().NotContain(PatientId);
    }

    [Theory]
    // relative, absolute, and version-specific forms all keep their shape around the rewritten id
    [InlineData("Patient/" + PatientId, "Patient/")]
    [InlineData("http://ehr.example.org/fhir/Patient/" + PatientId, "http://ehr.example.org/fhir/Patient/")]
    [InlineData("Patient/" + PatientId + "/_history/2", "Patient/")]
    public async Task Reference_forms_are_preserved_around_the_rewritten_id(string reference, string expectedPrefix)
    {
        var sut = BuildSut(("Patient", [IdRule("Patient", """{"mode":"hash"}""")]));
        var json = $$$"""{"resourceType":"Observation","subject":{"reference":"{{{reference}}}"}}""";

        var rewritten = JsonNode.Parse(await RunAsync(sut, "Observation", json))!["subject"]!["reference"]!.GetValue<string>();

        rewritten.Should().StartWith(expectedPrefix).And.NotContain(PatientId);
        if (reference.Contains("_history")) rewritten.Should().EndWith("/_history/2");
    }

    [Theory]
    [InlineData("#contained-obs")]   // contained reference - no resource type to resolve
    [InlineData("urn:uuid:2f6b6f6d-1a2b-4c3d-9e8f-7a6b5c4d3e2f")]
    [InlineData("Patient")]          // malformed: no id segment
    public async Task Unresolvable_reference_forms_are_left_untouched(string reference)
    {
        var sut = BuildSut(("Patient", [IdRule("Patient", """{"mode":"hash"}""")]));
        var json = $$$"""{"resourceType":"Observation","subject":{"reference":"{{{reference}}}"}}""";

        var rewritten = JsonNode.Parse(await RunAsync(sut, "Observation", json))!["subject"]!["reference"]!.GetValue<string>();

        rewritten.Should().Be(reference);
    }

    /// <summary>
    /// The end-to-end property the fix exists for. MappedSqlServerDestinationWriter resolves a FK by running
    /// <c>WHERE [PatientId] = @referenceId</c>, where referenceId is JsonMappingEngine.ExtractReferenceId's
    /// "everything after the last slash" applied to the child's subject.reference, and [PatientId] holds the
    /// Patient row's own redacted $.id. Those two have to be byte-identical or the write fails outright with
    /// "no row in [dbo].[Patient] has [PatientId] = …". Mapping runs after de-identification, so both sides
    /// here are the post-redaction values the writer will actually see.
    /// </summary>
    [Theory]
    [InlineData("""{"mode":"mask","keepLength":"4"}""")]
    [InlineData("""{"mode":"hash"}""")]
    [InlineData("""{"mode":"redact","token":"REDACTED-ID"}""")]
    public async Task Sql_foreign_key_lookup_resolves_after_redaction(string configJson)
    {
        var sut = BuildSut(("Patient", [IdRule("Patient", configJson)]));

        var patientKeyColumnValue = JsonNode.Parse(await RunAsync(sut, "Patient", PatientJson))!["id"]!.GetValue<string>();
        var childReference = JsonNode.Parse(await RunAsync(sut, "Observation", ObservationJson))!
            ["subject"]!["reference"]!.GetValue<string>();

        // Mirrors JsonMappingEngine.ExtractReferenceId.
        var referenceId = childReference[(childReference.LastIndexOf('/') + 1)..];

        referenceId.Should().Be(patientKeyColumnValue,
            "the FK lookup compares exactly these two values, so a mismatch orphans every child row");
    }

    /// <summary>Epic ids carry dots and hyphens ("eGmO0h.1.UQQrExl4bfM7OQ3"), and an Encounter's Patient
    /// pointer sits at subject.reference alongside other references that must be left alone.</summary>
    [Fact]
    public async Task Epic_shaped_encounter_rewrites_only_the_patient_reference()
    {
        var sut = BuildSut(("Patient", [IdRule("Patient", """{"mode":"mask","keepLength":"4"}""")]));
        var encounter = $$"""
            {
              "resourceType": "Encounter",
              "id": "eGmO0h.1.UQQrExl4bfM7OQ3",
              "status": "finished",
              "subject": { "reference": "Patient/{{PatientId}}", "display": "Jane Doe" },
              "participant": [ { "individual": { "reference": "Practitioner/eM5CWtq-hxA3" } } ],
              "location": [ { "location": { "reference": "Location/eLoc.1.2" } } ]
            }
            """;

        var result = JsonNode.Parse(await RunAsync(sut, "Encounter", encounter))!;

        var subjectReference = result["subject"]!["reference"]!.GetValue<string>();
        subjectReference.Should().StartWith("Patient/").And.NotContain(PatientId);
        // What JsonMappingEngine.ExtractReferenceId will hand the FK lookup — must be non-empty, or the
        // writer skips resolution and the NOT NULL FK column fails with "Cannot insert the value NULL".
        subjectReference[(subjectReference.LastIndexOf('/') + 1)..].Should().NotBeEmpty();

        result["id"]!.GetValue<string>().Should().Be("eGmO0h.1.UQQrExl4bfM7OQ3", "Encounter has no id rule");
        result["participant"]![0]!["individual"]!["reference"]!.GetValue<string>().Should().Be("Practitioner/eM5CWtq-hxA3");
        result["location"]![0]!["location"]!["reference"]!.GetValue<string>().Should().Be("Location/eLoc.1.2");
    }

    [Fact]
    public void TryParseReference_splits_each_supported_form()
    {
        SafeHarborDeIdentificationService.TryParseReference(
            "http://host/fhir/Patient/123/_history/4", out var prefix, out var type, out var id, out var suffix)
            .Should().BeTrue();
        prefix.Should().Be("http://host/fhir/");
        type.Should().Be("Patient");
        id.Should().Be("123");
        suffix.Should().Be("/_history/4");

        SafeHarborDeIdentificationService.TryParseReference("#x", out _, out _, out _, out _).Should().BeFalse();
    }
}

/// <summary>
/// A redaction token is a string, so it can only stand in for a string. Writing "[REDACTED]" over a boolean or a
/// number produced a value nothing downstream could carry: the destination column it maps to is typed
/// (Patient.active -> a bit column), so every row of that resource failed to insert — and because the SQL writer
/// isolates per-record failures instead of throwing, the resource silently landed nothing and the run surfaced
/// only a later resource's FK failure against the rows that never arrived. It is invalid FHIR as well: a
/// FHIR-native destination rejects "active": "[REDACTED]" outright.
/// </summary>
public sealed class SafeHarborRedactTypeSafetyTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();

    private const string PatientJson = """
    {
      "resourceType": "Patient",
      "id": "p-1",
      "active": true,
      "multipleBirthInteger": 2,
      "gender": "female"
    }
    """;

    private static SafeHarborDeIdentificationService BuildSut(params string[] sourceFields)
    {
        var rules = sourceFields.Select(sourceField => new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.HashingMasking,
            """{"mode":"redact","token":"[REDACTED]"}""",
            resourceType: "Patient", sourceField: sourceField,
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId)).ToArray();

        var repository = new Mock<ITransformationRuleRepository>();
        repository
            .Setup(x => x.GetPreMappingRulesAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rules);

        return new SafeHarborDeIdentificationService(repository.Object);
    }

    private static async Task<JsonObject> RedactAsync(params string[] sourceFields)
    {
        var result = await BuildSut(sourceFields).DeIdentifyAsync(
            new DeIdentificationRequest("Patient", "p-1", PatientJson, [], ProfileId), CancellationToken.None);
        return (JsonObject)JsonNode.Parse(result.Json)!;
    }

    [Fact]
    public async Task Redacting_a_string_still_writes_the_token()
    {
        var patient = await RedactAsync("gender");

        patient["gender"]!.GetValue<string>().Should().Be("[REDACTED]");
    }

    [Theory]
    [InlineData("active")]                 // boolean -> a bit column
    [InlineData("multipleBirthInteger")]   // number  -> an int column
    public async Task Redacting_a_non_string_removes_the_property_rather_than_writing_a_token(string sourceField)
    {
        var patient = await RedactAsync(sourceField);

        patient.ContainsKey(sourceField).Should()
            .BeFalse("a string token in a typed column fails every row of the resource on insert");
    }

    [Fact]
    public async Task Untouched_fields_keep_their_original_types()
    {
        var patient = await RedactAsync("active");

        patient["multipleBirthInteger"]!.GetValue<int>().Should().Be(2);
        patient["gender"]!.GetValue<string>().Should().Be("female");
        patient["id"]!.GetValue<string>().Should().Be("p-1");
    }
}

/// <summary>
/// A rule whose path lands on a REPEATING primitive — name.given, address.line, every other 0..* string element
/// in FHIR — matched only a single JsonValue and so did nothing at all. The failure was invisible in the worst
/// way: on one Patient, the mask rule on "$.name[*].family" produced "**pez" while the sibling rule on
/// "$.name[*].given[*]" left "Camila" and the hash rule on "$.address[*].line[*]" left the street address
/// verbatim — three rules shown as active on the same screen, one of them working.
/// </summary>
public sealed class SafeHarborRepeatingPrimitiveTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();

    // Trimmed from the real Epic payload that exposed this.
    private const string PatientJson = """
    {
      "resourceType": "Patient",
      "id": "erXuFYUfucBZaryVksYEcMg3",
      "name": [
        { "use": "official", "family": "Lopez", "given": [ "Camila", "Maria" ] },
        { "use": "usual", "family": "Lopez", "given": [ "Camila", "Maria" ] }
      ],
      "address": [
        { "line": [ "3268 West Johnson St.", "Apt 117" ], "city": "GARLAND", "postalCode": "75043" }
      ]
    }
    """;

    private static async Task<JsonObject> ApplyAsync(string sourceField, string configJson)
    {
        var rule = new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.HashingMasking, configJson,
            resourceType: "Patient", sourceField: sourceField,
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId);

        var repository = new Mock<ITransformationRuleRepository>();
        repository
            .Setup(x => x.GetPreMappingRulesAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);

        var result = await new SafeHarborDeIdentificationService(repository.Object).DeIdentifyAsync(
            new DeIdentificationRequest("Patient", "p1", PatientJson, [], ProfileId), CancellationToken.None);

        return (JsonObject)JsonNode.Parse(result.Json)!;
    }

    private static string[] GivenNames(JsonObject patient) =>
        patient["name"]!.AsArray().SelectMany(n => n!["given"]!.AsArray()).Select(g => g!.GetValue<string>()).ToArray();

    private static string[] AddressLines(JsonObject patient) =>
        patient["address"]!.AsArray().SelectMany(a => a!["line"]!.AsArray()).Select(l => l!.GetValue<string>()).ToArray();

    [Fact]
    public async Task Mask_applies_to_every_element_of_a_repeating_primitive()
    {
        var patient = await ApplyAsync("$.name[*].given[*]", """{"mode":"mask","keepLength":4}""");

        // "Camila" -> 2 stars + "mila"; "Maria" -> 1 star + "aria".
        GivenNames(patient).Should().Equal("**mila", "*aria", "**mila", "*aria");
    }

    [Fact]
    public async Task Hash_applies_to_every_element_of_a_repeating_primitive()
    {
        var patient = await ApplyAsync("$.address[*].line[*]", """{"mode":"hash"}""");

        AddressLines(patient).Should().OnlyContain(line => line.StartsWith("anon-"));
        AddressLines(patient).Should().NotContain("3268 West Johnson St.");
    }

    [Fact]
    public async Task Redact_replaces_every_element_of_a_repeating_primitive()
    {
        var patient = await ApplyAsync("$.address[*].line[*]", """{"mode":"redact","token":"[REDACTED]"}""");

        AddressLines(patient).Should().OnlyContain(line => line == "[REDACTED]");
    }

    [Fact]
    public async Task Remove_still_deletes_the_whole_repeating_element()
    {
        var patient = await ApplyAsync("$.name[*].given[*]", """{"mode":"remove"}""");

        patient["name"]!.AsArray().Should().OnlyContain(n => !((JsonObject)n!).ContainsKey("given"));
    }

    [Fact]
    public async Task A_single_string_sibling_is_unaffected_by_the_array_handling()
    {
        var patient = await ApplyAsync("$.name[*].family", """{"mode":"mask","keepLength":3}""");

        patient["name"]!.AsArray().Select(n => n!["family"]!.GetValue<string>()).Should().OnlyContain(f => f == "**pez");
        GivenNames(patient).Should().Equal("Camila", "Maria", "Camila", "Maria");
    }

    /// <summary>An array of OBJECTS has no scalar form — Redact must still drop it wholesale rather than
    /// silently leaving the objects in place, which is what a blanket "arrays are handled" change would do.</summary>
    [Fact]
    public async Task Redacting_an_array_of_objects_still_removes_it()
    {
        var patient = await ApplyAsync("$.name", """{"mode":"redact","token":"[REDACTED]"}""");

        patient.ContainsKey("name").Should().BeFalse();
    }
}

/// <summary>
/// A MIXED array — some string elements, some not — is the shape where "handle arrays of strings" can go wrong.
/// Transforming only the string elements and returning leaves every other element untouched, and an object
/// element carries exactly the identifying text a redaction rule exists to remove. Under Redact that would also
/// be a regression: before arrays were handled at all, Redact removed the whole property.
/// </summary>
public sealed class SafeHarborMixedArrayTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();

    /// <summary>"Camila" alongside an object still spelling the name out in full.</summary>
    private const string PatientJson = """
    {
      "resourceType": "Patient",
      "id": "p-1",
      "alias": [ "Camila", { "text": "Camila Maria Lopez" }, 42 ]
    }
    """;

    private static async Task<JsonObject> ApplyAsync(string configJson)
    {
        var rule = new TransformationRule(
            TransformScope.ResourceType, TransformNodeType.HashingMasking, configJson,
            resourceType: "Patient", sourceField: "$.alias",
            executionPhase: TransformExecutionPhase.PreMapping, deIdentificationProfileId: ProfileId);

        var repository = new Mock<ITransformationRuleRepository>();
        repository
            .Setup(x => x.GetPreMappingRulesAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([rule]);

        var result = await new SafeHarborDeIdentificationService(repository.Object).DeIdentifyAsync(
            new DeIdentificationRequest("Patient", "p-1", PatientJson, [], ProfileId), CancellationToken.None);

        return (JsonObject)JsonNode.Parse(result.Json)!;
    }

    [Fact]
    public async Task Redacting_a_mixed_array_removes_the_whole_property()
    {
        var patient = await ApplyAsync("""{"mode":"redact","token":"[REDACTED]"}""");

        patient.ContainsKey("alias").Should()
            .BeFalse("a non-string element cannot hold a token, and leaving it behind ships the very text the rule removes");
        patient.ToJsonString().Should().NotContain("Camila Maria Lopez");
    }

    [Fact]
    public async Task Masking_a_mixed_array_transforms_the_strings_and_leaves_the_rest()
    {
        // Mask has no meaningful form for an object or a number — the same reason the scalar path leaves those
        // alone — and deleting data under a "mask" rule would surprise more than it protects. Pinned so the
        // limitation is a decision rather than an accident.
        var patient = await ApplyAsync("""{"mode":"mask","keepLength":4}""");

        var alias = patient["alias"]!.AsArray();
        alias[0]!.GetValue<string>().Should().Be("**mila");
        alias[1]!["text"]!.GetValue<string>().Should().Be("Camila Maria Lopez");
        alias[2]!.GetValue<int>().Should().Be(42);
    }

    [Fact]
    public async Task Hashing_a_mixed_array_transforms_the_strings_and_leaves_the_rest()
    {
        var patient = await ApplyAsync("""{"mode":"hash"}""");

        var alias = patient["alias"]!.AsArray();
        alias[0]!.GetValue<string>().Should().StartWith("anon-");
        alias[1]!["text"]!.GetValue<string>().Should().Be("Camila Maria Lopez");
    }

    [Fact]
    public async Task Removing_a_mixed_array_still_deletes_it_outright()
    {
        var patient = await ApplyAsync("""{"mode":"remove"}""");

        patient.ContainsKey("alias").Should().BeFalse();
    }
}
