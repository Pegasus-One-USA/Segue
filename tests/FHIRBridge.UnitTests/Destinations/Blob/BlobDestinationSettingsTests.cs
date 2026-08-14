using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Blob;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations.Blob;

public sealed class BlobDestinationSettingsTests
{
    private static DestinationConfiguration Destination(string? target, string? connectionMetadataJson) =>
        new("Blob Export", DestinationType.BlobStorage, new SecretReference("kv", "secret"), target, connectionMetadataJson);

    [Theory]
    [InlineData("""{"dest_blobAuthMode":"connectionString","dest_blobContainer":"fhir"}""", BlobDestinationAuthMode.ConnectionString)]
    [InlineData("""{"dest_blobAuthMode":"accountKey","dest_blobContainer":"fhir","dest_blobAccountName":"acct"}""", BlobDestinationAuthMode.AccountKey)]
    [InlineData("""{"dest_blobAuthMode":"sasUrl","dest_blobContainer":"fhir"}""", BlobDestinationAuthMode.SasUrl)]
    [InlineData("""{"dest_blobAuthMode":"managedIdentity","dest_blobContainer":"fhir","dest_blobAccountUrl":"https://acct.blob.core.windows.net"}""", BlobDestinationAuthMode.ManagedIdentity)]
    [InlineData("""{"dest_blobAuthMode":"servicePrincipal","dest_blobContainer":"fhir","dest_blobAccountUrl":"https://acct.blob.core.windows.net","dest_blobTenantId":"t","dest_blobClientId":"c"}""", BlobDestinationAuthMode.ServicePrincipal)]
    public void Parse_reads_the_configured_auth_mode(string json, BlobDestinationAuthMode expected)
    {
        var settings = BlobDestinationSettings.Parse(Destination(null, json));

        settings.AuthMode.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"dest_blobAuthMode":"not-a-real-mode"}""")]
    public void Missing_or_unrecognized_auth_mode_defaults_to_ConnectionString(string? json)
    {
        // Target ("fhir") supplies the container so this test only ever exercises auth-mode parsing.
        var settings = BlobDestinationSettings.Parse(Destination("fhir", json));

        settings.AuthMode.Should().Be(BlobDestinationAuthMode.ConnectionString);
    }

    [Fact]
    public void Target_wins_over_dest_blobContainer_when_both_are_present()
    {
        var settings = BlobDestinationSettings.Parse(
            Destination("from-target", """{"dest_blobContainer":"from-metadata"}"""));

        settings.ContainerName.Should().Be("from-target");
    }

    [Fact]
    public void Falls_back_to_dest_blobContainer_when_Target_is_blank()
    {
        var settings = BlobDestinationSettings.Parse(Destination(null, """{"dest_blobContainer":"from-metadata"}"""));

        settings.ContainerName.Should().Be("from-metadata");
    }

    [Theory]
    [InlineData("FHIR_Export")] // uppercase/underscore — Azure rejects with InvalidResourceName
    [InlineData("ab")] // too short (min 3)
    [InlineData("-fhir")] // leading hyphen
    [InlineData("fhir-")] // trailing hyphen
    [InlineData("fhir--export")] // consecutive hyphens
    public void Invalid_Azure_container_name_throws(string container)
    {
        var act = () => BlobDestinationSettings.Parse(Destination(container, null));

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("fhir")]
    [InlineData("fhirbridge-exports")]
    [InlineData("a23")] // exactly 3 chars, minimum length
    public void Valid_Azure_container_name_passes(string container)
    {
        var settings = BlobDestinationSettings.Parse(Destination(container, null));

        settings.ContainerName.Should().Be(container);
    }

    [Fact]
    public void Missing_container_on_both_Target_and_metadata_throws()
    {
        var act = () => BlobDestinationSettings.Parse(Destination(null, "{}"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AccountUrl_is_derived_from_AccountName_and_default_endpoint_suffix()
    {
        var settings = BlobDestinationSettings.Parse(
            Destination("fhir", """{"dest_blobAuthMode":"accountKey","dest_blobAccountName":"acct"}"""));

        settings.AccountUrl.Should().Be("https://acct.blob.core.windows.net");
    }

    [Fact]
    public void AccountUrl_derivation_honors_a_custom_endpoint_suffix()
    {
        var settings = BlobDestinationSettings.Parse(Destination(
            "fhir",
            """{"dest_blobAuthMode":"accountKey","dest_blobAccountName":"acct","dest_blobEndpointSuffix":"core.usgovcloudapi.net"}"""));

        settings.AccountUrl.Should().Be("https://acct.blob.core.usgovcloudapi.net");
    }

    [Fact]
    public void Explicit_AccountUrl_overrides_the_derived_one()
    {
        var settings = BlobDestinationSettings.Parse(Destination(
            "fhir",
            """{"dest_blobAuthMode":"accountKey","dest_blobAccountName":"acct","dest_blobAccountUrl":"https://custom.example.com"}"""));

        settings.AccountUrl.Should().Be("https://custom.example.com");
    }

    [Theory]
    [InlineData("""{"dest_blobAuthMode":"connectionString"}""", true)]
    [InlineData("""{"dest_blobAuthMode":"accountKey","dest_blobAccountName":"acct"}""", true)]
    [InlineData("""{"dest_blobAuthMode":"sasUrl"}""", true)]
    [InlineData("""{"dest_blobAuthMode":"managedIdentity","dest_blobAccountUrl":"https://acct.blob.core.windows.net"}""", false)]
    [InlineData("""{"dest_blobAuthMode":"servicePrincipal","dest_blobAccountUrl":"https://acct.blob.core.windows.net","dest_blobTenantId":"t","dest_blobClientId":"c"}""", true)]
    public void RequiresSecret_is_false_only_for_ManagedIdentity(string metadataWithoutContainer, bool expectedRequiresSecret)
    {
        // Inject the always-required container field without hand-writing five near-identical JSON literals.
        var json = metadataWithoutContainer.Replace("{", """{"dest_blobContainer":"fhir",""");

        var settings = BlobDestinationSettings.Parse(Destination(null, json));

        settings.RequiresSecret.Should().Be(expectedRequiresSecret);
    }

    [Fact]
    public void AccountKey_mode_without_AccountName_throws()
    {
        var act = () => BlobDestinationSettings.Parse(
            Destination("fhir", """{"dest_blobAuthMode":"accountKey"}"""));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ManagedIdentity_mode_without_AccountUrl_or_AccountName_throws()
    {
        var act = () => BlobDestinationSettings.Parse(
            Destination("fhir", """{"dest_blobAuthMode":"managedIdentity"}"""));

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("""{"dest_blobAuthMode":"servicePrincipal","dest_blobAccountUrl":"https://acct.blob.core.windows.net"}""")]
    [InlineData("""{"dest_blobAuthMode":"servicePrincipal","dest_blobAccountUrl":"https://acct.blob.core.windows.net","dest_blobTenantId":"t"}""")]
    public void ServicePrincipal_mode_missing_tenant_or_client_id_throws(string json)
    {
        var act = () => BlobDestinationSettings.Parse(Destination("fhir", json));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CreateContainerIfNotExists_defaults_to_true()
    {
        var settings = BlobDestinationSettings.Parse(Destination("fhir", "{}"));

        settings.CreateContainerIfNotExists.Should().BeTrue();
    }

    [Fact]
    public void CreateContainerIfNotExists_can_be_disabled()
    {
        var settings = BlobDestinationSettings.Parse(
            Destination("fhir", """{"dest_blobCreateContainerIfNotExists":"false"}"""));

        settings.CreateContainerIfNotExists.Should().BeFalse();
    }

    [Fact]
    public void PathPrefix_has_surrounding_slashes_trimmed()
    {
        var settings = BlobDestinationSettings.Parse(
            Destination("fhir", """{"dest_blobPathPrefix":"/fhirbridge/patient/"}"""));

        settings.PathPrefix.Should().Be("fhirbridge/patient");
    }

    [Fact]
    public void Granularity_defaults_to_Bulk_when_missing()
    {
        var settings = BlobDestinationSettings.Parse(Destination("fhir", "{}"));

        settings.Granularity.Should().Be(BlobDeliveryGranularity.Bulk);
    }

    [Theory]
    [InlineData("individual", BlobDeliveryGranularity.Individual)]
    [InlineData("Individual", BlobDeliveryGranularity.Individual)]
    [InlineData("bulk", BlobDeliveryGranularity.Bulk)]
    [InlineData("not-a-real-value", BlobDeliveryGranularity.Bulk)]
    public void Granularity_is_parsed_case_insensitively_with_Bulk_fallback(string raw, BlobDeliveryGranularity expected)
    {
        var settings = BlobDestinationSettings.Parse(
            Destination("fhir", $$"""{"dest_blobGranularity":"{{raw}}"}"""));

        settings.Granularity.Should().Be(expected);
    }

    [Theory]
    [InlineData("append", BlobDeliveryGranularity.Bulk)]
    [InlineData("upsert", BlobDeliveryGranularity.Individual)]
    public void Granularity_falls_back_to_the_legacy_dest_writeMode_key_when_dest_blobGranularity_is_absent(
        string legacyRaw, BlobDeliveryGranularity expected)
    {
        var settings = BlobDestinationSettings.Parse(Destination("fhir", $$"""{"dest_writeMode":"{{legacyRaw}}"}"""));

        settings.Granularity.Should().Be(expected);
    }

    [Fact]
    public void RecordMode_defaults_to_Upsert_when_missing()
    {
        var settings = BlobDestinationSettings.Parse(Destination("fhir", "{}"));

        settings.RecordMode.Should().Be(BlobRecordMode.Upsert);
    }

    [Theory]
    [InlineData("insert", BlobRecordMode.Insert)]
    [InlineData("Insert", BlobRecordMode.Insert)]
    [InlineData("upsert", BlobRecordMode.Upsert)]
    [InlineData("update", BlobRecordMode.Update)]
    [InlineData("not-a-real-value", BlobRecordMode.Upsert)]
    public void RecordMode_is_parsed_case_insensitively_with_Upsert_fallback(string raw, BlobRecordMode expected)
    {
        var settings = BlobDestinationSettings.Parse(Destination("fhir", $$"""{"dest_blobRecordMode":"{{raw}}"}"""));

        settings.RecordMode.Should().Be(expected);
    }

    [Fact]
    public void FolderPattern_defaults_to_name_when_missing()
    {
        var settings = BlobDestinationSettings.Parse(Destination("fhir", "{}"));

        settings.FolderPattern.Should().Be("{name}");
    }

    [Fact]
    public void FolderPattern_reads_the_configured_value()
    {
        var settings = BlobDestinationSettings.Parse(
            Destination("fhir", """{"dest_blobFolderPattern":"{name}/{date:yyyy/MM/dd}"}"""));

        settings.FolderPattern.Should().Be("{name}/{date:yyyy/MM/dd}");
    }

    [Fact]
    public void FileNamePattern_defaults_to_a_timestamped_unique_name_for_Insert()
    {
        var settings = BlobDestinationSettings.Parse(
            Destination("fhir", """{"dest_blobRecordMode":"insert"}"""));

        settings.FileNamePattern.Should().Be("{id}_{date:yyyyMMddHHmmssfff}_{guid}.json");
    }

    [Theory]
    [InlineData("upsert")]
    [InlineData("update")]
    public void FileNamePattern_defaults_to_a_static_id_only_name_for_Upsert_and_Update(string recordMode)
    {
        var settings = BlobDestinationSettings.Parse(
            Destination("fhir", $$"""{"dest_blobRecordMode":"{{recordMode}}"}"""));

        settings.FileNamePattern.Should().Be("{id}.json");
    }

    [Fact]
    public void FileNamePattern_reads_the_configured_value_regardless_of_RecordMode()
    {
        var settings = BlobDestinationSettings.Parse(Destination(
            "fhir",
            """{"dest_blobRecordMode":"insert","dest_blobFileNamePattern":"{id}_{date:yyyyMMdd}.json"}"""));

        settings.FileNamePattern.Should().Be("{id}_{date:yyyyMMdd}.json");
    }

    [Theory]
    // The literal "\\" here is JSON's own escape for a single backslash — the parsed pattern is
    // "{name}\{date:yyyyMMdd}", containing one real backslash character, which is what the validation
    // rejects. Writing an *unescaped* backslash directly in the JSON would just make the whole
    // ConnectionMetadataJson malformed, which GetString silently swallows to null (see
    // Missing_or_unrecognized_auth_mode_defaults_to_ConnectionString above) — that would make this test
    // pass for the wrong reason, never reaching BlobDestinationSettings' own validation at all.
    [InlineData("""{"dest_blobFolderPattern":"{name}\\{date:yyyyMMdd}"}""")]
    [InlineData("""{"dest_blobFileNamePattern":"{id}\\{guid}.json"}""")]
    public void Naming_pattern_with_a_backslash_throws(string json)
    {
        var act = () => BlobDestinationSettings.Parse(Destination("fhir", json));

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("dest_blobFolderPattern", "{name}/")]
    [InlineData("dest_blobFileNamePattern", "{id}.")]
    public void Naming_pattern_ending_with_dot_or_slash_throws(string key, string pattern)
    {
        var act = () => BlobDestinationSettings.Parse(Destination("fhir", $$"""{"{{key}}":"{{pattern}}"}"""));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Naming_pattern_exceeding_the_max_length_throws()
    {
        var longPattern = new string('a', 513) + ".json";
        var act = () => BlobDestinationSettings.Parse(
            Destination("fhir", $$"""{"dest_blobFileNamePattern":"{{longPattern}}"}"""));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Naming_patterns_using_only_the_documented_tokens_pass()
    {
        var settings = BlobDestinationSettings.Parse(Destination(
            "fhir",
            """{"dest_blobFolderPattern":"{name}/{date:yyyy/MM/dd}","dest_blobFileNamePattern":"{id}_{date:yyyyMMddHHmmssfff}_{guid}.json"}"""));

        settings.FolderPattern.Should().Be("{name}/{date:yyyy/MM/dd}");
        settings.FileNamePattern.Should().Be("{id}_{date:yyyyMMddHHmmssfff}_{guid}.json");
    }
}
