using System.Text.Json;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Validation;

/// <summary>
/// Server-side mirror of the destination wizard's sqlForm/mongoForm/csvForm required fields and
/// <c>_syncDeliveryModeValidators</c>'s conditional-by-delivery-mode rules — see
/// <c>CreateDestinationConfigurationRequestValidator</c>'s XML doc for the Angular source of each rule.
/// </summary>
public sealed class CreateDestinationConfigurationRequestValidatorTests
{
    private readonly CreateDestinationConfigurationRequestValidator _sut = new();

    private static string Json(object metadata) => JsonSerializer.Serialize(metadata);

    private static CreateDestinationConfigurationRequest Request(
        DestinationType type, object? metadata) =>
        new(
            "My destination",
            type,
            "kv-vault",
            "secret-name",
            Target: null,
            InlineSecret: null,
            ConnectionMetadataJson: metadata is null ? null : Json(metadata));

    [Fact]
    public void Missing_name_fails()
    {
        var request = Request(DestinationType.SqlServer, new { dest_server = "s", dest_database = "d", dest_auth = "sql-auth" }) with
        {
            Name = "",
        };

        _sut.Validate(request).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Null_connection_metadata_skips_metadata_rules_for_update_preserve_case()
    {
        var request = Request(DestinationType.SqlServer, metadata: null);
        _sut.Validate(request).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("dest_server")]
    [InlineData("dest_database")]
    [InlineData("dest_auth")]
    public void Sql_metadata_missing_a_required_field_fails(string missingKey)
    {
        var metadata = new Dictionary<string, string>
        {
            ["dest_server"] = "srv",
            ["dest_database"] = "db",
            ["dest_auth"] = "sql-auth",
        };
        metadata.Remove(missingKey);

        var result = _sut.Validate(Request(DestinationType.SqlServer, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == missingKey);
    }

    [Fact]
    public void Sql_metadata_with_all_required_fields_passes()
    {
        var metadata = new { dest_server = "srv", dest_database = "db", dest_auth = "sql-auth" };
        _sut.Validate(Request(DestinationType.SqlServer, metadata)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Mongo_metadata_missing_collection_fails()
    {
        var result = _sut.Validate(Request(DestinationType.Mongo, new { dest_name = "Mongo" }));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_collection");
    }

    [Fact]
    public void Csv_download_mode_requires_file_pattern_only()
    {
        var metadata = new { dest_deliveryMode = "download", dest_filePattern = "{resource}.csv" };
        _sut.Validate(Request(DestinationType.Csv, metadata)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Csv_sftp_mode_missing_host_fails()
    {
        var metadata = new
        {
            dest_deliveryMode = "sftp",
            dest_filePattern = "{resource}.csv",
            dest_sftpUsername = "user",
            dest_sftpRemoteFolder = "/out",
            dest_sftpPort = "22",
        };

        var result = _sut.Validate(Request(DestinationType.Csv, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_sftpHost");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-number")]
    public void Csv_sftp_mode_port_out_of_range_fails(string port)
    {
        var metadata = new
        {
            dest_deliveryMode = "sftp",
            dest_filePattern = "{resource}.csv",
            dest_sftpHost = "host",
            dest_sftpUsername = "user",
            dest_sftpRemoteFolder = "/out",
            dest_sftpPort = port,
        };

        var result = _sut.Validate(Request(DestinationType.Csv, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_sftpPort");
    }

    [Fact]
    public void Csv_sftp_mode_with_all_required_fields_passes()
    {
        var metadata = new
        {
            dest_deliveryMode = "sftp",
            dest_filePattern = "{resource}.csv",
            dest_sftpHost = "host",
            dest_sftpUsername = "user",
            dest_sftpRemoteFolder = "/out",
            dest_sftpPort = "22",
        };

        _sut.Validate(Request(DestinationType.Csv, metadata)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Csv_email_mode_missing_recipient_fails()
    {
        var metadata = new { dest_deliveryMode = "email", dest_filePattern = "{resource}.csv" };
        var result = _sut.Validate(Request(DestinationType.Csv, metadata));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_emailTo");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10081")]
    public void Csv_download_url_mode_expiry_out_of_range_fails(string expiry)
    {
        var metadata = new
        {
            dest_deliveryMode = "downloadUrl",
            dest_filePattern = "{resource}.csv",
            dest_downloadLinkExpiryMinutes = expiry,
        };

        var result = _sut.Validate(Request(DestinationType.Csv, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_downloadLinkExpiryMinutes");
    }

    [Fact]
    public void Csv_download_url_mode_with_valid_expiry_passes()
    {
        var metadata = new
        {
            dest_deliveryMode = "downloadUrl",
            dest_filePattern = "{resource}.csv",
            dest_downloadLinkExpiryMinutes = "60",
        };

        _sut.Validate(Request(DestinationType.Csv, metadata)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Ahds_metadata_missing_fhir_service_url_fails()
    {
        var metadata = new { dest_authMode = "clientCredentials", dest_tenantId = "t", dest_clientId = "c" };
        var result = _sut.Validate(Request(DestinationType.AzureHealthDataServices, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_fhirServiceUrl");
    }

    [Theory]
    [InlineData("dest_tenantId")]
    [InlineData("dest_clientId")]
    public void Ahds_client_credentials_mode_missing_a_required_field_fails(string missingKey)
    {
        var metadata = new Dictionary<string, string>
        {
            ["dest_fhirServiceUrl"] = "https://myworkspace-myfhir.fhir.azurehealthcareapis.com",
            ["dest_authMode"] = "clientCredentials",
            ["dest_tenantId"] = "tenant-1",
            ["dest_clientId"] = "client-1",
        };
        metadata.Remove(missingKey);

        var result = _sut.Validate(Request(DestinationType.AzureHealthDataServices, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == missingKey);
    }

    [Fact]
    public void Ahds_client_credentials_mode_with_all_required_fields_passes()
    {
        var metadata = new
        {
            dest_fhirServiceUrl = "https://myworkspace-myfhir.fhir.azurehealthcareapis.com",
            dest_authMode = "clientCredentials",
            dest_tenantId = "tenant-1",
            dest_clientId = "client-1",
        };

        _sut.Validate(Request(DestinationType.AzureHealthDataServices, metadata)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Ahds_unrecognized_auth_mode_fails()
    {
        var metadata = new
        {
            dest_fhirServiceUrl = "https://myworkspace-myfhir.fhir.azurehealthcareapis.com",
            dest_authMode = "somethingElse",
        };

        var result = _sut.Validate(Request(DestinationType.AzureHealthDataServices, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_authMode");
    }

    [Fact]
    public void Ahds_managed_identity_mode_does_not_require_tenant_client_id_or_a_key_vault_secret()
    {
        var metadata = new
        {
            dest_fhirServiceUrl = "https://myworkspace-myfhir.fhir.azurehealthcareapis.com",
            dest_authMode = "managedIdentity",
        };
        var request = Request(DestinationType.AzureHealthDataServices, metadata) with
        {
            KeyVaultName = "",
            SecretName = "",
        };

        _sut.Validate(request).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Ahds_client_credentials_mode_without_a_key_vault_secret_fails()
    {
        var metadata = new
        {
            dest_fhirServiceUrl = "https://myworkspace-myfhir.fhir.azurehealthcareapis.com",
            dest_authMode = "clientCredentials",
            dest_tenantId = "tenant-1",
            dest_clientId = "client-1",
        };
        var request = Request(DestinationType.AzureHealthDataServices, metadata) with
        {
            KeyVaultName = "",
            SecretName = "",
        };

        var result = _sut.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "KeyVaultName");
        result.Errors.Should().Contain(e => e.PropertyName == "SecretName");
    }
}
