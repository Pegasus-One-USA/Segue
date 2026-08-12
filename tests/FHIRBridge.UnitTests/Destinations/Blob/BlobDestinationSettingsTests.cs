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
}
