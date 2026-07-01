namespace FHIRBridge.Integration.Hl7v2;

/// <summary>A single HL7 v2 segment (e.g. PID) and its fields.</summary>
public sealed class Hl7v2Segment
{
    private readonly IReadOnlyList<Hl7v2Field> _fields;

    public Hl7v2Segment(string id, IReadOnlyList<Hl7v2Field> fields)
    {
        Id = id;
        _fields = fields;
    }

    public string Id { get; }

    /// <summary>
    /// Returns the field at the given 1-based HL7 position. Internally <c>_fields[0]</c> is the segment id, so field N
    /// sits at index N. The parser injects the field-separator field for header segments (MSH/BHS/FHS) so MSH-1
    /// resolves to the separator, MSH-3 to the third real field, etc. — matching HL7 numbering for all segment types.
    /// </summary>
    public Hl7v2Field Field(int position)
        => position >= 0 && position < _fields.Count ? _fields[position] : Hl7v2Field.Empty;
}

/// <summary>A field value with component (^) and repetition (~) access.</summary>
public sealed class Hl7v2Field
{
    public static Hl7v2Field Empty { get; } = new(string.Empty, Hl7Encoding.Default);

    private readonly Hl7Encoding _encoding;

    public Hl7v2Field(string value, Hl7Encoding encoding)
    {
        Value = value;
        _encoding = encoding;
    }

    /// <summary>The raw field value (first repetition included).</summary>
    public string Value { get; }

    /// <summary>Returns the 1-based component, e.g. PID-5 component 1 = family name. Empty when absent.</summary>
    public string Component(int position)
    {
        var firstRepetition = Value.Split(_encoding.Repetition)[0];
        var components = firstRepetition.Split(_encoding.Component);
        return position >= 1 && position <= components.Length ? components[position - 1] : string.Empty;
    }

    /// <summary>Repetitions of this field (split on ~).</summary>
    public IReadOnlyList<string> Repetitions()
        => Value.Split(_encoding.Repetition, StringSplitOptions.None);
}
