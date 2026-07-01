namespace FHIRBridge.Application.Services;

/// <summary>
/// Controls the synchronous patient-aggregation read (<c>GET .../fhirbridge/Patient/{id}</c>). Bound from the
/// "PatientAggregation" configuration section; defaults below apply when unbound.
/// </summary>
public sealed class PatientAggregationOptions
{
    public const string SectionName = "PatientAggregation";

    /// <summary>
    /// Maximum number of resource-type queries (Patient root + each compartment type) issued to the source in
    /// parallel. Mirrors the pipeline's extraction parallelism cap. Default 4.
    /// </summary>
    public int MaxQueryParallelism { get; set; } = 4;

    /// <summary>Page size (<c>_count</c>) requested from the source per query. Default 100.</summary>
    public int SearchCount { get; set; } = 100;

    /// <summary>Maximum number of pages followed per resource type before stopping. Default 5.</summary>
    public int MaxPages { get; set; } = 5;

    public static PatientAggregationOptions Default { get; } = new();
}
