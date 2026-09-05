namespace FHIRBridge.Runtime.Application.Abstractions.Connectors;

/// <summary>
/// Reports extraction that completed but returned LESS than the source holds. This is deliberately separate from
/// <c>SearchAsync</c>'s return value: a connector can lose data without failing the call — one category of a
/// fanned-out search being rejected, or a page cap cutting a paged fetch short — and the resource list alone can
/// never show it. The list looks the same whether it is all the data or a third of it, which is how a run that
/// extracted 28 of 55 Observations finished as <c>Succeeded</c> with nothing anywhere saying otherwise.
/// <para>
/// A source node executor drains this after fetching and folds the reasons into the same
/// <c>skippedResourceTypes</c> metadata that scope-skips and destination write failures use, so the run reports
/// <c>WorkflowRunStatus.PartialSuccess</c> instead of a clean success. Implemented by the connector rather than
/// returned from the call so that neither <c>IFhirSourceClient</c> nor its four callers change shape.
/// </para>
/// </summary>
public interface IResourceExtractionDiagnostics
{
    /// <summary>
    /// Returns the reasons accumulated since the last drain, and clears them. Draining (rather than exposing a
    /// growing list) keeps a connector instance reused across nodes from reporting the same loss twice.
    /// </summary>
    IReadOnlyList<string> DrainIncompleteReasons();
}
