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
    private readonly ITenantConfigurationRepository _tenantRepository;
    private readonly ISecretProvider _secretProvider;
    private readonly IFhirAccessTokenProvider _accessTokenProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOperationalAuditService _auditService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<SourceConnectionTestService> _logger;

    public SourceConnectionTestService(
        ITenantConfigurationRepository tenantRepository,
        ISecretProvider secretProvider,
        IFhirAccessTokenProvider accessTokenProvider,
        IHttpClientFactory httpClientFactory,
        IOperationalAuditService auditService,
        ICurrentUserService currentUserService,
        ILogger<SourceConnectionTestService> logger)
    {
        _tenantRepository = tenantRepository;
        _secretProvider = secretProvider;
        _accessTokenProvider = accessTokenProvider;
        _httpClientFactory = httpClientFactory;
        _auditService = auditService;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    public async Task<SourceConnectionTestResultDto> TestAsync(
        Guid tenantId,
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var tenant = await _tenantRepository.GetByIdAsync(tenantId, cancellationToken)
            ?? throw new NotFoundException("Tenant", tenantId);
        var sourceConnection = tenant.SourceConnections.FirstOrDefault(x => x.Id == sourceConnectionId)
            ?? throw new NotFoundException("SourceConnection", sourceConnectionId);

        await RecordAuditAsync(
            tenantId,
            sourceConnectionId,
            "SourceConnectionTestStarted",
            "Started",
            $"Source connection test started for {sourceConnection.SourceSystemType}.",
            cancellationToken);

        if (!sourceConnection.IsEnabled)
        {
            const string message = "Source connection is disabled and cannot be tested.";
            await RecordAuditAsync(
                tenantId,
                sourceConnectionId,
                "SourceConnectionTestSkipped",
                "Skipped",
                message,
                cancellationToken);

            return new SourceConnectionTestResultDto(
                tenantId,
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
                    tenantId,
                    sourceConnectionId,
                    sourceConnection.SourceSystemType.ToString(),
                    true,
                    "Completed",
                    "Sample source connection is available.",
                    DateTime.UtcNow),
                SourceSystemType.Epic => await TestEpicAsync(tenantId, sourceConnection, cancellationToken),
                _ => throw new NotSupportedException($"Source connection test is not implemented for {sourceConnection.SourceSystemType}.")
            };

            await RecordAuditAsync(
                tenantId,
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
                "Source connection test failed for tenant {TenantId}, source {SourceConnectionId}.",
                tenantId,
                sourceConnectionId);

            await RecordAuditAsync(
                tenantId,
                sourceConnectionId,
                "SourceConnectionTestFailed",
                "Failed",
                exception.Message,
                cancellationToken);

            return new SourceConnectionTestResultDto(
                tenantId,
                sourceConnectionId,
                sourceConnection.SourceSystemType.ToString(),
                false,
                "Failed",
                exception.Message,
                DateTime.UtcNow);
        }
    }

    private async Task<SourceConnectionTestResultDto> TestEpicAsync(
        Guid tenantId,
        SourceConnection sourceConnection,
        CancellationToken cancellationToken)
    {
        var source = await BuildEpicSourceConfigurationAsync(tenantId, sourceConnection, cancellationToken);
        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        var metadataUrl = $"{sourceConnection.BaseUrl.TrimEnd('/')}/metadata";

        var httpClient = _httpClientFactory.CreateClient(nameof(SourceConnectionTestService));
        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new SourceConnectionTestResultDto(
                tenantId,
                sourceConnection.Id,
                sourceConnection.SourceSystemType.ToString(),
                false,
                "Failed",
                $"Epic metadata endpoint returned {(int)response.StatusCode}.",
                DateTime.UtcNow);
        }

        return new SourceConnectionTestResultDto(
            tenantId,
            sourceConnection.Id,
            sourceConnection.SourceSystemType.ToString(),
            true,
            "Completed",
            "Epic SMART Backend Services authentication and FHIR metadata call succeeded.",
            DateTime.UtcNow);
    }

    private async Task<FhirSourceConfiguration> BuildEpicSourceConfigurationAsync(
        Guid tenantId,
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
            tenantId,
            sourceConnection.Id,
            ApplicationType: sourceConnection.ApplicationType);
    }

    private Task RecordAuditAsync(
        Guid tenantId,
        Guid sourceConnectionId,
        string action,
        string status,
        string message,
        CancellationToken cancellationToken)
    {
        return _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                tenantId,
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
