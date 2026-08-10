namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// Config keys the caller (TransformationRuleService/MappingNodeExecutor) injects fresh on every execution —
/// never persisted in a <see cref="Domain.Entities.TransformationRule.ConfigJson"/>, never editable in the
/// Rules UI. These exist so a node can adapt to per-record context (which destination it's writing to, which
/// patient this record belongs to) without widening <see cref="ITransformNode.Execute"/>'s signature for
/// every node that doesn't need it.
/// </summary>
public static class ReservedTransformConfigKeys
{
    /// <summary>The real <see cref="Domain.Enums.DestinationType"/> this rule is resolving for, as its
    /// string name (e.g. "FhirRepository", "SqlServer") — lets a node emit a FHIR-shaped structure only for
    /// FHIR-native destinations and a flat scalar everywhere else, automatically.</summary>
    public const string DestinationType = "_destinationType";

    /// <summary>The Patient resource id this row belongs to — only populated for Patient rows, since deriving
    /// it for any other resource type would need reference resolution the caller doesn't do. See
    /// <see cref="Nodes.DateMathAgeNode"/>'s seeded date-shift.</summary>
    public const string PatientId = "_patientId";

    /// <summary>The display text the SOURCE resource's own JSON already carried alongside this code (e.g.
    /// Epic's own <c>Coding.display</c>, sibling to the <c>Coding.code</c> a rule's source field points at) —
    /// only populated by the Runtime pipeline (it needs the whole resource JSON, which
    /// <c>TransformationRuleService.PreviewAsync</c> never has, only a bare sample value), and only for a
    /// source field ending in "code". <see cref="Nodes.CodeableConceptBuilderNode"/> uses it as the fallback
    /// tier between "resolved from the local terminology DB" and "the bare code itself."</summary>
    public const string SourceDisplayHint = "_sourceDisplayHint";
}
