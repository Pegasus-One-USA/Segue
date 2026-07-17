using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Tests connectivity for non-relational (CSV) destination storage types that the destination wizard collects
/// discrete connection fields for. Only 'sftp' is a genuine network connection today (blob/S3/local/gcs storage
/// types have no discrete-field tester yet).
/// </summary>
public interface ICsvDestinationConnectionTestService
{
    Task<ConnectionTestResultDto> TestSftpConnectionAsync(
        SftpConnectionTestRequest request,
        CancellationToken cancellationToken);
}
