using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.SharedKernel.Exceptions;
using Renci.SshNet;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Opens an SFTP session with the wizard's discrete host/port/username/password fields and verifies the remote
/// folder is reachable — the CSV-destination analogue of <see cref="SqlDestinationSchemaService"/>'s relational
/// probe. Never throws for connection failures; returns <c>Connected=false</c> + <c>Error</c> instead.
/// </summary>
public sealed class SftpDestinationConnectionTestService : ICsvDestinationConnectionTestService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProvider _secretProvider;

    public SftpDestinationConnectionTestService(
        IConfigurationRepository configurationRepository,
        ISecretProvider secretProvider)
    {
        _configurationRepository = configurationRepository;
        _secretProvider = secretProvider;
    }

    public async Task<ConnectionTestResultDto> TestSftpConnectionAsync(
        SftpConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        var password = request.Password;
        if (string.IsNullOrWhiteSpace(password) && request.DestinationId is { } destinationId)
        {
            password = await ResolveStoredPasswordAsync(destinationId, cancellationToken);
        }

        return await Task.Run(() =>
        {
            try
            {
                using var client = new SftpClient(request.Host, request.Port, request.Username, password ?? string.Empty);
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

    /// <summary>Resolves an already-saved SFTP destination's stored secret (the whole sftp://user:pass@host/...
    /// URI — see WorkflowBuildAssemblerService.buildSftpUri/destination-connection-secret.util.ts's own
    /// buildSftpUri, which is what originally wrote it) and extracts just the password component, so a Test
    /// Connection with a blank password field can still verify against the real stored credential without the
    /// browser ever holding it. Returns null (never throws) if the destination, its secret, or the URI shape
    /// isn't resolvable — the caller then just attempts the connection with a blank password, which fails
    /// honestly rather than masking the real problem.</summary>
    private async Task<string?> ResolveStoredPasswordAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        try
        {
            var destination = await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
            if (destination is null) return null;

            var storedSecret = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
            if (!Uri.TryCreate(storedSecret, UriKind.Absolute, out var sftpUri)) return null;

            var separatorIndex = sftpUri.UserInfo.IndexOf(':');
            if (separatorIndex < 0) return null;

            return Uri.UnescapeDataString(sftpUri.UserInfo[(separatorIndex + 1)..]);
        }
        catch (SecretNotConfiguredException)
        {
            return null;
        }
    }
}
