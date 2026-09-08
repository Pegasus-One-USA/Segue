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
        IConfigurationRepository configurationRepository, ISecretProvider secretProvider)
    {
        _configurationRepository = configurationRepository;
        _secretProvider = secretProvider;
    }

    public async Task<ConnectionTestResultDto> TestSftpConnectionAsync(
        SftpConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        // Re-testing an already-saved destination: the form never re-displays the stored password, so a blank
        // one here means "use what's already saved" — resolve it from the stored "sftp://user:password@host:port/
        // path" secret URI instead (see MappedSftpDestinationWriter/SftpUploader for the same URI shape).
        if (string.IsNullOrWhiteSpace(request.Password) && request.DestinationId is { } destinationId)
        {
            var storedPassword = await ResolveStoredPasswordAsync(destinationId, cancellationToken);
            if (storedPassword is not null)
            {
                request = request with { Password = storedPassword };
            }
        }

        return await Task.Run(() =>
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

    private async Task<string?> ResolveStoredPasswordAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        var destination = await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
        if (destination is null)
        {
            return null;
        }

        string secretUri;
        try
        {
            secretUri = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        }
        catch (SecretNotConfiguredException)
        {
            return null;
        }

        if (!Uri.TryCreate(secretUri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "sftp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        return userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
    }
}
