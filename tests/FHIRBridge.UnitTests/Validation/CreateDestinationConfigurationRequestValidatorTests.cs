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

    // Mongo has no required connection metadata of its own. The connection string is the whole credential and
    // lives in the encrypted secret, not here; the collection is chosen per resource on the mapping canvas
    // (the build sends target: null for Mongo so those per-resource choices win), and a collection that
    // doesn't exist yet is created on the first write — so there is nothing here left to require.
    [Fact]
    public void Mongo_metadata_needs_no_collection()
    {
        _sut.Validate(Request(DestinationType.Mongo, new { dest_name = "Mongo" })).IsValid.Should().BeTrue();
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
    public void FhirRepository_with_no_metadata_passes_matching_todays_absence_of_validation()
    {
        _sut.Validate(Request(DestinationType.FhirRepository, metadata: null)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void FhirRepository_auth_type_absent_passes_without_requiring_target()
    {
        _sut.Validate(Request(DestinationType.FhirRepository, new { })).IsValid.Should().BeTrue();
    }

    [Fact]
    public void FhirRepository_explicit_none_auth_type_passes_without_requiring_target()
    {
        _sut.Validate(Request(DestinationType.FhirRepository, new { dest_fhirAuthType = "none" })).IsValid.Should().BeTrue();
    }

    [Fact]
    public void FhirRepository_unsupported_auth_type_fails()
    {
        var result = _sut.Validate(Request(DestinationType.FhirRepository, new { dest_fhirAuthType = "made-up" }));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_fhirAuthType");
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("basic")]
    [InlineData("clientCredentials")]
    public void FhirRepository_non_none_auth_type_without_target_fails(string authType)
    {
        var result = _sut.Validate(Request(DestinationType.FhirRepository, new { dest_fhirAuthType = authType }));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Target");
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("basic")]
    [InlineData("clientCredentials")]
    public void FhirRepository_non_none_auth_type_with_target_passes(string authType)
    {
        var request = Request(DestinationType.FhirRepository, new { dest_fhirAuthType = authType }) with
        {
            Target = "https://aidbox.example.com/fhir",
        };

        _sut.Validate(request).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Blob_metadata_missing_auth_mode_or_container_fails()
    {
        var result = _sut.Validate(Request(DestinationType.BlobStorage, new { }));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_blobAuthMode");
        result.Errors.Should().Contain(e => e.PropertyName == "dest_blobContainer");
    }

    [Fact]
    public void Blob_connectionString_mode_with_just_container_passes()
    {
        var metadata = new { dest_blobAuthMode = "connectionString", dest_blobContainer = "fhir" };
        _sut.Validate(Request(DestinationType.BlobStorage, metadata)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("FHIR_Export")]
    [InlineData("ab")]
    [InlineData("-fhir")]
    [InlineData("fhir-")]
    [InlineData("fhir--export")]
    public void Blob_container_name_violating_Azure_naming_rules_fails(string container)
    {
        var metadata = new { dest_blobAuthMode = "connectionString", dest_blobContainer = container };

        var result = _sut.Validate(Request(DestinationType.BlobStorage, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_blobContainer");
    }

    [Fact]
    public void Blob_accountKey_mode_missing_account_name_fails()
    {
        var metadata = new { dest_blobAuthMode = "accountKey", dest_blobContainer = "fhir" };

        var result = _sut.Validate(Request(DestinationType.BlobStorage, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_blobAccountName");
    }

    [Fact]
    public void Blob_accountKey_mode_with_account_name_passes()
    {
        var metadata = new
        {
            dest_blobAuthMode = "accountKey",
            dest_blobContainer = "fhir",
            dest_blobAccountName = "acct",
        };

        _sut.Validate(Request(DestinationType.BlobStorage, metadata)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Blob_managedIdentity_mode_missing_account_url_fails()
    {
        var metadata = new { dest_blobAuthMode = "managedIdentity", dest_blobContainer = "fhir" };

        var result = _sut.Validate(Request(DestinationType.BlobStorage, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_blobAccountUrl");
    }

    [Fact]
    public void Blob_servicePrincipal_mode_missing_tenant_and_client_id_fails()
    {
        var metadata = new
        {
            dest_blobAuthMode = "servicePrincipal",
            dest_blobContainer = "fhir",
            dest_blobAccountUrl = "https://acct.blob.core.windows.net",
        };

        var result = _sut.Validate(Request(DestinationType.BlobStorage, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_blobTenantId");
        result.Errors.Should().Contain(e => e.PropertyName == "dest_blobClientId");
    }

    [Fact]
    public void Blob_servicePrincipal_mode_with_all_required_fields_passes()
    {
        var metadata = new
        {
            dest_blobAuthMode = "servicePrincipal",
            dest_blobContainer = "fhir",
            dest_blobAccountUrl = "https://acct.blob.core.windows.net",
            dest_blobTenantId = "tenant",
            dest_blobClientId = "client",
        };

        _sut.Validate(Request(DestinationType.BlobStorage, metadata)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("dest_blobFolderPattern")]
    [InlineData("dest_blobFileNamePattern")]
    public void Blank_naming_pattern_is_valid(string key)
    {
        var metadata = new Dictionary<string, string>
        {
            ["dest_blobAuthMode"] = "connectionString",
            ["dest_blobContainer"] = "fhir",
            [key] = "",
        };

        _sut.Validate(Request(DestinationType.BlobStorage, metadata)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("dest_blobFolderPattern", @"{name}\{date:yyyyMMdd}")]
    [InlineData("dest_blobFileNamePattern", @"{id}\{guid}.json")]
    public void Naming_pattern_with_a_backslash_fails(string key, string pattern)
    {
        var metadata = new Dictionary<string, string>
        {
            ["dest_blobAuthMode"] = "connectionString",
            ["dest_blobContainer"] = "fhir",
            [key] = pattern,
        };

        var result = _sut.Validate(Request(DestinationType.BlobStorage, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == key);
    }

    [Theory]
    [InlineData("dest_blobFolderPattern", "{name}/")]
    [InlineData("dest_blobFileNamePattern", "{id}.")]
    public void Naming_pattern_ending_with_dot_or_slash_fails(string key, string pattern)
    {
        var metadata = new Dictionary<string, string>
        {
            ["dest_blobAuthMode"] = "connectionString",
            ["dest_blobContainer"] = "fhir",
            [key] = pattern,
        };

        var result = _sut.Validate(Request(DestinationType.BlobStorage, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == key);
    }

    [Fact]
    public void Naming_pattern_exceeding_the_max_length_fails()
    {
        var metadata = new Dictionary<string, string>
        {
            ["dest_blobAuthMode"] = "connectionString",
            ["dest_blobContainer"] = "fhir",
            ["dest_blobFileNamePattern"] = new string('a', 513) + ".json",
        };

        var result = _sut.Validate(Request(DestinationType.BlobStorage, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_blobFileNamePattern");
    }

    [Fact]
    public void Naming_patterns_using_only_the_documented_tokens_pass()
    {
        var metadata = new Dictionary<string, string>
        {
            ["dest_blobAuthMode"] = "connectionString",
            ["dest_blobContainer"] = "fhir",
            ["dest_blobFolderPattern"] = "{name}/{date:yyyy/MM/dd}",
            ["dest_blobFileNamePattern"] = "{id}_{date:yyyyMMddHHmmssfff}_{guid}.json",
        };

        _sut.Validate(Request(DestinationType.BlobStorage, metadata)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("{id}/{guid}.json")]
    [InlineData("sub/{id}.json")]
    public void File_name_pattern_containing_a_slash_fails(string pattern)
    {
        var metadata = new Dictionary<string, string>
        {
            ["dest_blobAuthMode"] = "connectionString",
            ["dest_blobContainer"] = "fhir",
            ["dest_blobFileNamePattern"] = pattern,
        };

        var result = _sut.Validate(Request(DestinationType.BlobStorage, metadata));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "dest_blobFileNamePattern");
    }

    [Fact]
    public void Folder_pattern_containing_a_slash_still_passes()
    {
        var metadata = new Dictionary<string, string>
        {
            ["dest_blobAuthMode"] = "connectionString",
            ["dest_blobContainer"] = "fhir",
            ["dest_blobFolderPattern"] = "{name}/{date:yyyy/MM/dd}",
        };

        _sut.Validate(Request(DestinationType.BlobStorage, metadata)).IsValid.Should().BeTrue();
    }
}
