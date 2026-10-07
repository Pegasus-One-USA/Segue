using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.EhrWrite;

/// <summary>
/// <see cref="IEhrWriteChannel"/> over a vendor connector's <see cref="IFhirWriteClient"/>: translates between the
/// writer's plain Application types and the Runtime connector's, and holds the resolved connection so every call
/// goes out with the same configuration and token.
/// </summary>
public sealed class FhirClientEhrWriteChannel : IEhrWriteChannel
{
    private readonly IFhirWriteClient _client;
    private readonly FhirSourceConfiguration _source;
    private readonly IFhirAccessTokenProvider? _accessTokenProvider;

    public FhirClientEhrWriteChannel(
        IFhirWriteClient client,
        FhirSourceConfiguration source,
        IFhirAccessTokenProvider? accessTokenProvider,
        Guid targetConnectionId,
        SourceSystemType targetVendor,
        Guid? destinationId,
        EhrWriteBackRunOptions options,
        bool vendorWriteApisActivated = false)
    {
        _client = client;
        _source = source;
        _accessTokenProvider = accessTokenProvider;
        TargetConnectionId = targetConnectionId;
        TargetVendor = targetVendor;
        DestinationId = destinationId;
        Options = options;
        VendorWriteApisActivated = vendorWriteApisActivated;
    }

    public Guid TargetConnectionId { get; }

    public SourceSystemType TargetVendor { get; }

    public string TargetBaseUrl => _source.BaseUrl ?? string.Empty;

    public Guid? DestinationId { get; }

    public EhrWriteBackRunOptions Options { get; }

    public bool VendorWriteApisActivated { get; }

    /// <summary>The connector and connection this channel sends over, for a vendor channel that wraps it.</summary>
    internal IFhirWriteClient Client => _client;

    internal FhirSourceConfiguration Source => _source;

    public Task<string?> GetGrantedScopeAsync(CancellationToken cancellationToken) =>
        _accessTokenProvider is IFhirGrantedScopeProvider scopes
            ? scopes.GetGrantedScopeAsync(_source, cancellationToken)
            : Task.FromResult<string?>(null);

    public async Task<EhrSearchOutcome> SearchByIdentifierAsync(
        string resourceType, string system, string value, CancellationToken cancellationToken) =>
        ToOutcome(await _client.SearchByIdentifierAsync(resourceType, system, value, _source, cancellationToken));

    public async Task<EhrSearchOutcome> SearchForPatientAsync(
        string resourceType, string targetPatientId, CancellationToken cancellationToken) =>
        ToOutcome(await _client.SearchForPatientAsync(resourceType, targetPatientId, _source, cancellationToken));

    public async Task<EhrPatientMatchOutcome> MatchPatientAsync(string patientJson, CancellationToken cancellationToken)
    {
        var result = await _client.MatchPatientAsync(patientJson, _source, cancellationToken);
        var kind = result.Kind switch
        {
            FhirPatientMatchKind.Certain => EhrPatientMatchKind.Certain,
            FhirPatientMatchKind.None => EhrPatientMatchKind.None,
            FhirPatientMatchKind.Ambiguous => EhrPatientMatchKind.Ambiguous,
            _ => EhrPatientMatchKind.Failed,
        };
        return new EhrPatientMatchOutcome(kind, result.PatientId, result.StatusCode, ToIssues(result.Issues));
    }

    public async Task<EhrCreateOutcome> CreateAsync(string resourceType, string resourceJson, CancellationToken cancellationToken)
    {
        var result = await _client.CreateAsync(resourceType, resourceJson, returnRepresentation: false, _source, cancellationToken);
        var kind = result.Kind switch
        {
            FhirWriteOutcomeKind.Created => EhrCreateKind.Created,
            FhirWriteOutcomeKind.Rejected => EhrCreateKind.Rejected,
            _ => EhrCreateKind.Unknown,
        };
        return new EhrCreateOutcome(kind, result.StatusCode, result.ResourceId, ToIssues(result.Issues));
    }

    private static EhrSearchOutcome ToOutcome(FhirSearchPage page) =>
        new(page.Succeeded, page.StatusCode, page.Resources, ToIssues(page.Issues));

    private static IReadOnlyList<EhrOutcomeIssue> ToIssues(IReadOnlyList<FhirOperationOutcomeIssue> issues) =>
        issues
            .Select(issue => new EhrOutcomeIssue(
                issue.Severity,
                issue.Code,
                issue.DetailCodes.FirstOrDefault(),
                issue.Expressions.FirstOrDefault()))
            .ToList();
}
