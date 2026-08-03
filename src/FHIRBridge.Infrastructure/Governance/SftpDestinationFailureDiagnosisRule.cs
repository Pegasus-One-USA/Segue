using FHIRBridge.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Diagnoses SFTP destination failures thrown by <c>SftpUploader</c> (shared by
/// <c>MappedSftpDestinationWriter</c> and <c>SftpDeliveryStrategy</c>'s CSV-over-SFTP delivery) — both its own
/// config-validation throws and the underlying SSH.NET (Renci.SshNet) connection/auth exceptions, matched by type
/// name rather than a direct package reference so this rule lives in FHIRBridge.Infrastructure without pulling in
/// SSH.NET just for exception types.
/// </summary>
public sealed class SftpDestinationFailureDiagnosisRule : IFailureDiagnosisRule
{
    public bool Matches(Exception exception) =>
        exception.GetType().Namespace?.Contains("Renci.SshNet", StringComparison.OrdinalIgnoreCase) == true
        || exception.Message.Contains("SFTP destination secret", StringComparison.OrdinalIgnoreCase);

    public Diagnosis Diagnose(Exception exception) => exception.GetType().Name switch
    {
        "SshAuthenticationException" => new Diagnosis(
            "The SFTP server rejected the username/password in this destination's connection secret.",
            DiagnosisAction.SelfFix),
        "SshConnectionException" or "SshOperationTimeoutException" or "SocketException" => new Diagnosis(
            "Could not reach the SFTP server — check the host, port, and network/firewall access from this " +
            "environment.",
            DiagnosisAction.SelfFix),
        "SftpPathNotFoundException" or "SftpPermissionDeniedException" => new Diagnosis(
            "The SFTP account does not have access to the configured remote directory.",
            DiagnosisAction.SelfFix),
        _ when exception.Message.Contains("SFTP destination secret", StringComparison.OrdinalIgnoreCase) => new Diagnosis(
            "The SFTP destination's connection secret is not a valid 'sftp://user:password@host:port/path' URI.",
            DiagnosisAction.SelfFix),
        _ => new Diagnosis(
            "The SFTP server rejected or could not complete this upload — check the connection secret and " +
            "remote path configured on this destination.",
            DiagnosisAction.SelfFix),
    };
}
