using FHIRBridge.Application.Abstractions.Security;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Backs the "Test connection" button on the SSO Configurations screen: checks the SAVED PingOne settings end to
/// end (worker token, then a group read) and turns any failure into a message that is safe to show an admin.
/// </summary>
public sealed class PingConnectionTester : IPingConnectionTester
{
    private readonly PingSettingsReader _settings;
    private readonly PingManagementClient _client;
    private readonly ISecretProvider _secretProvider;
    private readonly ILogger<PingConnectionTester> _logger;

    public PingConnectionTester(
        PingSettingsReader settings,
        PingManagementClient client,
        ISecretProvider secretProvider,
        ILogger<PingConnectionTester> logger)
    {
        _settings = settings;
        _client = client;
        _secretProvider = secretProvider;
        _logger = logger;
    }

    public async Task<ExternalConnectionTestResult> TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settings.GetAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.EnvironmentId) || string.IsNullOrWhiteSpace(settings.WorkerClientId))
            {
                return new ExternalConnectionTestResult(false, "Environment ID and Worker Client ID are required.");
            }

            if (settings.WorkerSecretReference is null)
            {
                return new ExternalConnectionTestResult(false, "The worker client secret has not been set.");
            }

            // Always a fresh token: a test must reflect the settings as saved, not a cached earlier success.
            var accessToken = await _client.GetManagementTokenAsync(settings, cancellationToken, forceFresh: true);

            using var document = await _client.GetJsonAsync($"{settings.ManagementApiBase}/groups?limit=1", accessToken, cancellationToken);
            return new ExternalConnectionTestResult(true, "Connected to PingOne. The worker application can read groups.");
        }
        catch (PingApiException ex)
        {
            _logger.LogWarning(
                "Ping connection test failed at the {Step} step: HTTP {Status} {Detail}", ex.Step, (int?)ex.Status, ex.Detail);
            return new ExternalConnectionTestResult(false, ex.ToAdminMessage());
        }
        catch (HttpRequestException)
        {
            return new ExternalConnectionTestResult(false, "PingOne could not be reached. Check the Auth/API domains and network access.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ping connection test failed.");
            return new ExternalConnectionTestResult(false, "The connection test failed. Check the settings and try again.");
        }
    }

    public async Task<string?> GetWorkerSecretHintAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settings.GetAsync(cancellationToken);
            if (settings.WorkerSecretReference is null)
            {
                return null;
            }

            var secret = (await _secretProvider.GetSecretAsync(settings.WorkerSecretReference, cancellationToken)).Trim();
            return secret.Length < 8
                ? $"{secret.Length} characters"
                : $"{secret.Length} characters, ending …{secret[^4..]}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
