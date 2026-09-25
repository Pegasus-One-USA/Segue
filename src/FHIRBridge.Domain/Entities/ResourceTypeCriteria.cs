using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// The FHIR search criteria to apply when extracting ONE resource type from ONE source node of a workflow —
/// authored per resource type in the destination wizard's "Map fields" step (the per-row "Criteria" button).
/// </summary>
/// <remarks>
/// Replaces the single connection-wide <c>SourceRetrievalConfiguration.SearchCriteria</c> as the authoring
/// surface, which applied the same parameters to every resource type. That one-size-fits-all string is why the
/// runtime has to defensively discard it for resource types it cannot describe (see
/// <c>SourceNodeExecutors.SearchCohortScopedAsync</c>, and the non-patient-compartment branch of its
/// <c>ExecuteAsync</c>): an <c>identifier=&lt;MRN list&gt;</c> that legitimately scopes <c>Patient</c> is
/// meaningless on <c>Condition</c>, and carrying it forward silently returned zero rows. Criteria stored here are
/// explicitly chosen for the one resource type named, so they survive that scrub.
/// <para>
/// The connection-level value remains a fallback: a resource type with no row here still gets it, so existing
/// connections that rely on it (notably athenahealth, which rejects an unscoped <c>Patient</c> search outright)
/// keep working unchanged.
/// </para>
/// <para>
/// <see cref="SourceNodeId"/> is the portal's canvas node key (<c>"source-1"</c>), deliberately NOT a foreign key
/// to <c>WorkflowNodes.Id</c>: <c>SqlWorkflowDefinitionStore.SaveAsync</c> persists a graph by deleting the whole
/// definition and re-inserting it with freshly generated node ids on every save, so an FK would be cascade-deleted
/// — the criteria would silently vanish the next time anyone edited the workflow. The canvas key survives that
/// rebuild. <see cref="WorkflowId"/> is likewise a plain scalar for the same reason.
/// </para>
/// </remarks>
public sealed class ResourceTypeCriteria : AuditableChildEntity<Guid>
{
    private ResourceTypeCriteria()
    {
    }

    public ResourceTypeCriteria(Guid workflowId, string sourceNodeId, string resourceType, string criteria)
    {
        Id = Guid.NewGuid();
        WorkflowId = workflowId;
        SourceNodeId = sourceNodeId.Trim();
        ResourceType = resourceType.Trim();
        Criteria = NormalizeCriteria(criteria);
    }

    /// <summary>The <c>WorkflowDefinitions.Id</c> these criteria belong to.</summary>
    public Guid WorkflowId { get; private set; }

    /// <summary>Canvas node key of the source these criteria filter (e.g. <c>"source-1"</c>) — see the remarks
    /// on this class for why this is the portal's key rather than a <c>WorkflowNodes</c> foreign key.</summary>
    public string SourceNodeId { get; private set; } = default!;

    /// <summary>FHIR resource type the criteria apply to, e.g. <c>Patient</c>, <c>Observation</c>.</summary>
    public string ResourceType { get; private set; } = default!;

    /// <summary>Raw FHIR search parameters, ampersand-separated and stored exactly as authored (never parsed or
    /// validated here) — e.g. <c>birthdate=gt2000-01-01&amp;gender=female</c>. Any parameter the target server
    /// accepts is valid.</summary>
    public string Criteria { get; private set; } = default!;

    /// <summary>
    /// Merges <paramref name="additionalCriteria"/> into the existing value rather than replacing it — the
    /// "Criteria" button appends to whatever is already there. A parameter whose key is already present is
    /// dropped, matching <c>SourceConnectionRuntimeResolver.ComposeSearchParameters</c>: re-sending the same key
    /// twice produces <c>identifier=A&amp;identifier=B</c>, which Epic rejects outright ("Don't support searching
    /// by IDENTIFIER AND IDENTIFIER").
    /// </summary>
    public void AppendCriteria(string additionalCriteria)
    {
        Criteria = MergeCriteria(Criteria, additionalCriteria);
    }

    /// <summary>Replaces the criteria outright — for an edit that removes or rewrites existing parameters, which
    /// an append can never express.</summary>
    public void ReplaceCriteria(string criteria)
    {
        Criteria = NormalizeCriteria(criteria);
    }

    /// <summary>
    /// Appends the parameters of <paramref name="additional"/> to <paramref name="existing"/>, skipping any whose
    /// key already appears. Static so the same merge runs before a row exists (the create path) as after it.
    /// </summary>
    public static string MergeCriteria(string? existing, string? additional)
    {
        var merged = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in new[] { existing, additional })
        {
            foreach (var segment in SplitParameters(source))
            {
                if (seenKeys.Add(ExtractParameterKey(segment)))
                {
                    merged.Add(segment);
                }
            }
        }

        return string.Join('&', merged);
    }

    private static string NormalizeCriteria(string? criteria) =>
        string.Join('&', SplitParameters(criteria));

    private static IEnumerable<string> SplitParameters(string? criteria)
    {
        if (string.IsNullOrWhiteSpace(criteria))
        {
            return [];
        }

        return criteria
            .Trim()
            .TrimStart('?')
            .Trim('&')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ExtractParameterKey(string segment)
    {
        var equals = segment.IndexOf('=');
        return equals < 0 ? segment : segment[..equals];
    }
}
