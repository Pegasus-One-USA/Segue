using FHIRBridge.Integration.Sql;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// Covers the connection-string guard that runs before any network call. Opening a connection needs a real server,
/// so what is testable here is exactly what the guard exists for: catching a connection string that cannot work as
/// written, before it produces an authentication error that names neither half of the problem.
///
/// <para>Note what is deliberately NOT tested: that Entra authentication works. SqlClient implements those modes,
/// this class does not, and a test asserting the driver's own behaviour would only restate its documentation.</para>
/// </summary>
public sealed class SqlServerConnectionFactoryTests
{
    private static Action Guard(string connectionString)
        => () => SqlServerConnectionFactory.GuardEntraConnectionString(connectionString);

    /// <summary>
    /// The ordinary case: no Authentication keyword at all means SQL or Windows authentication, which the guard
    /// has nothing to say about.
    /// </summary>
    [Theory]
    [InlineData("Server=localhost;Database=Test;User Id=sa;Password=secret")]
    [InlineData("Server=localhost;Database=Test;Integrated Security=true")]
    public void A_connection_string_without_an_entra_mode_passes_untouched(string connectionString)
        => Guard(connectionString).Should().NotThrow();

    /// <summary>
    /// Active Directory Default takes its identity from the host environment — a managed identity in Azure, a
    /// developer sign-in locally. This is the shape a Fabric SQL Database destination uses in production.
    /// </summary>
    [Fact]
    public void Active_directory_default_needs_no_credential_fields()
        => Guard("Server=x.database.fabric.microsoft.com;Database=Clinical;Authentication=Active Directory Default")
            .Should().NotThrow();

    /// <summary>
    /// Service principal is complete when both halves are present: client id in User Id, secret in Password.
    /// </summary>
    [Fact]
    public void A_complete_service_principal_connection_string_passes()
        => Guard(
                "Server=x.database.fabric.microsoft.com;Database=Clinical;"
                    + "Authentication=Active Directory Service Principal;User Id=app-id;Password=app-secret")
            .Should().NotThrow();

    /// <summary>
    /// Half a service principal is the likeliest first mistake, and SqlClient reports it as a failed login rather
    /// than as a missing field — so the guard names the field instead.
    /// </summary>
    [Theory]
    [InlineData("User Id=app-id", "Password")]
    [InlineData("Password=app-secret", "User Id")]
    public void An_incomplete_service_principal_is_refused_by_name(string half, string _)
    {
        Guard($"Server=x;Database=y;Authentication=Active Directory Service Principal;{half}")
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Service Principal*missing*");
    }

    /// <summary>
    /// A password left on a mode that ignores it is the fingerprint of a SQL-authentication connection string that
    /// had an Authentication keyword bolted on. The password is silently ignored and the connection then fails as
    /// though the identity lacked access, which sends someone to check role assignments that are already correct.
    /// </summary>
    [Fact]
    public void A_password_left_on_a_mode_that_ignores_it_is_refused()
    {
        Guard(
                "Server=x.database.fabric.microsoft.com;Database=Clinical;"
                    + "Authentication=Active Directory Default;User Id=sa;Password=leftover")
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*ignores Password*");
    }

    /// <summary>
    /// Managed identity reads User Id as the user-assigned identity's client id, which is legitimate and carries
    /// no password — so it must not be caught by the rule above.
    /// </summary>
    [Fact]
    public void Managed_identity_may_carry_a_client_id_in_user_id()
        => Guard(
                "Server=x.database.fabric.microsoft.com;Database=Clinical;"
                    + "Authentication=Active Directory Managed Identity;User Id=11111111-2222-3333-4444-555555555555")
            .Should().NotThrow();
}
