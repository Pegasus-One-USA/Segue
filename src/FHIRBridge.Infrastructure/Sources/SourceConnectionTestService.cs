using System.Net.Http.Headers;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

public sealed class SourceConnectionTestService : ISourceConnectionTestService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProvider _secretProvider;
    private readonly IFhirAccessTokenProvider _accessTokenProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SourceConnectionTestService> _logger;

    public SourceConnectionTestService(
        IConfigurationRepository configurationRepository,
        ISecretProvider secretProvider,
        IFhirAccessTokenProvider accessTokenProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<SourceConnectionTestService> logger)
    {
        _configurationRepository = configurationRepository;
        _secretProvider = secretProvider;
        _accessTokenProvider = accessTokenProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<SourceConnectionTestResultDto> TestAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken)
            ?? throw new NotFoundException("SourceConnection", sourceConnectionId);

        if (!sourceConnection.IsEnabled)
        {
            const string message = "Source connection is disabled and cannot be tested.";

            return new SourceConnectionTestResultDto(
                sourceConnectionId,
                sourceConnection.SourceSystemType.ToString(),
                false,
                "Skipped",
                message,
                DateTime.UtcNow);
        }

        try
        {
            var result = sourceConnection.SourceSystemType switch
            {
                SourceSystemType.Sample => new SourceConnectionTestResultDto(
                    sourceConnectionId,
                    sourceConnection.SourceSystemType.ToString(),
                    true,
                    "Completed",
                    "Sample source connection is available.",
                    DateTime.UtcNow),
                SourceSystemType.Epic => await TestEpicAsync(sourceConnection, cancellationToken),
                _ => throw new NotSupportedException($"Source connection test is not implemented for {sourceConnection.SourceSystemType}.")
            };

            // The setup-time smoke test. Its outcome previously reached only the HTTP response the portal
            // rendered, so a support question about "it said it worked yesterday" had nothing to check against.
            _logger.Log(
                result.IsSuccessful ? LogLevel.Information : LogLevel.Warning,
                LogEvents.ConnectionTestCompleted,
                "Source connection test for '{SourceName}' ({SourceConnectionId}, {SourceSystemType}) " +
                "returned {TestStatus}: Success={TestSuccess} Message={TestMessage}",
                sourceConnection.Name, sourceConnectionId, sourceConnection.SourceSystemType,
                result.Status, result.IsSuccessful, result.Message);

            return result;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                LogEvents.ConnectionTestCompleted,
                exception,
                "Source connection test threw for '{SourceName}' ({SourceConnectionId}, {SourceSystemType}): {FailureReason}",
                sourceConnection.Name,
                sourceConnectionId,
                sourceConnection.SourceSystemType,
                exception.Message);

            // Per docs/ERRORS_SCREEN_CATEGORIZATION_ANALYSIS.md §6: this previously returned exception.Message
            // raw, unlike every other error path in the app — the exact class of leak SafeErrorText exists to stop.
            //
            // A FHIRBridgeException's UserMessage is author-written and client-safe BY CONSTRUCTION, so it is
            // preferred over sanitizing Message: the sanitizer's 200-char/shape filter would otherwise reject a
            // perfectly safe explanation (e.g. SourceUnavailableException's "the source is not reachable … the
            // credentials on this connection are not the cause") and replace it with the generic fallback,
            // throwing away the very diagnosis this path exists to deliver. Same trust rule the API's global
            // exception handler already applies to FHIRBridgeException.
            var message = exception is FHIRBridge.SharedKernel.Exceptions.FHIRBridgeException domainFailure
                ? domainFailure.UserMessage
                : FHIRBridge.Governance.SafeErrorText.SanitizeOr(exception.Message, "The connection test failed.");

            return new SourceConnectionTestResultDto(
                sourceConnectionId,
                sourceConnection.SourceSystemType.ToString(),
                false,
                "Failed",
                message,
                DateTime.UtcNow);
        }
    }

    private async Task<SourceConnectionTestResultDto> TestEpicAsync(
        SourceConnection sourceConnection,
        CancellationToken cancellationToken)
    {
        // A loopback base URL has no real OAuth server — any Token Endpoint configured against it is guaranteed to
        // fail (unreachable, or misinterpreted as a FHIR REST call by a plain FHIR server). Test reachability
        // directly against /metadata with no bearer token instead of attempting a real JWT exchange. Real
        // (non-loopback) Epic endpoints are completely unaffected.
        var isLoopback = Uri.TryCreate(sourceConnection.BaseUrl, UriKind.Absolute, out var baseUri) && baseUri.IsLoopback;

        string? accessToken = null;
        if (!isLoopback)
        {
            var source = await BuildEpicSourceConfigurationAsync(sourceConnection, cancellationToken);
            accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        }

        var metadataUrl = $"{sourceConnection.BaseUrl.TrimEnd('/')}/metadata";

        var httpClient = _httpClientFactory.CreateClient(nameof(SourceConnectionTestService));
        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new SourceConnectionTestResultDto(
                sourceConnection.Id,
                sourceConnection.SourceSystemType.ToString(),
                false,
                "Failed",
                $"Epic metadata endpoint returned {(int)response.StatusCode}.",
                DateTime.UtcNow);
        }

        return new SourceConnectionTestResultDto(
            sourceConnection.Id,
            sourceConnection.SourceSystemType.ToString(),
            true,
            "Completed",
            isLoopback
                ? "Reached the FHIR metadata endpoint (loopback source — no SMART Backend Services auth attempted)."
                : "Epic SMART Backend Services authentication and FHIR metadata call succeeded.",
            DateTime.UtcNow);
    }

    private async Task<FhirSourceConfiguration> BuildEpicSourceConfigurationAsync(
        SourceConnection sourceConnection,
        CancellationToken cancellationToken)
    {
        if (sourceConnection.Authentication.PrivateKey is null)
        {
            throw new InvalidOperationException("Epic private key secret reference is missing.");
        }

        var privateKeyPem = await _secretProvider.GetSecretAsync(
            sourceConnection.Authentication.PrivateKey,
            cancellationToken);

        return new FhirSourceConfiguration(
            RuntimeSourceType.Epic,
            sourceConnection.Name,
            sourceConnection.BaseUrl,
            sourceConnection.Authentication.TokenEndpoint,
            sourceConnection.Authentication.ClientId,
            sourceConnection.Authentication.KeyId,
            privateKeyPem,
            sourceConnection.Authentication.Scopes,
            1,
            1,
            sourceConnection.Id,
            ApplicationType: sourceConnection.ApplicationType);
    }
}
