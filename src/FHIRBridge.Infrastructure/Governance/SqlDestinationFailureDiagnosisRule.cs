using FHIRBridge.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Diagnoses the common relational-destination write failures that still reach the outer route-level catch (see
/// <c>RelationalDestinationWriterBase.WriteAsync</c>/<c>MappedSqlServerDestinationWriter.WriteAsync</c>, which let
/// most ADO.NET driver exceptions propagate unmodified) — a login/credentials failure, an unreachable host, a
/// permission-denied DML/DDL, or lock contention (deadlock/lock timeout). Constraint-violation errors on an
/// individual record (PK/unique/FK/NOT NULL/truncation/conversion) are caught per-record by those writers instead
/// and never reach this rule — see the per-record <c>RecordErrors</c> on <c>DestinationWriteResult</c>.
/// Matches by message shape rather than a provider-specific exception type (Microsoft.Data.SqlClient, Npgsql,
/// MySqlConnector all phrase these differently but share recognizable substrings), so this rule needs no
/// dependency on any specific driver.
/// </summary>
public sealed class SqlDestinationFailureDiagnosisRule : IFailureDiagnosisRule
{
    public bool Matches(Exception exception) =>
        LoginFailure(exception.Message) ||
        HostUnreachable(exception.Message) ||
        PermissionDenied(exception.Message) ||
        LockContention(exception.Message);

    public Diagnosis Diagnose(Exception exception)
    {
        var message = exception.Message;

        if (LoginFailure(message))
        {
            return new Diagnosis("Check the credentials on this destination connection.", DiagnosisAction.SelfFix);
        }

        if (PermissionDenied(message))
        {
            return new Diagnosis(
                "This destination's account doesn't have permission for this operation — grant it INSERT/UPDATE " +
                "(and SELECT for table/column checks) on the target table.",
                DiagnosisAction.SelfFix);
        }

        if (LockContention(message))
        {
            return new Diagnosis(
                "The destination table was locked by another process (deadlock or lock timeout) — retry the run; " +
                "if it recurs, check for other jobs writing to the same table concurrently.",
                DiagnosisAction.SelfFix);
        }

        return new Diagnosis("Check network/firewall access to this destination.", DiagnosisAction.SelfFix);
    }

    private static bool LoginFailure(string message) =>
        message.Contains("Login failed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Access denied for user", StringComparison.OrdinalIgnoreCase);

    private static bool HostUnreachable(string message) =>
        message.Contains("network-related", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("timeout expired", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("could not connect", StringComparison.OrdinalIgnoreCase);

    private static bool PermissionDenied(string message) =>
        message.Contains("permission was denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("The SELECT permission was denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("The INSERT permission was denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("The UPDATE permission was denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("permission denied for table", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("command denied to user", StringComparison.OrdinalIgnoreCase);

    private static bool LockContention(string message) =>
        message.Contains("deadlocked", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Lock wait timeout exceeded", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("could not obtain lock", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("deadlock detected", StringComparison.OrdinalIgnoreCase);
}
