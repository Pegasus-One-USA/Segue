namespace FHIRBridge.Domain.Enums;

/// <summary>
/// How a <see cref="MappingValueType.Json"/> mapping field's already-serialized JSON text is materialized
/// in the destination column. Only destinations with a native structured type honour anything but
/// <see cref="JsonString"/> — today that is MongoDB, whose BSON documents can nest sub-documents/arrays
/// directly; every relational writer stores JSON as text regardless and so ignores this entirely.
/// </summary>
public enum JsonColumnWriteMode
{
    /// <summary>
    /// The JSON lands in the column as one text value. Default, and the only behaviour that existed before
    /// this option — every mapping saved without an explicit choice keeps it.
    /// </summary>
    JsonString = 0,

    /// <summary>
    /// The JSON text is parsed and written as the destination's own native structured type — a nested BSON
    /// sub-document (or array) for MongoDB — so the destination can index and query inside the value with
    /// dotted paths instead of treating it as an opaque string.
    /// </summary>
    Document = 1,
}
