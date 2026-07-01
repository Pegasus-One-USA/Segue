using System.Text;

namespace FHIRBridge.Integration.Hl7v2;

/// <summary>
/// Minimal Lower Layer Protocol (MLLP) helpers: framing constants, frame extraction, and ACK construction. MLLP wraps
/// each HL7 message between a start block (VT, 0x0B) and an end block (FS + CR, 0x1C 0x0D).
/// </summary>
public static class Hl7MllpProtocol
{
    public const byte StartBlock = 0x0B; // VT
    public const byte EndBlock1 = 0x1C;  // FS
    public const byte EndBlock2 = 0x0D;  // CR

    /// <summary>Wraps an HL7 message in MLLP framing bytes.</summary>
    public static byte[] Frame(string message)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        var framed = new byte[payload.Length + 3];
        framed[0] = StartBlock;
        Array.Copy(payload, 0, framed, 1, payload.Length);
        framed[^2] = EndBlock1;
        framed[^1] = EndBlock2;
        return framed;
    }

    /// <summary>
    /// Extracts the HL7 message text from a complete MLLP frame buffer, tolerating a missing start block.
    /// Returns null if the end block has not yet been received (caller should keep reading).
    /// </summary>
    public static string? TryExtractMessage(ReadOnlySpan<byte> buffer)
    {
        var start = 0;
        if (buffer.Length > 0 && buffer[0] == StartBlock)
        {
            start = 1;
        }

        for (var i = start; i < buffer.Length - 1; i++)
        {
            if (buffer[i] == EndBlock1 && buffer[i + 1] == EndBlock2)
            {
                return Encoding.UTF8.GetString(buffer[start..i]);
            }
        }

        return null;
    }

    /// <summary>True when the buffer contains a complete MLLP end block.</summary>
    public static bool ContainsCompleteFrame(ReadOnlySpan<byte> buffer)
    {
        for (var i = 0; i < buffer.Length - 1; i++)
        {
            if (buffer[i] == EndBlock1 && buffer[i + 1] == EndBlock2)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds an HL7 v2 ACK message responding to <paramref name="inbound"/>. <paramref name="accepted"/> selects an
    /// AA (application accept) vs AE (application error) acknowledgement code; <paramref name="errorText"/> is included
    /// in the MSA for AE responses.
    /// </summary>
    public static string BuildAck(Hl7v2Message inbound, bool accepted, string? errorText = null)
    {
        var encoding = inbound.Encoding;
        var sep = encoding.Field;
        var msh = inbound.Segment("MSH");

        // Echo back with sender/receiver swapped per HL7 ACK convention.
        var sendingApp = msh?.Field(5).Value ?? string.Empty;   // their receiving app becomes our sending app
        var sendingFacility = msh?.Field(6).Value ?? string.Empty;
        var receivingApp = msh?.Field(3).Value ?? "FHIRBridge";
        var receivingFacility = msh?.Field(4).Value ?? string.Empty;
        var version = msh?.Field(12).Value ?? "2.5";
        var controlId = inbound.MessageControlId;
        var ackCode = accepted ? "AA" : "AE";

        var mshSegment = string.Join(sep,
            "MSH",
            $"{encoding.Component}{encoding.Repetition}{encoding.Escape}{encoding.SubComponent}",
            sendingApp,
            sendingFacility,
            receivingApp,
            receivingFacility,
            string.Empty, // timestamp left blank (deterministic output)
            string.Empty,
            $"ACK{encoding.Component}{inbound.TriggerEvent}",
            $"{controlId}-ACK",
            "P",
            version);

        var msaSegment = accepted || string.IsNullOrWhiteSpace(errorText)
            ? string.Join(sep, "MSA", ackCode, controlId)
            : string.Join(sep, "MSA", ackCode, controlId, errorText.Replace(sep, ' '));

        return mshSegment + "\r" + msaSegment;
    }
}
