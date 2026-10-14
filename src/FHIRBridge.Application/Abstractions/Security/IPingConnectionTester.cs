namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Checks the saved PingOne settings end to end: the worker application can obtain a Management API token and
/// read groups. Backs the "Test connection" button on the SSO Configurations screen.
/// </summary>
public interface IPingConnectionTester
{
    Task<ExternalConnectionTestResult> TestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A non-reversible hint about the stored worker secret (its length and last four characters) so an admin can
    /// compare it with the PingOne console and spot a mis-paste. Null when none is stored or it can't be read.
    /// </summary>
    Task<string?> GetWorkerSecretHintAsync(CancellationToken cancellationToken);
}
