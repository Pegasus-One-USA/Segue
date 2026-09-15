using System.Net;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Destinations.Auth;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// Pins the writer's byte-for-byte behavior when <c>dest_fhirAuthType</c> is absent/"none" (every FhirRepository row
/// before auth support existed), then exercises the new opt-in bearer/clientCredentials paths.
/// </summary>
public sealed class MappedFhirRepositoryDestinationWriterTests
{
    private static DestinationConfiguration Destination(string? connectionMetadataJson, string? target = "https://fhir.example.com") =>
        new("FHIR Store", DestinationType.FhirRepository, new SecretReference("kv", "secret"), target, connectionMetadataJson);

    private static MappingProfile Mapping() =>
        new("Patient FHIR", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient", []);

    private static MappedDestinationRecord Record(string? sourceJson = null, string sourceResourceId = "123", string resourceType = "Patient") =>
        new(Guid.NewGuid(), resourceType, resourceType, sourceResourceId, new Dictionary<string, object?>(), sourceJson);

    private static PipelineWriteContext Context(
        Func<string, string, CancellationToken, Task<string?>>? fetchMissingReferenceAsync = null,
        string? sourceBaseUrl = null) =>
        new(true, "Workflow", DateTimeOffset.UtcNow, FetchMissingReferenceAsync: fetchMissingReferenceAsync,
            SourceBaseUrl: sourceBaseUrl);

    private static (MappedFhirRepositoryDestinationWriter Writer, CapturingHandler Handler, Mock<ISecretProvider> SecretProvider)
        CreateWriter(
            string secretValue = "https://fhir.example.com",
            IFhirDestinationTokenProvider? tokenProvider = null,
            IAzureManagedIdentityFhirTokenProvider? managedIdentityTokenProvider = null)
    {
        var handler = new CapturingHandler();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var secretProvider = new Mock<ISecretProvider>();
        secretProvider
            .Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secretValue);

        var writer = new MappedFhirRepositoryDestinationWriter(
            secretProvider.Object,
            httpClientFactory.Object,
            tokenProvider ?? Mock.Of<IFhirDestinationTokenProvider>(),
            managedIdentityTokenProvider ?? Mock.Of<IAzureManagedIdentityFhirTokenProvider>());

        return (writer, handler, secretProvider);
    }

