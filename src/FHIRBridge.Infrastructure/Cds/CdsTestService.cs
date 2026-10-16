using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Governance;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Cds;

/// <summary>
/// The admin Test screen: runs a pasted request through the same <see cref="CdsEvaluator"/> path as a live call,
/// without a JWT and also on a disabled instance. It writes no CDS call log row and no CDS metric (it is not an EHR
/// call); the governance audit records only that a test ran, against which service, and its outcome.
/// </summary>
public sealed class CdsTestService : ICdsTestService
{
    private readonly ICdsServiceInstanceRepository _instances;
    private readonly ICdsServiceHandlerRegistry _handlers;
    private readonly CdsEvaluator _evaluator;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly ILogger<CdsTestService> _logger;

    public CdsTestService(
        ICdsServiceInstanceRepository instances,
        ICdsServiceHandlerRegistry handlers,
        CdsEvaluator evaluator,
        IGovernanceLogger governanceLogger,
        ILogger<CdsTestService> logger)
    {
        _instances = instances;
        _handlers = handlers;
        _evaluator = evaluator;
        _governanceLogger = governanceLogger;
        _logger = logger;
    }

    public async Task<CdsTestResultDto> RunAsync(Guid instanceId, CdsRequest request, CancellationToken cancellationToken)
    {
        var instance = await _instances.GetByIdAsync(instanceId, cancellationToken)
            ?? throw new NotFoundException("CdsService", instanceId);

        CdsTestResultDto result;
        if (_handlers.Find(instance.HandlerKey) is not { } handler)
        {
            result = new CdsTestResultDto(
                nameof(CdsCallOutcome.Error),
                (int)CdsCallStatus.Error,
                null,
                0,
                [$"The handler type '{instance.HandlerKey}' of this service is not available."],
                CdsSettingsValues.Resolve([], instance.SettingsJson).ToJsonObject());
        }
        else
        {
            var evaluation = await _evaluator.EvaluateAsync(instance, handler, request, cancellationToken);
            var messages = new List<string>(evaluation.Messages);
            if (!instance.IsEnabled)
            {
                messages.Add("This service is disabled: EHR calls to it currently get 404.");
            }

            result = new CdsTestResultDto(
                evaluation.Outcome.ToString(),
                (int)evaluation.Status,
                evaluation.Status == CdsCallStatus.Ok ? evaluation.Response : null,
                evaluation.DurationMs,
                messages,
                evaluation.ResolvedSettings);
        }

        try
        {
            await _governanceLogger.LogAuditAsync(
                new AuditEntry(
                    Module: "CdsHooks",
                    Action: "TestRun",
                    EntityType: "CdsService",
                    EntityId: instance.Id.ToString(),
                    EntityName: instance.Title,
                    Remarks: $"Test run of '{instance.ServiceId}': {result.Outcome} ({result.HttpStatus}), " +
                             $"{result.Response?.Cards.Count ?? 0} card(s), {result.DurationMs} ms."),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError("Could not audit the CDS test run of {ServiceId}: {ErrorType}.", instance.ServiceId, exception.GetType().Name);
        }

        return result;
    }
}
