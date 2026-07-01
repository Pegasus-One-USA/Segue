namespace FHIRBridge.Runtime.Application.Pipeline;

/// <summary>Builds pipeline DAGs. The default graph reproduces the canonical Extract→Govern→Transform→Output flow.</summary>
public static class PipelineGraphFactory
{
    public const string Extraction = "Extraction";
    public const string Governance = "Governance";
    public const string Transform = "Transform";
    public const string Output = "Output";

    public static PipelineGraph CreateDefault() => new(
    [
        new PipelineStageDefinition(Extraction),
        new PipelineStageDefinition(Governance, Extraction),
        new PipelineStageDefinition(Transform, Governance),
        new PipelineStageDefinition(Output, Transform)
    ]);
}
