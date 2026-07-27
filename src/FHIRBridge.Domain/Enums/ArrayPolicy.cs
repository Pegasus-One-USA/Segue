namespace FHIRBridge.Domain.Enums;

/// <summary>
/// How a mapping field that crosses a repeating (max-cardinality "*") FHIR element
/// is materialized into the destination.
/// </summary>
public enum ArrayPolicy
{
    /// <summary>Field is scalar (one-to-one); no array handling needed.</summary>
    Scalar = 0,

    /// <summary>Map only the first array item into a single scalar column.</summary>
    FirstItem = 1,

    /// <summary>Emit one destination row per array item; parent scalar columns repeat.</summary>
    RepeatParent = 2,

    /// <summary>Write array items to a separate child table with a parent foreign key + ordinal index.</summary>
    SeparateDestination = 3,

    /// <summary>Store the whole array as a JSON string in one column.</summary>
    StoreJson = 4,

    /// <summary>Strict: fail validation if more than one value is present.</summary>
    RejectIfMultiple = 5,

    /// <summary>
    /// Pick the array item whose sibling code element (mapping field's <c>CorrelationCodeJsonPath</c>) matches the
    /// field's <c>CorrelationCodeValue</c>, instead of taking items by position. Needed for multi-component
    /// elements like <c>Observation.component[]</c> (blood-pressure systolic/diastolic), where the component
    /// carrying a given value isn't reliably at the same array index across resources.
    /// </summary>
    CorrelateByCode = 6
}
