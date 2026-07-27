using FHIRBridge.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Diagnoses the two most common relational-destination write failures (see
/// <c>RelationalDestinationWriterBase.WriteAsync</c>, which lets the ADO.NET driver's exception propagate
/// unmodified): a login/credentials failure vs. an unreachable host. Matches by message shape rather than a
/// provider-specific exception type (Microsoft.Data.SqlClient, Npgsql, MySqlConnector all phrase these
/// differently but share recognizable substrings), so this rule needs no dependency on any specific driver.
/// </summary>
public sealed class SqlDestinationFailureDiagnosisRule : IFailureDiagnosisRule
{
    public bool Matches(Exception exception) =>
        LoginFailure(exception.Message) || HostUnreachable(exception.Message);

    public Diagnosis Diagnose(Exception exception) => LoginFailure(exception.Message)
        ? new Diagnosis("Check the credentials on this destination connection.", DiagnosisAction.SelfFix)
        : new Diagnosis("Check network/firewall access to this destination.", DiagnosisAction.SelfFix);

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
}
