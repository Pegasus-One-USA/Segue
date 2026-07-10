using System.Net.Http.Headers;
using FHIRBridge.Application.Abstractions.Audit;
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
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

public sealed class SourceConnectionTestService : ISourceConnectionTestService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISecretProvider _secretProvider;
    private readonly IFhirAccessTokenProvider _accessTokenProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOperationalAuditService _auditService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<SourceConnectionTestService> _logger;

    public SourceConnectionTestService(
        IConfigurationRepository configurationRepository,
        ISecretProvider secretProvider,
        IFhirAccessTokenProvider accessTokenProvider,
        IHttpClientFactory httpClientFactory,
        IOperationalAuditService auditService,
        ICurrentUserService currentUserService,
        ILogger<SourceConnectionTestService> logger)
    {
        _configurationRepository = configurationRepository;
        _secretProvider = secretProvider;
        _accessTokenProvider = accessTokenProvider;
        _httpClientFactory = httpClientFactory;
        _auditService = auditService;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    public async Task<SourceConnectionTestResultDto> TestAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken)
            ?? throw new NotFoundException("SourceConnection", sourceConnectionId);

        await RecordAuditAsync(
            sourceConnectionId,
            "SourceConnectionTestStarted",
            "Started",
            $"Source connection test started for {sourceConnection.SourceSystemType}.",
            cancellationToken);

        if (!sourceConnection.IsEnabled)
        {
            const string message = "Source connection is disabled and cannot be tested.";
            await RecordAuditAsync(
                sourceConnectionId,
                "SourceConnectionTestSkipped",
                "Skipped",
                message,
                cancellationToken);

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

            await RecordAuditAsync(
                sourceConnectionId,
                result.IsSuccessful ? "SourceConnectionTestCompleted" : "SourceConnectionTestFailed",
                result.Status,
                result.Message,
                cancellationToken);

            return result;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Source connection test failed for source {SourceConnectionId}.",
                sourceConnectionId);

            await RecordAuditAsync(
                sourceConnectionId,
                "SourceConnectionTestFailed",
                "Failed",
                exception.Message,
                cancellationToken);

            return new SourceConnectionTestResultDto(
                sourceConnectionId,
                sourceConnection.SourceSystemType.ToString(),
                false,
                "Failed",
                exception.Message,
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

    private Task RecordAuditAsync(
        Guid sourceConnectionId,
        string action,
        string status,
        string message,
        CancellationToken cancellationToken)
    {
        return _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                null,
                null,
                sourceConnectionId,
                null,
                null,
                null,
                action,
                status,
                message,
                null,
                _currentUserService.CurrentUser.AuditName,
                null),
            cancellationToken);
    }
}
