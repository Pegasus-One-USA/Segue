using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Xunit;

namespace FHIRBridge.UnitTests.Destinations;

// Covers SqlConnectionSecretMerger.TryInheritCredentials for every SQL-family engine — the fix for a fork
// (picking an existing connection, then editing an unrelated field like "Require SSL") forcing a password
// retype, by inheriting the old connection's credentials into the newly-built (password-blank) one.
public sealed class SqlConnectionSecretMergerTests
{
    private readonly SqlConnectionSecretMerger _sut = new();

    [Fact]
    public void Postgres_inherits_the_password_when_the_new_secret_is_missing_one()
    {
        var result = _sut.TryInheritCredentials(
            DestinationType.PostgreSql,
            existingSecret: "Host=old;Database=db;Username=admin;Password=hunter2;SSL Mode=Prefer",
            newSecret: "Host=new;Database=db;Username=admin;Password=;SSL Mode=Require");

        result.Should().NotBeNull();
        var merged = new Npgsql.NpgsqlConnectionStringBuilder(result!);
        merged.Password.Should().Be("hunter2");
        merged.Host.Should().Be("new");
        merged.SslMode.Should().Be(Npgsql.SslMode.Require);
    }

    [Fact]
    public void MySql_inherits_the_password_when_the_new_secret_is_missing_one()
    {
        var result = _sut.TryInheritCredentials(
            DestinationType.MySql,
            existingSecret: "Server=old;Database=db;User Id=admin;Password=hunter2;SslMode=Preferred",
            newSecret: "Server=new;Database=db;User Id=admin;Password=;SslMode=Required");

        result.Should().NotBeNull();
        var merged = new MySqlConnector.MySqlConnectionStringBuilder(result!);
        merged.Password.Should().Be("hunter2");
        merged.Server.Should().Be("new");
    }

    [Fact]
    public void SqlServer_inherits_the_password_when_the_new_secret_is_missing_one()
    {
        var result = _sut.TryInheritCredentials(
            DestinationType.SqlServer,
            existingSecret: "Server=old;Database=db;User Id=admin;Password=hunter2;TrustServerCertificate=True;Encrypt=True",
            newSecret: "Server=new;Database=db;User Id=admin;Password=;TrustServerCertificate=True;Encrypt=True");

        result.Should().NotBeNull();
        var merged = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(result!);
        merged.Password.Should().Be("hunter2");
        merged.DataSource.Should().Be("new");
    }

    [Fact]
    public void AzureSql_uses_the_same_merge_as_SqlServer()
    {
        var result = _sut.TryInheritCredentials(
            DestinationType.AzureSql,
            existingSecret: "Server=old;Database=db;User Id=admin;Password=hunter2;TrustServerCertificate=True;Encrypt=True",
            newSecret: "Server=new;Database=db;User Id=admin;Password=;TrustServerCertificate=True;Encrypt=True");

        result.Should().NotBeNull();
    }

    [Fact]
    public void Returns_null_when_the_new_secret_already_has_a_real_password()
    {
        var result = _sut.TryInheritCredentials(
            DestinationType.PostgreSql,
            existingSecret: "Host=old;Username=admin;Password=hunter2;",
            newSecret: "Host=new;Username=admin;Password=typed-by-user;");

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_the_old_secret_has_no_password_either()
    {
        var result = _sut.TryInheritCredentials(
            DestinationType.MySql,
            existingSecret: "Server=old;User Id=admin;Password=;",
            newSecret: "Server=new;User Id=admin;Password=;");

        result.Should().BeNull();
    }

    [Fact]
    public void SqlServer_never_touches_Active_Directory_Default_connections()
    {
        var result = _sut.TryInheritCredentials(
            DestinationType.SqlServer,
            existingSecret: "Server=old;Database=db;User Id=admin;Password=hunter2;TrustServerCertificate=True;Encrypt=True",
            newSecret: "Server=new;Database=db;Authentication=Active Directory Default;TrustServerCertificate=True;Encrypt=True");

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_a_non_SQL_family_destination_type()
    {
        var result = _sut.TryInheritCredentials(
            DestinationType.Mongo,
            existingSecret: "mongodb://user:pass@old:27017/db",
            newSecret: "mongodb://user:@new:27017/db");

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_null_instead_of_throwing_when_a_connection_string_is_malformed()
    {
        var result = _sut.TryInheritCredentials(
            DestinationType.PostgreSql,
            existingSecret: "not a connection string at all ===;;;",
            newSecret: "Host=new;Password=;");

        result.Should().BeNull();
    }
}
