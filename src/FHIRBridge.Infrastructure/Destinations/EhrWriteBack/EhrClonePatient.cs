using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>
/// QA clone mode: turns a source patient into an obviously synthetic test patient, so an EHR's own records can be
/// written back into the same EHR without touching the real chart. Deterministic per source patient (a re-run builds
/// the same clone), and built so it cannot be mistaken for, or matched to, the original:
/// <list type="bullet">
/// <item>family name prefixed with <see cref="FamilyPrefix"/>;</item>
/// <item>birth date moved back 30 to 209 days;</item>
/// <item>every identifier replaced by one synthetic SSN in the 900–999 area, which the SSA never issues;</item>
/// <item>phone and email dropped, so the clone shares no contact point with the original.</item>
/// </list>
/// The vendor's patient profile then shapes the clone like any other patient.
/// </summary>
public static class EhrClonePatient
{
    public const string FamilyPrefix = "Zztest";

    public const string SsnSystem = "urn:oid:2.16.840.1.113883.4.1";

    public static JsonObject Build(JsonObject sourcePatient, string sourcePatientId)
    {
        var clone = (JsonObject)sourcePatient.DeepClone();
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes("ehr-clone|" + sourcePatientId));

        clone.Remove("id");
        clone.Remove("meta");
        clone.Remove("telecom");
        clone.Remove("link");
        clone.Remove("photo");

        foreach (var name in Objects(clone, "name"))
        {
            var family = String(name, "family");
            if (!string.IsNullOrWhiteSpace(family) && !family.StartsWith(FamilyPrefix, StringComparison.Ordinal))
            {
                name["family"] = FamilyPrefix + family;
            }

            name.Remove("text");
        }

        if (String(clone, "birthDate") is { } birthDate
            && DateOnly.TryParseExact(birthDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var born))
        {
            var shiftDays = 30 + (BitConverter.ToUInt16(seed, 0) % 180);
            clone["birthDate"] = born.AddDays(-shiftDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        clone["identifier"] = new JsonArray(new JsonObject
        {
            ["use"] = "official",
            ["system"] = SsnSystem,
            ["value"] = SyntheticSsn(seed),
        });

        return clone;
    }

    /// <summary>900-999 area, 01-99 group, 0001-9999 serial: valid in shape, never a real SSN.</summary>
    private static string SyntheticSsn(byte[] seed)
    {
        var area = 900 + (seed[2] % 100);
        var group = 1 + (seed[3] % 99);
        var serial = 1 + (BitConverter.ToUInt16(seed, 4) % 9999);
        return string.Create(CultureInfo.InvariantCulture, $"{area:000}-{group:00}-{serial:0000}");
    }
}
