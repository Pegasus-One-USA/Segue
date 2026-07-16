using System.Text.Json;

namespace HealthAppBackend;

// Flattens a raw FHIR Patient JSON payload into the columns persisted on PatientEntity. Called once at
// ingestion time (POST /api/workflow/run) so reads never need to re-parse the JSON payload.
public sealed record PatientFields(
    string? FullName,
    string? FirstName,
    string? MiddleName,
    string? LastName,
    string? Gender,
    string? LegalSex,
    string? SexForClinicalUse,
    string? Pronouns,
    string? BirthDate,
    string? MaritalStatus,
    string? PatientStatus,
    string? Deceased,
    string? UsCoreSex,
    string? Race,
    string? Ethnicity,
    string? Address,
    string? City,
    string? State,
    string? PostalCode,
    string? Country,
    string? HomePhone,
    string? MobilePhone,
    string? Email,
    string? PreferredLanguage,
    string? GeneralPractitioner,
    string? ManagingOrganization);

public static class PatientFieldExtractor
{
    public static PatientFields Extract(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;

        var nameEntry = root.TryGetProperty("name", out var nameArr) && nameArr.GetArrayLength() > 0
            ? nameArr[0]
            : (JsonElement?)null;
        var givenNameList = nameEntry?.TryGetProperty("given", out var givenArr) == true
            ? givenArr.EnumerateArray().Select(g => g.GetString()).ToList()
            : [];
        var firstName = givenNameList.Count > 0 ? givenNameList[0] : null;
        var middleName = givenNameList.Count > 1 ? string.Join(" ", givenNameList.Skip(1)) : null;

        var addressEntry = root.TryGetProperty("address", out var addressArr) && addressArr.GetArrayLength() > 0
            ? addressArr[0]
            : (JsonElement?)null;

        string? homePhone = null, mobilePhone = null, email = null;
        if (root.TryGetProperty("telecom", out var telecomArr))
        {
            foreach (var entry in telecomArr.EnumerateArray())
            {
                var system = Text(entry, "system");
                var use = Text(entry, "use");
                var value = Text(entry, "value");

                if (system == "phone" && use == "mobile")
                {
                    mobilePhone ??= value;
                }
                else if (system == "phone")
                {
                    homePhone ??= value;
                }
                else if (system == "email")
                {
                    email ??= value;
                }
            }
        }

        string? preferredLanguage = null;
        if (root.TryGetProperty("communication", out var commArr))
        {
            foreach (var entry in commArr.EnumerateArray())
            {
                var isPreferred = entry.TryGetProperty("preferred", out var preferredProp) && preferredProp.ValueKind == JsonValueKind.True;
                if (preferredLanguage is null || isPreferred)
                {
                    preferredLanguage = entry.TryGetProperty("language", out var lang) ? Text(lang, "text") : null;
                    if (isPreferred)
                    {
                        break;
                    }
                }
            }
        }

        var generalPractitioner = root.TryGetProperty("generalPractitioner", out var gpArr) && gpArr.GetArrayLength() > 0
            ? Text(gpArr[0], "display")
            : null;
        var managingOrganization = root.TryGetProperty("managingOrganization", out var org) ? Text(org, "display") : null;
        var maritalStatus = root.TryGetProperty("maritalStatus", out var marital) ? Text(marital, "text") : null;

        var isActive = root.TryGetProperty("active", out var activeProp) && activeProp.ValueKind == JsonValueKind.True;
        var patientStatus = root.TryGetProperty("active", out _) ? (isActive ? "Active" : "Inactive") : null;

        var isDeceased = root.TryGetProperty("deceasedBoolean", out var deceasedProp) && deceasedProp.ValueKind == JsonValueKind.True;
        var deceased = root.TryGetProperty("deceasedBoolean", out _) ? (isDeceased ? "Yes" : "No") : null;

        return new PatientFields(
            FullName: nameEntry.HasValue ? Text(nameEntry.Value, "text") : null,
            FirstName: firstName,
            MiddleName: middleName,
            LastName: nameEntry.HasValue ? Text(nameEntry.Value, "family") : null,
            Gender: Text(root, "gender"),
            LegalSex: FindExtensionValue(root, "http://open.epic.com/FHIR/StructureDefinition/extension/legal-sex"),
            SexForClinicalUse: FindExtensionValue(root, "http://open.epic.com/FHIR/StructureDefinition/extension/sex-for-clinical-use"),
            Pronouns: FindExtensionValue(root, "http://open.epic.com/FHIR/StructureDefinition/extension/calculated-pronouns-to-use-for-text"),
            BirthDate: Text(root, "birthDate"),
            MaritalStatus: maritalStatus,
            PatientStatus: patientStatus,
            Deceased: deceased,
            UsCoreSex: FindExtensionValue(root, "http://hl7.org/fhir/us/core/StructureDefinition/us-core-sex"),
            Race: FindExtensionValue(root, "http://hl7.org/fhir/us/core/StructureDefinition/us-core-race"),
            Ethnicity: FindExtensionValue(root, "http://hl7.org/fhir/us/core/StructureDefinition/us-core-ethnicity"),
            Address: addressEntry.HasValue ? Text(addressEntry.Value, "text") : null,
            City: addressEntry.HasValue ? Text(addressEntry.Value, "city") : null,
            State: addressEntry.HasValue ? Text(addressEntry.Value, "state") : null,
            PostalCode: addressEntry.HasValue ? Text(addressEntry.Value, "postalCode") : null,
            Country: addressEntry.HasValue ? Text(addressEntry.Value, "country") : null,
            HomePhone: homePhone,
            MobilePhone: mobilePhone,
            Email: email,
            PreferredLanguage: preferredLanguage,
            GeneralPractitioner: generalPractitioner,
            ManagingOrganization: managingOrganization);
    }

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

    // Reads a top-level FHIR extension by URL, preferring a nested "text" sub-extension (US Core race/ethnicity
    // shape), then valueCodeableConcept.text (legal-sex shape), then valueString/valueCode (simple shapes).
    private static string? FindExtensionValue(JsonElement root, string url)
    {
        if (!root.TryGetProperty("extension", out var extensions))
        {
            return null;
        }

        foreach (var extension in extensions.EnumerateArray())
        {
            if (!extension.TryGetProperty("url", out var extUrl) || extUrl.GetString() != url)
            {
                continue;
            }

            if (extension.TryGetProperty("extension", out var nested))
            {
                foreach (var child in nested.EnumerateArray())
                {
                    if (child.TryGetProperty("url", out var childUrl) && childUrl.GetString() == "text"
                        && child.TryGetProperty("valueString", out var nestedText))
                    {
                        return nestedText.GetString();
                    }
                }
            }

            if (extension.TryGetProperty("valueCodeableConcept", out var codeableConcept)
                && codeableConcept.TryGetProperty("text", out var conceptText))
            {
                return conceptText.GetString();
            }

            if (extension.TryGetProperty("valueString", out var valueString))
            {
                return valueString.GetString();
            }

            if (extension.TryGetProperty("valueCode", out var valueCode))
            {
                return valueCode.GetString();
            }

            return null;
        }

        return null;
    }
}
