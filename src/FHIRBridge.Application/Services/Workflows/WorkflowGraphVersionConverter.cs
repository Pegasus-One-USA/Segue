using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Application.Services.Workflows;

/// <summary>
/// Decides how a stored V1 graph becomes a V2 one, ahead of V1's builder being deleted
/// (docs/backend/18-workflow-self-contained-config-plan.md §8.4).
///
/// V2 collapsed six granular V1 transform steps — normalize, flatten extensions, data quality, patient
/// matching, terminology, FHIR validation — into ONE "Transformation" step
/// (FhirResourceTransformNode). So conversion is not a rename: several nodes fold into one, and that is a
/// real change in what the graph does, not a cosmetic retype. Everything here is reported before anything is
/// written, and a graph that cannot be converted cleanly is left alone rather than half-migrated.
/// </summary>
public static class WorkflowGraphVersionConverter
{
    /// <summary>
    /// V1-only transform node types, in the order V1 ran them. All of these collapse into a single
    /// FhirResourceTransformNode — which is why a converted graph can have fewer nodes than it started with.
    /// </summary>
    public static readonly IReadOnlyList<string> CollapsibleV1TransformNodeTypes =
    [
        WorkflowNodeTypes.UsCoreValidation,
        WorkflowNodeTypes.Normalization,
        WorkflowNodeTypes.FlattenExtensions,
        WorkflowNodeTypes.DataQualityScoring,
        WorkflowNodeTypes.PatientMatching,
        WorkflowNodeTypes.Terminology,
        WorkflowNodeTypes.TerminologyValidate,
        WorkflowNodeTypes.TerminologyLookup,
        WorkflowNodeTypes.TerminologyTranslate,
        WorkflowNodeTypes.TerminologyExpand,
    ];

    /// <summary>The rank the V2 catalog gives its Transformation step (DefaultWorkflowNodeCatalog).</summary>
    public const int TransformationRank = 34;

    /// <summary>
    /// Whether this graph needs converting: it carries at least one node type V2's builder has no tile for.
    /// A graph already made only of V2 node types needs nothing, whichever builder authored it.
    /// </summary>
    public static bool RequiresConversion(WorkflowDefinition workflow) =>
        workflow.Nodes.Any(node => CollapsibleV1TransformNodeTypes.Contains(node.NodeType));

    /// <summary>The V1-only nodes in this graph, in graph order.</summary>
    public static IReadOnlyList<WorkflowNode> CollapsibleNodes(WorkflowDefinition workflow) =>
        workflow.Nodes
            .Where(node => CollapsibleV1TransformNodeTypes.Contains(node.NodeType))
            .OrderBy(node => node.Rank)
            .ThenBy(node => node.SubRank)
            .ToList();

    /// <summary>
    /// Why this graph cannot be converted automatically, or null when it can.
    ///
    /// The one case deliberately refused: more than one collapsible node carrying its own configuration.
    /// Folding those into a single Transformation node would have to merge configurations that were authored
    /// against different steps, and there is no correct way to decide which wins. Reporting it and leaving the
    /// graph alone is the honest outcome — plan §8.4 says a graph that cannot be cleanly converted is reported,
    /// never half-migrated.
    /// </summary>
    public static string? DescribeBlocker(WorkflowDefinition workflow)
    {
        var configured = CollapsibleNodes(workflow)
            .Where(HasMeaningfulConfiguration)
            .ToList();

        if (configured.Count > 1)
        {
            var names = string.Join(", ", configured.Select(node => $"'{node.DisplayName}' ({node.NodeType})"));
            return $"{configured.Count} configured V1 transform steps ({names}) would collapse into one "
                + "Transformation node, and their configurations cannot be merged automatically. "
                + "Reconfigure this workflow's transformation step by hand, then re-run.";
        }

        return null;
    }

    /// <summary>
    /// Whether a node carries configuration that would be lost by collapsing it. An empty object (or absent
    /// config) is the common case for these steps — they were mostly toggles on the V1 canvas — and collapses
    /// cleanly.
    /// </summary>
    private static bool HasMeaningfulConfiguration(WorkflowNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigurationJson))
        {
            return false;
        }

        var trimmed = node.ConfigurationJson.Trim();
        if (trimmed is "{}" or "null")
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(trimmed);
            var settings = WorkflowNodeConfigurationEnvelope.ResolveSettings(document.RootElement);
            if (settings.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return true;
            }

            // Builder bookkeeping (__transformId, __name, __builderVersion, __vendorId) is not real
            // configuration — a node carrying only those collapses without losing anything a user set.
            return settings.EnumerateObject().Any(property => !property.Name.StartsWith("__", StringComparison.Ordinal));
        }
        catch (System.Text.Json.JsonException)
        {
            // Unparseable config is the migration service's problem to report, not something to collapse past.
            return true;
        }
    }
}
