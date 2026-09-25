using Microsoft.Data.SqlClient;

namespace FHIRBridge.Integration.Sql;

/// <summary>
/// Opens SQL Server / Azure SQL / Fabric SQL Database connections for destination writers. The customer owns and
/// manages the destination database; a missing database surfaces as a normal SQL connection error rather than
/// being auto-created.
///
/// <para><b>Entra-only targets need no token code here.</b> A SQL Database in Microsoft Fabric has no SQL logins
/// at all — every documented way of reaching one authenticates through Microsoft Entra — so a connection string
/// carrying a user id and password cannot reach it. What makes that work is the connection string's own
/// <c>Authentication</c> keyword, which SqlClient implements natively: <c>Active Directory Default</c> (SqlClient
/// 3.0+) runs the full <c>DefaultAzureCredential</c> chain, and <c>Active Directory Service Principal</c>
/// (SqlClient 2.0+) reads User Id and Password as the client id and secret. This solution is on 5.2.2, and the
/// Azure.Identity the first mode needs already arrives transitively with it.</para>
///
/// <para>An earlier draft of this class intercepted those two modes to fetch and attach a token by hand, written
/// on the assumption that the generic SQL path had no Entra support at all. It has. Re-implementing what the
/// driver already does would have added failure modes — a second credential chain to keep in step, and a tenant
/// id the connection string has no field for — without adding capability, so the interception was dropped.
/// Contrast <c>FabricWarehouseConnectionFactory</c>, which genuinely does attach a token: a Fabric Warehouse
/// destination configures its identity through the wizard's own auth fields, so there is no <c>Authentication</c>
/// keyword for the driver to act on.</para>
///
/// <para><b>What this adds over <c>new SqlConnection(...)</c></b> is a check that a connection string naming an
/// Entra mode is not also carrying something that contradicts it — SqlClient's own error for that combination
/// names neither half, and reads like a permissions problem.</para>
/// </summary>
public static class SqlServerConnectionFactory
{
    public static async Task<SqlConnection> OpenConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        GuardEntraConnectionString(connectionString);

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        return connection;
    }

    /// <summary>
    /// Refuses a connection string whose Entra mode cannot work as written, naming the specific problem.
    ///
    /// <para>Both cases below otherwise surface as an authentication failure from the service, which reads like a
    /// missing role assignment and sends someone to check permissions that are in fact correct. The distinction
    /// matters most for a Fabric SQL Database, where Entra is the only option and a half-converted connection
    /// string is the likeliest first mistake.</para>
    /// </summary>
    internal static void GuardEntraConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);

        if (builder.Authentication == SqlAuthenticationMethod.NotSpecified)
        {
            return;
        }

        var isServicePrincipal = builder.Authentication == SqlAuthenticationMethod.ActiveDirectoryServicePrincipal;

        // Service principal carries its client id and secret in User Id and Password. A missing half is an
        // incomplete connection string, not a rejected credential.
        if (isServicePrincipal
            && (string.IsNullOrWhiteSpace(builder.UserID) || string.IsNullOrWhiteSpace(builder.Password)))
        {
            throw new InvalidOperationException(
                "Connection string uses 'Active Directory Service Principal' authentication, which takes the "
                    + "application (client) id in User Id and the client secret in Password. One of them is "
                    + "missing.");
        }

        // A password alongside a mode that has no use for one means the string was written for SQL authentication
        // and then had an Authentication keyword added: the password is silently ignored, and the failure that
        // follows looks like the identity lacks access rather than like a configuration mistake.
        if (!isServicePrincipal
            && builder.Authentication != SqlAuthenticationMethod.ActiveDirectoryPassword
            && !string.IsNullOrWhiteSpace(builder.Password))
        {
            throw new InvalidOperationException(
                $"Connection string uses '{builder.Authentication}' authentication, which ignores Password — the "
                    + "identity comes from the host environment, not the connection string. Remove Password, or "
                    + "switch to 'Active Directory Service Principal' if you meant to authenticate with a client "
                    + "id and secret.");
        }
    }
}
