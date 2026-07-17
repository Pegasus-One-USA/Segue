using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using Renci.SshNet;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Opens an SFTP session with the wizard's discrete host/port/username/password fields and verifies the remote
/// folder is reachable — the CSV-destination analogue of <see cref="SqlDestinationSchemaService"/>'s relational
/// probe. Never throws for connection failures; returns <c>Connected=false</c> + <c>Error</c> instead.
/// </summary>
public sealed class SftpDestinationConnectionTestService : ICsvDestinationConnectionTestService
{
    public Task<ConnectionTestResultDto> TestSftpConnectionAsync(
        SftpConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                using var client = new SftpClient(request.Host, request.Port, request.Username, request.Password ?? string.Empty);
                client.Connect();
                try
                {
                    var remoteFolder = string.IsNullOrWhiteSpace(request.RemoteFolder) ? "/" : request.RemoteFolder;
                    if (!client.Exists(remoteFolder))
                    {
                        return new ConnectionTestResultDto(false, $"Remote folder '{remoteFolder}' does not exist.");
                    }
                }
                finally
                {
                    client.Disconnect();
                }

                return new ConnectionTestResultDto(true, null);
            }
            catch (Exception exception)
            {
                return new ConnectionTestResultDto(false, exception.Message);
            }
        }, cancellationToken);
    }
}