    [Fact]
    public async Task No_metadata_sends_no_auth_header_and_resolves_target_from_secret()
    {
        var (writer, handler, secretProvider) = CreateWriter(secretValue: "https://legacy-fhir.example.com/", tokenProvider: null);
        var destination = Destination(connectionMetadataJson: null, target: null);

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.Headers.Authorization.Should().BeNull();
        handler.LastRequest.RequestUri!.ToString().Should().StartWith("https://legacy-fhir.example.com/Patient/");
        secretProvider.Verify(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Explicit_none_auth_type_behaves_identically_to_absent_metadata()
    {
        var (writer, handler, _) = CreateWriter(secretValue: "https://legacy-fhir.example.com");
        var destination = Destination("""{"dest_fhirAuthType":"none"}""", target: null);

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.Headers.Authorization.Should().BeNull();
        handler.LastRequest.RequestUri!.ToString().Should().StartWith("https://legacy-fhir.example.com/Patient/");
    }

    [Fact]
    public async Task Target_wins_over_secret_when_both_present_and_auth_is_none()
    {
        var (writer, handler, secretProvider) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://from-target.example.com");

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString().Should().StartWith("https://from-target.example.com/Patient/");
        secretProvider.Verify(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Bearer_auth_attaches_token_from_secret_and_uses_target_as_base_url()
    {
        var (writer, handler, _) = CreateWriter(secretValue: """{"token":"my-static-token"}""");
        var destination = Destination(
            """{"dest_fhirAuthType":"bearer"}""",
            target: "https://aidbox.example.com/fhir");

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.Headers.Authorization.Should().NotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("my-static-token");
        handler.LastRequest.RequestUri!.ToString().Should().StartWith("https://aidbox.example.com/fhir/Patient/");
    }

    [Fact]
    public async Task ClientCredentials_auth_attaches_token_from_token_provider()
    {
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider
            .Setup(t => t.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("access-token-123");

        var (writer, handler, _) = CreateWriter(
            secretValue: """{"clientId":"cid","clientSecret":"csecret","tokenEndpoint":"https://aidbox.example.com/auth/token"}""",
            tokenProvider: tokenProvider.Object);
        var destination = Destination(
            """{"dest_fhirAuthType":"clientCredentials"}""",
            target: "https://aidbox.example.com/fhir");

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.Headers.Authorization!.Parameter.Should().Be("access-token-123");
        tokenProvider.Verify(
            t => t.GetAccessTokenAsync(
                It.Is<FhirDestinationOAuth2Options>(o => o.TokenEndpoint == "https://aidbox.example.com/auth/token" && o.ClientId == "cid"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ManagedIdentity_auth_attaches_token_from_managed_identity_provider_and_needs_no_secret()
    {
        var managedIdentityTokenProvider = new Mock<IAzureManagedIdentityFhirTokenProvider>();
        managedIdentityTokenProvider
            .Setup(t => t.GetAccessTokenAsync(
                "https://myfhirservice.fhir.azurehealthcareapis.com/.default", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("managed-identity-token");

        var (writer, handler, secretProvider) = CreateWriter(managedIdentityTokenProvider: managedIdentityTokenProvider.Object);
        var destination = Destination(
            """{"dest_fhirAuthType":"managedIdentity"}""",
            target: "https://myfhirservice.fhir.azurehealthcareapis.com");

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.Headers.Authorization!.Parameter.Should().Be("managed-identity-token");
        secretProvider.Verify(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AzureFhirService_destination_type_shares_this_writer_unchanged()
    {
        // AzureFhirService is registered to this same writer in ConfiguredDestinationWriterFactory (Azure Health
        // Data Services is a standard FHIR R4 server) — confirms the shared write path is unaffected by which of
        // the two DestinationType values the destination carries.
        var (writer, handler, _) = CreateWriter();
        var destination = new DestinationConfiguration(
            "AHDS", DestinationType.AzureFhirService, new SecretReference("kv", "secret"),
            "https://aidbox.example.com/fhir", connectionMetadataJson: null);

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString().Should().StartWith("https://aidbox.example.com/fhir/Patient/");
    }

    [Fact]
    public async Task Auth_enabled_without_target_throws()
    {
        var (writer, _, _) = CreateWriter(secretValue: """{"token":"abc"}""");
        var destination = Destination("""{"dest_fhirAuthType":"bearer"}""", target: null);

        var act = () => writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Source_json_is_used_verbatim_with_id_reconciled_regardless_of_auth_mode()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://fhir.example.com");
        var record = Record("""{"resourceType":"Patient","name":[{"family":"Doe"}]}""");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"resourceType\":\"Patient\"");
        handler.LastRequestBody.Should().Contain("\"id\":\"123\"");
    }

    // ── Ids that violate FHIR's id datatype constraint (some Epic-issued ids exceed 64 chars) ────────────────

    [Fact]
    public async Task An_id_longer_than_64_characters_is_replaced_with_a_deterministic_valid_id()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://fhir.example.com");
        var longId = "ecLi-J.W4eNp2GKJJpFG5GQ55b1GMgCcd1920e3y5YJz.JEjypM5tY2ECMN1ZE8jw3"; // 66 chars, real Epic-style id
        longId.Length.Should().BeGreaterThan(64);
        var record = Record($$"""{"resourceType":"Observation","id":"{{longId}}"}""", sourceResourceId: longId, resourceType: "Observation");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString().Should().NotContain(longId);
        handler.LastRequest.RequestUri!.ToString().Should().MatchRegex(@"/Observation/[0-9a-fA-F\-]{36}$");
        handler.LastRequestBody.Should().NotContain($"\"id\":\"{longId}\"");
        // Original id preserved for traceability, not silently discarded.
        handler.LastRequestBody.Should().Contain(longId);
        handler.LastRequestBody.Should().Contain("\"system\":\"urn:fhirbridge:source-id\"");
    }

    [Fact]
    public async Task The_same_invalid_id_maps_to_the_same_replacement_id_every_run()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://fhir.example.com");
        var longId = new string('a', 70);
        var record = Record($$"""{"resourceType":"Observation","id":"{{longId}}"}""", sourceResourceId: longId, resourceType: "Observation");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);
        var firstEndpoint = handler.LastRequest!.RequestUri!.ToString();

        var (writer2, handler2, _) = CreateWriter();
        await writer2.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);
        var secondEndpoint = handler2.LastRequest!.RequestUri!.ToString();

        firstEndpoint.Should().Be(secondEndpoint);
    }

    [Fact]
    public async Task An_id_within_the_64_character_limit_using_only_valid_characters_is_left_unchanged()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://fhir.example.com");
        var validId = new string('a', 64); // exactly at the limit
        var record = Record($$"""{"resourceType":"Observation","id":"{{validId}}"}""", sourceResourceId: validId, resourceType: "Observation");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString().Should().EndWith($"/Observation/{validId}");
    }

    // ── Coding.version stripping for stable HL7-core CodeSystems (avoids a destination FHIR server rejecting a
    // ── code purely because its version label doesn't match whatever CodeSystem version it has loaded) ──────

    [Fact]
    public async Task Version_is_stripped_from_a_coding_on_a_stable_catalog_CodeSystem()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var record = Record(
            """
            {"resourceType":"Condition","id":"c1","clinicalStatus":{"coding":[
                {"system":"http://terminology.hl7.org/CodeSystem/condition-clinical","version":"4.0.0","code":"active"}
            ]}}
            """,
            resourceType: "Condition");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"code\":\"active\"");
        handler.LastRequestBody.Should().NotContain("\"version\"");
    }

    [Fact]
    public async Task Version_is_stripped_from_condition_verificationStatus_and_category_confirmed_by_the_Epic_repro_sample()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var record = Record(
            """
            {"resourceType":"Condition","id":"c1",
             "verificationStatus":{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/condition-ver-status","version":"4.0.0","code":"confirmed"}]},
             "category":[{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/condition-category","version":"4.0.0","code":"encounter-diagnosis"}]}]}
            """,
            resourceType: "Condition");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().NotContain("\"version\"");
        handler.LastRequestBody.Should().Contain("\"code\":\"confirmed\"");
        handler.LastRequestBody.Should().Contain("\"code\":\"encounter-diagnosis\"");
    }

    [Fact]
    public async Task Version_is_preserved_on_encounter_status_a_CodeSystem_with_real_cross_version_code_drift()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var record = Record(
            """
            {"resourceType":"Encounter","id":"e1","status":"finished",
             "statusHistory":[{"status":"onleave","coding":[{"system":"http://hl7.org/fhir/encounter-status","version":"4.0.0","code":"onleave"}]}]}
            """,
            resourceType: "Encounter");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"version\":\"4.0.0\"");
    }

    [Fact]
    public async Task Version_is_stripped_from_allergyintolerance_verification_an_additive_only_R5_change()
    {
        // R5 adds a 5th code ("presumed") but removes nothing, so a source that only emits R4-era codes (Epic) can
        // never be invalidated by this change — this is the real-world shape that failed: Epic tagged "4.0.0" while
        // the destination had "4.0.1" loaded, a benign within-R4 version bump on an identical code set.
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var record = Record(
            """
            {"resourceType":"AllergyIntolerance","id":"a1","verificationStatus":{"coding":[
                {"system":"http://terminology.hl7.org/CodeSystem/allergyintolerance-verification","version":"4.0.0","code":"confirmed"}
            ]}}
            """,
            resourceType: "AllergyIntolerance");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"code\":\"confirmed\"");
        handler.LastRequestBody.Should().NotContain("\"version\"");
    }

    [Fact]
    public async Task Version_is_stripped_from_composition_status_whose_R5_hierarchy_kept_all_original_codes()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var record = Record(
            """
            {"resourceType":"DocumentReference","id":"d1","docStatus":"final","category":[{"coding":[
                {"system":"http://hl7.org/fhir/composition-status","version":"4.0.1","code":"final"}
            ]}]}
            """,
            resourceType: "DocumentReference");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"code\":\"final\"");
        handler.LastRequestBody.Should().NotContain("\"version\"");
    }

    [Fact]
    public async Task Version_is_preserved_on_a_non_catalog_CodeSystem_such_as_SNOMED_CT()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var record = Record(
            """{"resourceType":"Condition","id":"c1","code":{"coding":[{"system":"http://snomed.info/sct","version":"http://snomed.info/sct/731000124108","code":"38341003"}]}}""",
            resourceType: "Condition");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"version\":\"http://snomed.info/sct/731000124108\"");
    }

    [Fact]
    public async Task Resource_with_no_coding_anywhere_is_unaffected_by_version_stripping()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var record = Record("""{"resourceType":"Patient","id":"p1","name":[{"family":"Doe"}]}""");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"family\":\"Doe\"");
    }

    // ── dest_fhirWriteMode: "bundle" ─────────────────────────────────────────

    [Fact]
    public async Task Bundle_mode_sends_one_POST_to_root_with_a_batch_Bundle_body()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(
            """{"dest_fhirAuthType":"none","dest_fhirWriteMode":"bundle"}""",
            target: "https://aidbox.example.com/fhir");
        var record = Record("""{"resourceType":"Patient","id":"123"}""");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        var (request, body) = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://aidbox.example.com/fhir");
        body.Should().Contain("\"resourceType\":\"Bundle\"");
        body.Should().Contain("\"type\":\"batch\"");
        body.Should().Contain("\"method\":\"PUT\"");
        body.Should().Contain("\"url\":\"Patient/123\"");
    }

    [Fact]
    public async Task Bundle_mode_isolates_one_failed_entry_from_the_other_two()
    {
        var (writer, handler, _) = CreateWriter();
        handler.RespondWith = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "resourceType": "Bundle",
                  "type": "batch-response",
                  "entry": [
                    {"response": {"status": "200 OK"}},
                    {"response": {"status": "422 Unprocessable Entity", "outcome": {"resourceType":"OperationOutcome","issue":[{"severity":"fatal","code":"invalid","diagnostics":"Referenced resource Organization/xyz does not exist"}]}}},
                    {"response": {"status": "200 OK"}}
                  ]
                }
                """)
        };
        var destination = Destination("""{"dest_fhirWriteMode":"bundle"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Patient","id":"p1"}""", sourceResourceId: "p1"),
            Record("""{"resourceType":"Patient","id":"p2"}""", sourceResourceId: "p2"),
            Record("""{"resourceType":"Patient","id":"p3"}""", sourceResourceId: "p3"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(2);
        result.WrittenResourceIds.Should().BeEquivalentTo(new[] { "p1", "p3" });
        result.RecordErrors.Should().ContainSingle();
        result.RecordErrors!.Single().Should().Be("Patient/p2: Referenced resource Organization/xyz does not exist");
    }

    [Fact]
    public async Task Bundle_mode_matches_response_entries_to_records_by_position_not_by_content()
    {
        var (writer, handler, _) = CreateWriter();
        handler.RespondWith = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Bare status entries with no resource/id info at all — proves matching is purely positional.
            Content = new StringContent("""
                {"resourceType":"Bundle","type":"batch-response","entry":[
                    {"response":{"status":"200 OK"}},
                    {"response":{"status":"200 OK"}}
                ]}
                """)
        };
        var destination = Destination("""{"dest_fhirWriteMode":"bundle"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Patient","id":"p1"}""", sourceResourceId: "p1"),
            Record("""{"resourceType":"Patient","id":"p2"}""", sourceResourceId: "p2"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        result.WrittenResourceIds.Should().Equal("p1", "p2");
    }

    [Fact]
    public async Task Bundle_mode_writes_a_non_FHIR_fallback_record_individually_alongside_the_bundle()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_fhirWriteMode":"bundle"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Patient","id":"p1"}""", sourceResourceId: "p1"),
            Record(sourceJson: null, sourceResourceId: "p2"), // no SourceJson -> non-FHIR fallback path
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Body.Should().Contain("\"resourceType\":\"Bundle\"");
        handler.Requests[1].Request.Method.Should().Be(HttpMethod.Put);
        result.Count.Should().Be(2);
    }

    [Fact]
    public async Task Bundle_mode_throws_when_response_entry_count_does_not_match_request()
    {
        var (writer, handler, _) = CreateWriter();
        handler.RespondWith = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"resourceType":"Bundle","type":"batch-response","entry":[{"response":{"status":"200 OK"}}]}""")
        };
        var destination = Destination("""{"dest_fhirWriteMode":"bundle"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Patient","id":"p1"}""", sourceResourceId: "p1"),
            Record("""{"resourceType":"Patient","id":"p2"}""", sourceResourceId: "p2"),
        };

        var act = () => writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── write ordering (Organization/Practitioner before a Patient referencing them) ────────────────────────

    [Fact]
    public async Task Individual_mode_writes_a_referenced_resource_type_before_the_resource_that_references_it()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        // Deliberately out of dependency order: Patient (references Organization) constructed first.
        var records = new[]
        {
            Record(
                """{"resourceType":"Patient","id":"p1","managingOrganization":{"reference":"Organization/org1"}}""",
                sourceResourceId: "p1", resourceType: "Patient"),
            Record("""{"resourceType":"Organization","id":"org1"}""", sourceResourceId: "org1", resourceType: "Organization"),
        };

        await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Request.RequestUri!.ToString().Should().Be("https://aidbox.example.com/fhir/Organization/org1");
        handler.Requests[1].Request.RequestUri!.ToString().Should().Be("https://aidbox.example.com/fhir/Patient/p1");
    }

    [Fact]
    public async Task Bundle_mode_orders_bundle_entries_so_the_referenced_type_comes_first()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_fhirWriteMode":"bundle"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record(
                """{"resourceType":"Patient","id":"p1","generalPractitioner":[{"reference":"Practitioner/pr1"}]}""",
                sourceResourceId: "p1", resourceType: "Patient"),
            Record("""{"resourceType":"Practitioner","id":"pr1"}""", sourceResourceId: "pr1", resourceType: "Practitioner"),
        };

        await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        var entryUrls = System.Text.Json.Nodes.JsonNode.Parse(handler.Requests[0].Body!)!["entry"]!.AsArray()
            .Select(e => e!["request"]!["url"]!.GetValue<string>())
            .ToList();
        entryUrls.Should().Equal("Practitioner/pr1", "Patient/p1");
    }

    [Fact]
    public async Task Bundle_mode_orders_same_type_reference_before_the_record_referencing_it()
    {
        // Reproduces Epic's multi-component vital-sign panel shape: two granular Observations both reference a
        // third, parent Observation via derivedFrom — a same-resource-type dependency the old group-level sort
        // (which only ordered across DIFFERENT resource types) couldn't see, so the parent (fetched last here,
        // matching the real-world batch order that caused this) needs to move before both children.
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_fhirWriteMode":"bundle"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record(
                """{"resourceType":"Observation","id":"child1","derivedFrom":[{"reference":"Observation/parent1"}]}""",
                sourceResourceId: "child1", resourceType: "Observation"),
            Record(
                """{"resourceType":"Observation","id":"child2","derivedFrom":[{"reference":"Observation/parent1"}]}""",
                sourceResourceId: "child2", resourceType: "Observation"),
            Record("""{"resourceType":"Observation","id":"parent1"}""", sourceResourceId: "parent1", resourceType: "Observation"),
        };

        await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        var entryUrls = System.Text.Json.Nodes.JsonNode.Parse(handler.Requests[0].Body!)!["entry"]!.AsArray()
            .Select(e => e!["request"]!["url"]!.GetValue<string>())
            .ToList();
        entryUrls.Should().Equal("Observation/parent1", "Observation/child1", "Observation/child2");
    }

    // ── transaction mode (atomic; resolves mutual/cyclic references batch mode can't) ──────────────────────

    [Fact]
    public async Task Transaction_mode_sends_type_transaction_and_marks_all_entries_written_on_success()
    {
        // The actual failing shape this mode exists for: two Observations that MUTUALLY reference each other
        // (hasMember <-> derivedFrom) — no write order could ever satisfy both in "bundle" mode, but a transaction
        // Bundle resolves references against the full set being written, not just what's committed so far.
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_fhirWriteMode":"transaction"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record(
                """{"resourceType":"Observation","id":"parent1","hasMember":[{"reference":"Observation/child1"}]}""",
                sourceResourceId: "parent1", resourceType: "Observation"),
            Record(
                """{"resourceType":"Observation","id":"child1","derivedFrom":[{"reference":"Observation/parent1"}]}""",
                sourceResourceId: "child1", resourceType: "Observation"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        var requestBody = System.Text.Json.Nodes.JsonNode.Parse(handler.Requests[0].Body!)!;
        requestBody["type"]!.GetValue<string>().Should().Be("transaction");
        result.Count.Should().Be(2);
        result.RecordErrors.Should().BeNull();
    }

    [Fact]
    public async Task Transaction_mode_fails_the_whole_write_when_the_outer_response_is_non_success()
    {
        var (writer, handler, _) = CreateWriter();
        handler.RespondWith = (_, _) => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent(
                """
                {"resourceType":"OperationOutcome","issue":[{"severity":"fatal","code":"invalid",
                "diagnostics":"Referenced resource Observation/child1 does not exist"}]}
                """)
        };
        var destination = Destination("""{"dest_fhirWriteMode":"transaction"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record(
                """{"resourceType":"Observation","id":"parent1","hasMember":[{"reference":"Observation/child1"}]}""",
                sourceResourceId: "parent1", resourceType: "Observation"),
            Record(
                """{"resourceType":"Observation","id":"child1","derivedFrom":[{"reference":"Observation/parent1"}]}""",
                sourceResourceId: "child1", resourceType: "Observation"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        result.RecordErrors.Should().ContainSingle(e =>
            e.Contains("Observation/parent1") && e.Contains("Observation/child1")
            && e.Contains("Referenced resource Observation/child1 does not exist"));
    }

    [Fact]
    public async Task Order_is_unchanged_when_the_referenced_resource_is_confirmed_present_at_the_destination()
    {
        var (writer, handler, _) = CreateWriter();
        // References Organization, but no Organization record is present in this batch at all — the destination's
        // own existence check confirms it's already there, so Patient must still be written, unaffected.
        handler.RespondWith = (request, _) => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"resourceType":"Bundle","type":"searchset","entry":[{"resource":{"resourceType":"Organization","id":"org-elsewhere"}}]}""")
            }
            : new HttpResponseMessage(HttpStatusCode.OK);
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record(
                """{"resourceType":"Patient","id":"p1","managingOrganization":{"reference":"Organization/org-elsewhere"}}""",
                sourceResourceId: "p1", resourceType: "Patient"),
        };

        await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().ContainSingle(r => r.Request.Method == HttpMethod.Put
            && r.Request.RequestUri!.ToString() == "https://aidbox.example.com/fhir/Patient/p1");
    }

    [Fact]
    public async Task A_non_FHIR_fallback_record_is_unaffected_by_reference_ordering()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Patient","id":"p1","managingOrganization":{"reference":"Organization/org1"}}""",
                sourceResourceId: "p1", resourceType: "Patient"),
            Record(sourceJson: null, sourceResourceId: "fallback1", resourceType: "Custom"),
            Record("""{"resourceType":"Organization","id":"org1"}""", sourceResourceId: "org1", resourceType: "Organization"),
        };

        await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Request.RequestUri!.ToString().Should().Be("https://aidbox.example.com/fhir/Organization/org1");
        handler.Requests[1].Request.RequestUri!.ToString().Should().Be("https://aidbox.example.com/fhir/Patient/p1");
        handler.Requests[2].Request.RequestUri!.ToString().Should().Contain("Custom/fallback1");
    }

    // ── referenced-but-absent-type warning (defense-in-depth for a deliberate destination-resource exclusion) ──

    [Fact]
    public async Task Individual_mode_blocks_a_record_whose_reference_is_confirmed_missing_everywhere()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        // Encounter is deliberately excluded from this batch entirely, and the (default, empty) existence-check
        // response confirms it doesn't already exist at the destination either — same shape as a destination whose
        // own resource selection dropped it while keeping Observation.
        var records = new[]
        {
            Record("""{"resourceType":"Observation","id":"o1","encounter":{"reference":"Encounter/e1"}}""",
                sourceResourceId: "o1", resourceType: "Observation"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Put);
        result.RecordErrors.Should().ContainSingle(e =>
            e.Contains("Observation/o1") && e.Contains("Encounter/e1") && e.Contains("not found"));
    }

    [Fact]
    public async Task No_warning_when_every_referenced_type_is_present_in_the_batch()
    {
        var (writer, _, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Observation","id":"o1","encounter":{"reference":"Encounter/e1"}}""",
                sourceResourceId: "o1", resourceType: "Observation"),
            Record("""{"resourceType":"Encounter","id":"e1"}""", sourceResourceId: "e1", resourceType: "Encounter"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        result.RecordErrors.Should().BeNull();
    }

    [Fact]
    public async Task Bundle_mode_blocks_a_record_whose_reference_is_confirmed_missing_everywhere()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_fhirWriteMode":"bundle"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Observation","id":"o1","encounter":{"reference":"Encounter/e1"}}""",
                sourceResourceId: "o1", resourceType: "Observation"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Post);
        result.RecordErrors.Should().ContainSingle(e => e.Contains("Observation/o1") && e.Contains("Encounter/e1"));
    }

    // ── reference resolution: present in batch / present at destination / missing everywhere ──────────────

    [Fact]
    public async Task Referenced_resource_present_in_the_current_batch_needs_no_destination_lookup()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org1"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
            Record("""{"resourceType":"Organization","id":"org1"}""", sourceResourceId: "org1", resourceType: "Organization"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Get);
        handler.Requests.Count(r => r.Request.Method == HttpMethod.Put).Should().Be(2);
        result.RecordErrors.Should().BeNull();
    }

    [Fact]
    public async Task Referenced_resource_absent_from_batch_but_found_at_destination_is_satisfied()
    {
        var (writer, handler, _) = CreateWriter();
        handler.RespondWith = (request, _) => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"resourceType":"Bundle","type":"searchset","entry":[{"resource":{"resourceType":"Organization","id":"org1"}}]}""")
            }
            : new HttpResponseMessage(HttpStatusCode.OK);
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org1"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        result.RecordErrors.Should().BeNull();
        handler.Requests.Should().ContainSingle(r => r.Request.Method == HttpMethod.Get);
        handler.Requests.Single(r => r.Request.Method == HttpMethod.Get).Request.RequestUri!.ToString()
            .Should().Contain("Organization?_id=org1");
        handler.Requests.Should().ContainSingle(r =>
            r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.ToString().Contains("Encounter/enc1"));
        handler.Requests.Should().NotContain(r =>
            r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.ToString().Contains("Organization"));
    }

    [Fact]
    public async Task Referenced_resource_missing_everywhere_blocks_only_the_referencing_record()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org1"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Put);
        result.RecordErrors.Should().ContainSingle(e =>
            e.Contains("Encounter/enc1") && e.Contains("Organization/org1") && e.Contains("not found"));
    }

    [Fact]
    public async Task Only_the_record_with_a_genuinely_missing_reference_is_blocked_the_other_still_writes()
    {
        var (writer, handler, _) = CreateWriter();
        handler.RespondWith = (request, _) =>
        {
            if (request.Method != HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            // Only org-found is ever returned — org-missing genuinely doesn't exist at the destination.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"resourceType":"Bundle","type":"searchset","entry":[{"resource":{"resourceType":"Organization","id":"org-found"}}]}""")
            };
        };
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org-missing"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
            Record("""{"resourceType":"Encounter","id":"enc2","serviceProvider":{"reference":"Organization/org-found"}}""",
                sourceResourceId: "enc2", resourceType: "Encounter"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().NotContain(r =>
            r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.ToString().Contains("enc1"));
        handler.Requests.Should().ContainSingle(r =>
            r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.ToString().Contains("enc2"));
        result.RecordErrors.Should().ContainSingle(e => e.Contains("Encounter/enc1") && e.Contains("org-missing"));
    }

    [Fact]
    public async Task Existence_check_failure_falls_back_to_a_non_blocking_warning_and_still_writes_the_record()
    {
        var (writer, handler, _) = CreateWriter();
        handler.RespondWith = (request, _) => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK);
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org1"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().ContainSingle(r => r.Request.Method == HttpMethod.Put);
        result.RecordErrors.Should().ContainSingle(e =>
            e.Contains("could not verify") && e.Contains("Organization/org1"));
    }

    [Fact]
    public async Task Duplicate_references_to_the_same_missing_resource_are_looked_up_once_and_block_every_referencing_record()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org-abc"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
            Record("""{"resourceType":"Encounter","id":"enc2","serviceProvider":{"reference":"Organization/org-abc"}}""",
                sourceResourceId: "enc2", resourceType: "Encounter"),
            Record("""{"resourceType":"Encounter","id":"enc3","serviceProvider":{"reference":"Organization/org-xyz"}}""",
                sourceResourceId: "enc3", resourceType: "Encounter"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Count(r => r.Request.Method == HttpMethod.Get).Should().Be(1);
        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Put);
        result.RecordErrors.Should().HaveCount(3);
        result.RecordErrors.Should().Contain(e => e.Contains("Encounter/enc1") && e.Contains("org-abc"));
        result.RecordErrors.Should().Contain(e => e.Contains("Encounter/enc2") && e.Contains("org-abc"));
        result.RecordErrors.Should().Contain(e => e.Contains("Encounter/enc3") && e.Contains("org-xyz"));
    }

    [Fact]
    public async Task Contained_resource_fragment_references_are_never_existence_checked()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record(
                """
                {"resourceType":"Encounter","id":"enc1","contained":[{"resourceType":"Practitioner","id":"comp1"}],
                 "participant":[{"individual":{"reference":"#comp1"}}]}
                """,
                sourceResourceId: "enc1", resourceType: "Encounter"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Get);
        handler.Requests.Should().ContainSingle(r => r.Request.Method == HttpMethod.Put);
        result.RecordErrors.Should().BeNull();
    }

    [Fact]
    public async Task Versioned_reference_matches_a_present_unversioned_record_by_type_and_id()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record(
                """{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org1/_history/3"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
            Record("""{"resourceType":"Organization","id":"org1"}""", sourceResourceId: "org1", resourceType: "Organization"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Get);
        result.RecordErrors.Should().BeNull();
    }

    [Fact]
    public async Task Absolute_url_reference_is_ignored_by_the_existence_check()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record(
                """{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"https://external.example.com/fhir/Organization/org1"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Get);
        handler.Requests.Should().ContainSingle(r => r.Request.Method == HttpMethod.Put);
        result.RecordErrors.Should().BeNull();
    }

    [Fact]
    public async Task Identifier_based_reference_with_no_reference_field_is_not_resolved_by_this_mechanism()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record(
                """{"resourceType":"Patient","id":"p1","generalPractitioner":[{"identifier":{"system":"urn:npi","value":"123"}}]}""",
                sourceResourceId: "p1", resourceType: "Patient"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Get);
        result.RecordErrors.Should().BeNull();
    }

    // ── dest_autoFetchMissingReferences (opt-in) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Auto_fetch_disabled_by_default_leaves_a_confirmed_missing_reference_blocked()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org1"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
        };
        var fetchCalled = false;
        var context = Context((_, _, _) => { fetchCalled = true; return Task.FromResult<string?>("""{"resourceType":"Organization","id":"org1"}"""); });

        var result = await writer.WriteAsync(destination, Mapping(), records, context, CancellationToken.None);

        fetchCalled.Should().BeFalse("the fetch hook must never be invoked unless dest_autoFetchMissingReferences is explicitly enabled");
        result.Count.Should().Be(0);
        result.RecordErrors.Should().ContainSingle(e => e.Contains("not found"));
    }

    [Fact]
    public async Task Auto_fetch_enabled_includes_a_successfully_fetched_reference_in_the_same_write()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_autoFetchMissingReferences":"true"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org1"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
        };
        var context = Context((type, id, _) =>
            Task.FromResult<string?>(type == "Organization" && id == "org1"
                ? """{"resourceType":"Organization","id":"org1","name":"Fetched Org"}"""
                : null));

        var result = await writer.WriteAsync(destination, Mapping(), records, context, CancellationToken.None);

        result.RecordErrors.Should().BeNull();
        handler.Requests.Should().ContainSingle(r => r.Request.Method == HttpMethod.Put
            && r.Request.RequestUri!.ToString().Contains("Organization/org1"));
        handler.Requests.Should().ContainSingle(r => r.Request.Method == HttpMethod.Put
            && r.Request.RequestUri!.ToString().Contains("Encounter/enc1"));
        // Organization must be written before Encounter — same reference-dependency ordering as an in-batch reference.
        var organizationIndex = handler.Requests.FindIndex(r => r.Request.RequestUri!.ToString().Contains("Organization/org1"));
        var encounterIndex = handler.Requests.FindIndex(r => r.Request.RequestUri!.ToString().Contains("Encounter/enc1"));
        organizationIndex.Should().BeLessThan(encounterIndex);
    }

    [Fact]
    public async Task Auto_fetch_failure_falls_back_to_blocking_the_record_exactly_as_if_auto_fetch_were_off()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_autoFetchMissingReferences":"true"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org1"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
        };
        var context = Context((_, _, _) => Task.FromResult<string?>(null));

        var result = await writer.WriteAsync(destination, Mapping(), records, context, CancellationToken.None);

        result.Count.Should().Be(0);
        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Put);
        result.RecordErrors.Should().ContainSingle(e => e.Contains("Encounter/enc1") && e.Contains("Organization/org1") && e.Contains("not found"));
    }

    [Fact]
    public async Task Auto_fetch_with_no_delegate_wired_up_falls_back_to_blocking()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_autoFetchMissingReferences":"true"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org1"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
        };

        // No fetchMissingReferenceAsync supplied — e.g. more than one source node feeds this destination.
        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        result.RecordErrors.Should().ContainSingle(e => e.Contains("not found"));
    }

    [Fact]
    public async Task Auto_fetch_cap_blocks_the_excess_and_reports_a_single_summary_line()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(
            """{"dest_autoFetchMissingReferences":"true","dest_autoFetchMaxCount":"1"}""",
            target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Encounter","id":"enc1","serviceProvider":{"reference":"Organization/org-a"}}""",
                sourceResourceId: "enc1", resourceType: "Encounter"),
            Record("""{"resourceType":"Encounter","id":"enc2","serviceProvider":{"reference":"Organization/org-b"}}""",
                sourceResourceId: "enc2", resourceType: "Encounter"),
        };
        var fetchedIds = new List<string>();
        var context = Context((type, id, _) =>
        {
            fetchedIds.Add(id);
            return Task.FromResult<string?>($$"""{"resourceType":"{{type}}","id":"{{id}}"}""");
        });

        var result = await writer.WriteAsync(destination, Mapping(), records, context, CancellationToken.None);

        fetchedIds.Should().HaveCount(1, "the cap of 1 must stop further auto-fetch attempts");
        result.RecordErrors.Should().ContainSingle(e => e.Contains("Auto-fetch limit") && e.Contains("1"));
        handler.Requests.Count(r => r.Request.Method == HttpMethod.Put).Should().Be(2); // one Encounter + its fetched Organization
    }

    [Fact]
    public async Task Auto_fetch_recursively_resolves_a_reference_inside_an_auto_fetched_record()
    {
        // Reproduces the real gap: an Observation references a missing Encounter; the fetched Encounter itself
        // references a missing Practitioner. Auto-fetch must resolve BOTH levels, not just the first.
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_autoFetchMissingReferences":"true"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Observation","id":"obs1","encounter":{"reference":"Encounter/enc1"}}""",
                sourceResourceId: "obs1", resourceType: "Observation"),
        };
        var fetchedIds = new List<string>();
        var context = Context((type, id, _) =>
        {
            fetchedIds.Add(id);
            if (type == "Encounter" && id == "enc1")
            {
                return Task.FromResult<string?>(
                    """{"resourceType":"Encounter","id":"enc1","participant":[{"individual":{"reference":"Practitioner/pr1"}}]}""");
            }
            if (type == "Practitioner" && id == "pr1")
            {
                return Task.FromResult<string?>("""{"resourceType":"Practitioner","id":"pr1"}""");
            }
            return Task.FromResult<string?>(null);
        });

        var result = await writer.WriteAsync(destination, Mapping(), records, context, CancellationToken.None);

        fetchedIds.Should().Equal("enc1", "pr1"); // Encounter discovered from the Observation first, Practitioner discovered from the fetched Encounter second
        result.RecordErrors.Should().BeNull();
        result.Count.Should().Be(3);
        handler.Requests.Should().Contain(r => r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.ToString().Contains("Observation/obs1"));
        handler.Requests.Should().Contain(r => r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.ToString().Contains("Encounter/enc1"));
        handler.Requests.Should().Contain(r => r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.ToString().Contains("Practitioner/pr1"));
    }

    [Fact]
    public async Task Auto_fetch_recursion_finds_the_nested_reference_already_at_the_destination()
    {
        // Same shape as above, but the nested Practitioner already exists at the destination — the existence check
        // must satisfy it without a second fetch call.
        var (writer, handler, _) = CreateWriter();
        handler.RespondWith = (request, _) => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"resourceType":"Bundle","type":"searchset","entry":[{"resource":{"resourceType":"Practitioner","id":"pr1"}}]}""")
            }
            : new HttpResponseMessage(HttpStatusCode.OK);
        var destination = Destination("""{"dest_autoFetchMissingReferences":"true"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Observation","id":"obs1","encounter":{"reference":"Encounter/enc1"}}""",
                sourceResourceId: "obs1", resourceType: "Observation"),
        };
        var fetchedIds = new List<string>();
        var context = Context((type, id, _) =>
        {
            fetchedIds.Add(id);
            return Task.FromResult<string?>(
                """{"resourceType":"Encounter","id":"enc1","participant":[{"individual":{"reference":"Practitioner/pr1"}}]}""");
        });

        var result = await writer.WriteAsync(destination, Mapping(), records, context, CancellationToken.None);

        fetchedIds.Should().Equal("enc1"); // Practitioner satisfied by the existence check — never fetched
        result.RecordErrors.Should().BeNull();
        handler.Requests.Should().Contain(r => r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.ToString().Contains("Observation/obs1"));
        handler.Requests.Should().Contain(r => r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.ToString().Contains("Encounter/enc1"));
        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.ToString().Contains("Practitioner"));
    }

    [Fact]
    public async Task Unresolvable_nested_reference_cascades_and_excludes_the_referencing_record_too()
    {
        // The actual failure this was built for: Observation -> fetched Encounter -> Practitioner that can't be
        // resolved at all. Both the Encounter AND the Observation that pulled it in must be excluded — writing the
        // Encounter alone would just reproduce the dangling-reference bug one level removed.
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_autoFetchMissingReferences":"true"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Observation","id":"obs1","encounter":{"reference":"Encounter/enc1"}}""",
                sourceResourceId: "obs1", resourceType: "Observation"),
        };
        var context = Context((type, id, _) =>
        {
            if (type == "Encounter" && id == "enc1")
            {
                return Task.FromResult<string?>(
                    """{"resourceType":"Encounter","id":"enc1","participant":[{"individual":{"reference":"Practitioner/pr1"}}]}""");
            }
            return Task.FromResult<string?>(null); // Practitioner/pr1 fetch fails, and the default handler finds nothing at the destination either
        });

        var result = await writer.WriteAsync(destination, Mapping(), records, context, CancellationToken.None);

        result.Count.Should().Be(0);
        handler.Requests.Should().NotContain(r => r.Request.Method == HttpMethod.Put);
        result.RecordErrors.Should().HaveCount(2);
        result.RecordErrors.Should().ContainSingle(e => e.Contains("Encounter/enc1") && e.Contains("Practitioner/pr1"));
        result.RecordErrors.Should().ContainSingle(e => e.Contains("Observation/obs1") && e.Contains("Encounter/enc1"));
    }

    /// <summary>
    /// Captures every request sent (not just the last one) so bundle-mode tests can assert on a whole call's worth
    /// of HTTP traffic; <see cref="LastRequest"/>/<see cref="LastRequestBody"/> stay derived from the same list so
    /// every pre-existing single-request test keeps working unmodified.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

        public HttpRequestMessage? LastRequest => Requests.Count > 0 ? Requests[^1].Request : null;
        public string? LastRequestBody => Requests.Count > 0 ? Requests[^1].Body : null;

        /// <summary>Test-supplied response builder; when null, defaults to a bare 200 OK for a plain PUT, or (for a
        /// Bundle POST) an all-succeeded batch-response Bundle with one "200 OK" entry per request entry.</summary>
        public Func<HttpRequestMessage, string?, HttpResponseMessage>? RespondWith { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));

            if (RespondWith is not null)
            {
                return RespondWith(request, body);
            }

            if (body is not null && body.Contains("\"resourceType\":\"Bundle\"", StringComparison.Ordinal))
            {
                var requestEntryCount = System.Text.Json.Nodes.JsonNode.Parse(body)!["entry"]!.AsArray().Count;
                var responseEntries = string.Join(
                    ",", Enumerable.Repeat("""{"response":{"status":"200 OK"}}""", requestEntryCount));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"resourceType":"Bundle","type":"batch-response","entry":[{{responseEntries}}]}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // ---- Absolute (source-qualified) references -------------------------------------------------------------
    // Epic returns fully-qualified references into its own base URL (Observation.focus/hasMember/derivedFrom).
    // These were previously skipped as "external", so they were never existence-checked, auto-fetched, or
    // excluded — the referencing record went to the destination carrying a guaranteed dangling reference and came
    // back as an opaque 422 "Referenced resource ... does not exist".

    private const string EpicBase = "https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4";

    private static MappedDestinationRecord ObservationWithFocus(string id, string focusReference) =>
        new(Guid.NewGuid(), "Observation", "Observation", id, new Dictionary<string, object?>(),
            $$"""{"resourceType":"Observation","id":"{{id}}","focus":[{"reference":"{{focusReference}}"}]}""");

    [Fact]
    public async Task Absolute_reference_into_source_base_url_is_auto_fetched_instead_of_written_dangling()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_fhirAuthType":"none","dest_autoFetchMissingReferences":"true"}""");
        var record = ObservationWithFocus("obs-1", $"{EpicBase}/Observation/obs-target");

        var fetched = new List<string>();
        Task<string?> Fetch(string type, string id, CancellationToken ct)
        {
            fetched.Add($"{type}/{id}");
            return Task.FromResult<string?>($$"""{"resourceType":"Observation","id":"{{id}}"}""");
        }

        var result = await writer.WriteAsync(
            destination, Mapping(), [record], Context(Fetch, EpicBase), CancellationToken.None);

        // The absolute reference is recognized as the source-relative Observation/obs-target and fetched.
        fetched.Should().Contain("Observation/obs-target");
        // Both the referencing record and the fetched target are written; nothing is excluded.
        result.RecordErrors.Should().BeNullOrEmpty();
        handler.Requests.Select(r => r.Request.RequestUri!.AbsolutePath)
            .Should().Contain(p => p.EndsWith("/Observation/obs-target", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Absolute_reference_that_cannot_be_fetched_excludes_the_record_instead_of_writing_it()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_fhirAuthType":"none","dest_autoFetchMissingReferences":"true"}""");
        var record = ObservationWithFocus("obs-1", $"{EpicBase}/Observation/gone");

        // Source has no such resource — exactly the case that previously produced Aidbox's 422.
        var result = await writer.WriteAsync(
            destination, Mapping(), [record],
            Context((_, _, _) => Task.FromResult<string?>(null), EpicBase), CancellationToken.None);

        result.RecordErrors.Should().NotBeNullOrEmpty();
        string.Join(" ", result.RecordErrors!).Should().Contain("Observation/gone");
        // The existence-check GET is expected (it proves the reference is now checked at all); what must NOT
        // happen is the PUT that previously came back as Aidbox's 422.
        handler.Requests.Should().NotContain(
            r => r.Request.Method == HttpMethod.Put,
            "the record with an unresolvable reference must never be written");
    }

    [Fact]
    public async Task Absolute_reference_to_a_different_host_is_still_treated_as_external_and_ignored()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_fhirAuthType":"none","dest_autoFetchMissingReferences":"true"}""");
        var record = ObservationWithFocus("obs-1", "https://other-hospital.org/fhir/Observation/elsewhere");

        var fetchCalled = false;
        var result = await writer.WriteAsync(
            destination, Mapping(), [record],
            Context((_, _, _) => { fetchCalled = true; return Task.FromResult<string?>(null); }, EpicBase),
            CancellationToken.None);

        fetchCalled.Should().BeFalse("a reference to a different server is not this source's to resolve");
        result.RecordErrors.Should().BeNullOrEmpty();
        handler.Requests.Should().ContainSingle("the record still writes, exactly as before this change");
    }

    [Fact]
    public async Task Absolute_reference_is_ignored_when_no_source_base_url_is_known()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_fhirAuthType":"none","dest_autoFetchMissingReferences":"true"}""");
        var record = ObservationWithFocus("obs-1", $"{EpicBase}/Observation/obs-target");

        // No SourceBaseUrl (ambiguous/unknown source) — behavior is unchanged from before this change.
        var result = await writer.WriteAsync(
            destination, Mapping(), [record],
            Context((_, _, _) => Task.FromResult<string?>(null)), CancellationToken.None);

        result.RecordErrors.Should().BeNullOrEmpty();
        handler.Requests.Should().ContainSingle();
    }
}
