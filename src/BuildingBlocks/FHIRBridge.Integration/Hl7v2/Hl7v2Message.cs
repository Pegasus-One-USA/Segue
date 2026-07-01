namespace FHIRBridge.Integration.Hl7v2;

/// <summary>
/// A parsed HL7 v2 message: an ordered list of segments. Provides convenient field/component access using HL7's
/// 1-based field numbering (MSH is special-cased so MSH-1 is the field separator and MSH-2 the encoding chars).
/// </summary>
public sealed class Hl7v2Message
{
    private readonly IReadOnlyList<Hl7v2Segment> _segments;

    public Hl7v2Message(IReadOnlyList<Hl7v2Segment> segments, Hl7Encoding encoding)
    {
        _segments = segments;
        Encoding = encoding;
    }

    public Hl7Encoding Encoding { get; }

    public IReadOnlyList<Hl7v2Segment> Segments => _segments;

    /// <summary>MSH-9 message type, e.g. "ADT^A01" → "ADT". Empty when absent.</summary>
    public string MessageType => Segment("MSH")?.Field(9).Component(1) ?? string.Empty;

    /// <summary>MSH-9 trigger event, e.g. "ADT^A01" → "A01". Empty when absent.</summary>
    public string TriggerEvent => Segment("MSH")?.Field(9).Component(2) ?? string.Empty;

    /// <summary>MSH-10 message control id used for the ACK. Empty when absent.</summary>
    public string MessageControlId => Segment("MSH")?.Field(10).Value ?? string.Empty;

    /// <summary>First segment with the given 3-letter id (e.g. "PID"), or null.</summary>
    public Hl7v2Segment? Segment(string segmentId)
        => _segments.FirstOrDefault(s => string.Equals(s.Id, segmentId, StringComparison.OrdinalIgnoreCase));

    /// <summary>All segments with the given id (e.g. every "OBX").</summary>
    public IEnumerable<Hl7v2Segment> SegmentsOf(string segmentId)
        => _segments.Where(s => string.Equals(s.Id, segmentId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Delimiters parsed from MSH-1/MSH-2.</summary>
public sealed record Hl7Encoding(char Field, char Component, char Repetition, char Escape, char SubComponent)
{
    public static Hl7Encoding Default { get; } = new('|', '^', '~', '\\', '&');
}
