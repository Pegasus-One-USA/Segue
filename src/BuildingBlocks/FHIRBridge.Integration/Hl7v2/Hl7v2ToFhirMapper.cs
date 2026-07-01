using System.Text;
using System.Text.Json;

namespace FHIRBridge.Integration.Hl7v2;

/// <summary>
/// Maps a parsed HL7 v2 message (ADT/ORU/MDM) into a FHIR R4 <c>Bundle</c> JSON document so it can flow through the
/// same ingestion/pipeline path as native FHIR payloads. PID becomes a Patient; ORU OBX segments become Observations.
/// </summary>
public static class Hl7v2ToFhirMapper
{
    public static string MapToFhirBundleJson(Hl7v2Message message)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("resourceType", "Bundle");
            writer.WriteString("type", "collection");
            writer.WritePropertyName("entry");
            writer.WriteStartArray();

            var patientId = WritePatientEntry(writer, message);

            if (string.Equals(message.MessageType, "ORU", StringComparison.OrdinalIgnoreCase))
            {
                WriteObservationEntries(writer, message, patientId);
            }
            else if (string.Equals(message.MessageType, "MDM", StringComparison.OrdinalIgnoreCase))
            {
                WriteDocumentReferenceEntry(writer, message, patientId);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string WritePatientEntry(Utf8JsonWriter writer, Hl7v2Message message)
    {
        var pid = message.Segment("PID");
        var mrn = pid?.Field(3).Component(1) ?? string.Empty;
        var patientId = string.IsNullOrWhiteSpace(mrn) ? "unknown" : mrn;

        writer.WriteStartObject();
        writer.WritePropertyName("resource");
        writer.WriteStartObject();
        writer.WriteString("resourceType", "Patient");
        writer.WriteString("id", patientId);

        if (!string.IsNullOrWhiteSpace(mrn))
        {
            writer.WritePropertyName("identifier");
            writer.WriteStartArray();
            writer.WriteStartObject();
            var assigningAuthority = pid?.Field(3).Component(4);
            if (!string.IsNullOrWhiteSpace(assigningAuthority))
            {
                writer.WriteString("system", $"urn:hl7v2:{assigningAuthority}");
            }

            writer.WriteString("value", mrn);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        var family = pid?.Field(5).Component(1);
        var given = pid?.Field(5).Component(2);
        if (!string.IsNullOrWhiteSpace(family) || !string.IsNullOrWhiteSpace(given))
        {
            writer.WritePropertyName("name");
            writer.WriteStartArray();
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(family))
            {
                writer.WriteString("family", family);
            }

            if (!string.IsNullOrWhiteSpace(given))
            {
                writer.WritePropertyName("given");
                writer.WriteStartArray();
                writer.WriteStringValue(given);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        var gender = MapGender(pid?.Field(8).Value);
        if (gender is not null)
        {
            writer.WriteString("gender", gender);
        }

        var birthDate = MapDate(pid?.Field(7).Value);
        if (birthDate is not null)
        {
            writer.WriteString("birthDate", birthDate);
        }

        writer.WriteEndObject(); // resource
        writer.WriteEndObject(); // entry

        return patientId;
    }

    private static void WriteObservationEntries(Utf8JsonWriter writer, Hl7v2Message message, string patientId)
    {
        var index = 0;
        foreach (var obx in message.SegmentsOf("OBX"))
        {
            index++;
            var code = obx.Field(3).Component(1);
            var display = obx.Field(3).Component(2);
            var value = obx.Field(5).Value;
            var units = obx.Field(6).Component(1);
            var status = MapObservationStatus(obx.Field(11).Value);

            writer.WriteStartObject();
            writer.WritePropertyName("resource");
            writer.WriteStartObject();
            writer.WriteString("resourceType", "Observation");
            writer.WriteString("id", $"{patientId}-obs-{index}");
            writer.WriteString("status", status);

            if (!string.IsNullOrWhiteSpace(code))
            {
                writer.WritePropertyName("code");
                writer.WriteStartObject();
                writer.WritePropertyName("coding");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("code", code);
                if (!string.IsNullOrWhiteSpace(display))
                {
                    writer.WriteString("display", display);
                }

                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WritePropertyName("subject");
            writer.WriteStartObject();
            writer.WriteString("reference", $"Patient/{patientId}");
            writer.WriteEndObject();

            if (!string.IsNullOrWhiteSpace(value))
            {
                if (decimal.TryParse(value, out var numeric))
                {
                    writer.WritePropertyName("valueQuantity");
                    writer.WriteStartObject();
                    writer.WriteNumber("value", numeric);
                    if (!string.IsNullOrWhiteSpace(units))
                    {
                        writer.WriteString("unit", units);
                    }

                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteString("valueString", value);
                }
            }

            writer.WriteEndObject(); // resource
            writer.WriteEndObject(); // entry
        }
    }

    // MDM messages: TXA carries the document header, OBX segments carry the document body text.
    private static void WriteDocumentReferenceEntry(Utf8JsonWriter writer, Hl7v2Message message, string patientId)
    {
        var txa = message.Segment("TXA");
        var documentType = txa?.Field(2).Component(1);
        var documentDate = MapDateTime(txa?.Field(4).Value);
        var uniqueDocumentId = txa?.Field(12).Component(1);
        var status = MapDocumentStatus(txa?.Field(17).Value);

        var body = string.Join(
            "\n",
            message.SegmentsOf("OBX").Select(obx => obx.Field(5).Value).Where(v => !string.IsNullOrWhiteSpace(v)));

        writer.WriteStartObject();
        writer.WritePropertyName("resource");
        writer.WriteStartObject();
        writer.WriteString("resourceType", "DocumentReference");
        writer.WriteString("id", string.IsNullOrWhiteSpace(uniqueDocumentId) ? $"{patientId}-doc" : uniqueDocumentId);
        writer.WriteString("status", status);

        if (!string.IsNullOrWhiteSpace(uniqueDocumentId))
        {
            writer.WritePropertyName("masterIdentifier");
            writer.WriteStartObject();
            writer.WriteString("value", uniqueDocumentId);
            writer.WriteEndObject();
        }

        if (!string.IsNullOrWhiteSpace(documentType))
        {
            writer.WritePropertyName("type");
            writer.WriteStartObject();
            writer.WritePropertyName("coding");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("code", documentType);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WritePropertyName("subject");
        writer.WriteStartObject();
        writer.WriteString("reference", $"Patient/{patientId}");
        writer.WriteEndObject();

        if (documentDate is not null)
        {
            writer.WriteString("date", documentDate);
        }

        writer.WritePropertyName("content");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WritePropertyName("attachment");
        writer.WriteStartObject();
        writer.WriteString("contentType", "text/plain");
        if (!string.IsNullOrWhiteSpace(body))
        {
            writer.WriteString("data", Convert.ToBase64String(Encoding.UTF8.GetBytes(body)));
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();

        writer.WriteEndObject(); // resource
        writer.WriteEndObject(); // entry
    }

    private static string MapDocumentStatus(string? txa17) => txa17?.Trim().ToUpperInvariant() switch
    {
        "CA" => "entered-in-error", // cancelled
        _ => "current"
    };

    private static string? MapDateTime(string? hl7DateTime)
    {
        var date = MapDate(hl7DateTime);
        if (date is null)
        {
            return null;
        }

        // HL7 timestamps are YYYYMMDDHHMMSS; surface a FHIR dateTime when a time component is present.
        if (hl7DateTime is { Length: >= 14 })
        {
            var t = hl7DateTime;
            return $"{date}T{t[8..10]}:{t[10..12]}:{t[12..14]}Z";
        }

        return date;
    }

    private static string? MapGender(string? hl7Gender) => hl7Gender?.Trim().ToUpperInvariant() switch
    {
        "M" => "male",
        "F" => "female",
        "O" => "other",
        "A" or "N" or "U" => "unknown",
        _ => null
    };

    private static string? MapDate(string? hl7Date)
    {
        if (string.IsNullOrWhiteSpace(hl7Date) || hl7Date.Length < 8)
        {
            return null;
        }

        var datePart = hl7Date[..8];
        return $"{datePart[..4]}-{datePart[4..6]}-{datePart[6..8]}";
    }

    private static string MapObservationStatus(string? hl7Status) => hl7Status?.Trim().ToUpperInvariant() switch
    {
        "F" => "final",
        "C" => "corrected",
        "P" => "preliminary",
        "X" => "cancelled",
        _ => "unknown"
    };
}
