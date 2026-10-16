using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Cds;

namespace FHIRBridge.Application.Services.Cds;

public sealed class CdsCatalogService : ICdsCatalogService
{
    private const int MaxPageSize = 200;

    private readonly ICdsServiceHandlerRegistry _handlers;
    private readonly ICdsServiceInstanceRepository _instanceRepository;
    private readonly ICdsTrustedIssuerRepository _issuerRepository;
    private readonly ICdsCallLogRepository _callLogRepository;
    private readonly ICdsPublicBaseUrlResolver _baseUrlResolver;

    public CdsCatalogService(
        ICdsServiceHandlerRegistry handlers,
        ICdsServiceInstanceRepository instanceRepository,
        ICdsTrustedIssuerRepository issuerRepository,
        ICdsCallLogRepository callLogRepository,
        ICdsPublicBaseUrlResolver baseUrlResolver)
    {
        _handlers = handlers;
        _instanceRepository = instanceRepository;
        _issuerRepository = issuerRepository;
        _callLogRepository = callLogRepository;
        _baseUrlResolver = baseUrlResolver;
    }

    public IReadOnlyList<CdsHandlerDto> GetHandlers() =>
        _handlers.All.Select(handler => new CdsHandlerDto(
            handler.Key,
            handler.DisplayName,
            handler.Description,
            handler.SupportedHooks,
            handler.Settings,
            handler.SupportedHooks.ToDictionary(
                hook => hook,
                hook => handler.GetPrefetchTemplates(hook),
                StringComparer.Ordinal),
            handler.SupportedFixTypes)).ToArray();

    public async Task<CdsDiscoveryInfoDto> GetDiscoveryInfoAsync(string requestOrigin, CancellationToken cancellationToken)
    {
        var baseUrl = await _baseUrlResolver.ResolveAsync(requestOrigin, cancellationToken);
        var instances = await _instanceRepository.GetAllAsync(cancellationToken);

        return new CdsDiscoveryInfoDto(
            baseUrl.BaseUrl,
            baseUrl.IsConfigured,
            CdsPublicBaseUrl.ConfigurationKey,
            baseUrl.DiscoveryUrl,
            instances
                .OrderBy(x => x.ServiceId, StringComparer.Ordinal)
                .Select(x => new CdsDiscoveryInfoServiceDto(
                    x.Id, x.ServiceId, x.Title, x.Hook, x.IsEnabled, baseUrl.ServiceUrl(x.ServiceId), baseUrl.FeedbackUrl(x.ServiceId)))
                .ToArray());
    }

    public async Task<CdsActivityPageDto> GetActivityAsync(
        CdsCallLogFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var paged = await _callLogRepository.GetPagedAsync(filter, page, pageSize, cancellationToken);

        // Both lists are small admin configuration, so one read each beats a lookup per row.
        var titles = (await _instanceRepository.GetAllAsync(cancellationToken)).ToDictionary(x => x.Id, x => x.Title);
        var issuerNames = (await _issuerRepository.GetAllAsync(cancellationToken)).ToDictionary(x => x.Id, x => x.Name);

        var items = paged.Items.Select(entry => new CdsCallLogDto(
            entry.Id,
            entry.OccurredOnUtc,
            entry.Kind,
            entry.ServiceId,
            entry.ServiceInstanceId,
            entry.ServiceInstanceId is { } instanceId ? titles.GetValueOrDefault(instanceId) : null,
            entry.HandlerKey,
            entry.Hook,
            entry.HookInstance,
            entry.Issuer,
            entry.TrustedIssuerId,
            entry.TrustedIssuerId is { } issuerId ? issuerNames.GetValueOrDefault(issuerId) : null,
            entry.Outcome,
            entry.HttpStatus,
            entry.CardCount,
            entry.DurationMs,
            entry.Detail,
            entry.TriggerAction,
            entry.EpicVersion,
            entry.CriteriaId,
            entry.ImplementationVersion,
            entry.CallbackResourceCount,
            entry.CallbackMs,
            entry.SuggestionsOffered,
            entry.SuggestionsAccepted,
            CdsReasonCodeCounts.Parse(entry.OverrideReasonCodes).Select(reason => reason.Code).ToArray(),
            CdsReasonCodeCounts.Parse(entry.OverrideReasonCodes))).ToArray();

        return new CdsActivityPageDto(items, paged.TotalCount, paged.Page, paged.PageSize);
    }
}
