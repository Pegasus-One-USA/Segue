using System.Text.Json;

namespace HealthAppBackend;

// Flattens a raw FHIR Practitioner JSON payload into the columns persisted on Practitioner11Entity. Mirrors
// PatientFieldExtractor's approach; used by POST /api/v11/practitioners/import when the New 11 workflow returns
// Practitioner resources.
public sealed record PractitionerFields(
    string? PractitionerId,
    string? FullName,
    string? FirstName,
    string? LastName,
    string? Title,
    string? Credential,
    string? Specialty,
    string? Gender,
    string? NPI,
    string? Phone,
    string? Email,
    string? AddressLine,
    string? City,
    string? State,
    string? PostalCode,
    bool? IsActive);

public static class PractitionerFieldExtractor
{
    private const string UsNpiSystem = "http://hl7.org/fhir/sid/us-npi";

    public static PractitionerFields Extract(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;

        var nameEntry = root.TryGetProperty("name", out var nameArr) && nameArr.GetArrayLength() > 0
            ? nameArr[0]
            : (JsonElement?)null;

        var given = nameEntry?.TryGetProperty("given", out var givenArr) == true
            ? givenArr.EnumerateArray().Select(g => g.GetString()).Where(g => !string.IsNullOrWhiteSpace(g)).ToList()
            : [];
        var firstName = given.Count > 0 ? given[0] : null;
        var family = nameEntry.HasValue ? Text(nameEntry.Value, "family") : null;
        var fullNameText = nameEntry.HasValue ? Text(nameEntry.Value, "text") : null;
        var fullName = fullNameText ?? Join(string.Join(' ', given), family);

        var title = nameEntry.HasValue ? FirstOfArray(nameEntry.Value, "prefix") : null;
        var suffix = nameEntry.HasValue ? FirstOfArray(nameEntry.Value, "suffix") : null;

        // qualification[].code.text — credential (e.g. MD) and, lacking a PractitionerRole here, doubles as specialty.
        string? qualificationText = null;
        if (root.TryGetProperty("qualification", out var quals) && quals.GetArrayLength() > 0
            && quals[0].TryGetProperty("code", out var qualCode))
        {
            qualificationText = Text(qualCode, "text");
        }

        string? npi = null;
        if (root.TryGetProperty("identifier", out var ids))
        {
            foreach (var id in ids.EnumerateArray())
            {
                if (Text(id, "system") == UsNpiSystem)
                {
                    npi = Text(id, "value");
                    break;
                }
            }
        }

        string? phone = null, email = null;
        if (root.TryGetProperty("telecom", out var telecom))
        {
            foreach (var entry in telecom.EnumerateArray())
            {
                var system = Text(entry, "system");
                if (system == "phone") { phone ??= Text(entry, "value"); }
                else if (system == "email") { email ??= Text(entry, "value"); }
            }
        }

        var addressEntry = root.TryGetProperty("address", out var addrArr) && addrArr.GetArrayLength() > 0
            ? addrArr[0]
            : (JsonElement?)null;
        var addressLine = addressEntry.HasValue ? FirstOfArray(addressEntry.Value, "line") : null;

        bool? isActive = root.TryGetProperty("active", out var activeProp)
            ? activeProp.ValueKind == JsonValueKind.True
            : null;

        return new PractitionerFields(
            PractitionerId: Text(root, "id"),
            FullName: string.IsNullOrWhiteSpace(fullName) ? null : fullName,
            FirstName: firstName,
            LastName: family,
            Title: title,
            Credential: suffix ?? qualificationText,
            Specialty: qualificationText,
            Gender: Text(root, "gender"),
            NPI: npi,
            Phone: phone,
            Email: email,
            AddressLine: addressLine,
            City: addressEntry.HasValue ? Text(addressEntry.Value, "city") : null,
            State: addressEntry.HasValue ? Text(addressEntry.Value, "state") : null,
            PostalCode: addressEntry.HasValue ? Text(addressEntry.Value, "postalCode") : null,
            IsActive: isActive);
    }

    private static string? Join(string? a, string? b)
    {
        var joined = string.Join(' ', new[] { a, b }.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    private static string? FirstOfArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            ? arr[0].GetString()
            : null;

    private static string? Text(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText()
        };
    }
}
