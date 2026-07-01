namespace FHIRBridge.Integration.Hl7v2;

/// <summary>Parses raw HL7 v2 pipe-delimited text into a navigable <see cref="Hl7v2Message"/>.</summary>
public static class Hl7v2Parser
{
    private static readonly char[] SegmentSeparators = ['\r', '\n'];
    private static readonly HashSet<string> HeaderSegments = new(StringComparer.OrdinalIgnoreCase) { "MSH", "BHS", "FHS" };

    public static Hl7v2Message Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("HL7 v2 message is empty.");
        }

        var lines = raw.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("MSH", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HL7 v2 message must begin with an MSH segment.");
        }

        var encoding = ReadEncoding(lines[0]);
        var segments = new List<Hl7v2Segment>(lines.Length);

        foreach (var line in lines)
        {
            if (line.Length < 3)
            {
                continue;
            }

            var id = line[..3];
            var isHeader = HeaderSegments.Contains(id);
            var rawFields = line.Split(encoding.Field);

            var fields = new List<Hl7v2Field>(rawFields.Length);
            foreach (var rawField in rawFields)
            {
                fields.Add(new Hl7v2Field(rawField, encoding));
            }

            // For header segments, HL7 treats the separator char as field 1. Insert it so positions line up.
            if (isHeader)
            {
                fields.Insert(1, new Hl7v2Field(encoding.Field.ToString(), encoding));
            }

            segments.Add(new Hl7v2Segment(id, fields));
        }

        return new Hl7v2Message(segments, encoding);
    }

    private static Hl7Encoding ReadEncoding(string mshLine)
    {
        // MSH|^~\&|...  → MSH[3] is the field separator, MSH[4..] the encoding characters.
        var fieldSeparator = mshLine.Length > 3 ? mshLine[3] : '|';
        var encodingChars = mshLine.Length > 4
            ? mshLine[4..].Split(fieldSeparator)[0]
            : "^~\\&";

        char At(int index, char fallback) => index < encodingChars.Length ? encodingChars[index] : fallback;

        return new Hl7Encoding(
            Field: fieldSeparator,
            Component: At(0, '^'),
            Repetition: At(1, '~'),
            Escape: At(2, '\\'),
            SubComponent: At(3, '&'));
    }
}
