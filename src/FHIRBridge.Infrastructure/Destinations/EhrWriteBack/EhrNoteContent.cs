using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>
/// Brings a note's text into the note itself. Most EHRs (eClinicalWorks, Epic) serve a DocumentReference whose
/// attachment is a <c>url</c> to a <c>Binary</c>, while the EHR APIs that file notes take the text inline as
/// base64 <c>data</c>. The Binary is read from the run's own source, through the same connection that read the note,
/// and only when the url points at that source: a link to anywhere else is never followed.
/// </summary>
internal static partial class EhrNoteContent
{
    /// <summary>
    /// Returns the note with its first attachment's content inline, or a skip/reject reason. A note that already
    /// carries <c>data</c>, or has no attachment url, is returned unchanged for the write profile to judge. The source
    /// note is never modified: a copy is returned when content is fetched.
    /// </summary>
    /// <param name="exportedBinaries">Binaries the source delivered in the same batch, by id: a bulk export's own Binary
    /// file. Used first, because a bulk-only app's token is refused on a plain <c>Binary/{id}</c> read.</param>
    public static async Task<(JsonObject Note, string? Reason)> InlineAsync(
        JsonObject note,
        string? sourceBaseUrl,
        IReadOnlyDictionary<string, JsonObject> exportedBinaries,
        Func<string, string, CancellationToken, Task<string?>>? fetchFromSource,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var attachment = FirstAttachment(note);
        var url = String(attachment, "url");
        if (attachment is null || !string.IsNullOrWhiteSpace(String(attachment, "data")) || string.IsNullOrWhiteSpace(url))
        {
            return (note, null);
        }

        // A CSV / SQL Table source has no server to read from and exports no Binaries; its rows must carry the text.
        if (fetchFromSource is null && exportedBinaries.Count == 0)
        {
            return (note, "note-content-not-inline");
        }

        var binaryId = BinaryIdOnSource(url, sourceBaseUrl);
        if (binaryId is null)
        {
            return (note, "note-content-url-not-on-source");
        }

        JsonObject? binary;
        if (exportedBinaries.TryGetValue(binaryId, out var exported))
        {
            binary = exported;
        }
        else if (fetchFromSource is null)
        {
            return (note, "note-content-not-found");
        }
        else
        {
            string? binaryJson;
            try
            {
                binaryJson = await fetchFromSource("Binary", binaryId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The source's failure message carries the status, url and the server's error body (an
                // OperationOutcome or auth error, never note content). The note waits for a later run.
                logger.LogWarning(
                    "Reading note content Binary/{BinaryId} from the source failed; the note is skipped this run. {Failure}",
                    binaryId, exception.Message);
                return (note, "note-content-fetch-failed");
            }

            binary = Parse(binaryJson);
        }

        var data = String(binary, "data");
        if (binary is null || String(binary, "resourceType") != "Binary" || string.IsNullOrWhiteSpace(data))
        {
            return (note, "note-content-not-found");
        }

        var copy = note.DeepClone().AsObject();
        var inlined = FirstAttachment(copy)!;
        inlined["data"] = data;
        inlined.Remove("url");

        // The Binary's own content type describes the bytes it holds; the attachment's is only a claim about them.
        if (String(binary, "contentType") is { Length: > 0 } contentType)
        {
            inlined["contentType"] = contentType;
        }

        return (copy, null);
    }

    /// <summary>The first attachment, the one a write profile files.</summary>
    private static JsonObject? FirstAttachment(JsonObject note) =>
        Objects(note, "content").Select(c => Object(c, "attachment")).FirstOrDefault(a => a is not null);

    /// <summary>
    /// The Binary id when <paramref name="url"/> is <c>Binary/{id}</c>, relative or absolute under the source's base
    /// URL; null for anything else (another server, another resource type, a query or a fragment).
    /// </summary>
    internal static string? BinaryIdOnSource(string url, string? sourceBaseUrl)
    {
        var trimmed = url.Trim();
        string relative;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            if (string.IsNullOrWhiteSpace(sourceBaseUrl) || !string.IsNullOrEmpty(absolute.Query) || !string.IsNullOrEmpty(absolute.Fragment))
            {
                return null;
            }

            var normalizedUrl = EhrWriteKeys.NormalizeBaseUrl(absolute.GetLeftPart(UriPartial.Path));
            var prefix = EhrWriteKeys.NormalizeBaseUrl(sourceBaseUrl) + "/";
            if (!normalizedUrl.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            relative = normalizedUrl[prefix.Length..];
        }
        else
        {
            relative = trimmed.TrimStart('/');
        }

        var match = BinaryReference().Match(relative);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A FHIR id: letters, digits, '-' and '.', at most 64 characters.
    [GeneratedRegex(@"^Binary/(?<id>[A-Za-z0-9\-\.]{1,64})$")]
    private static partial Regex BinaryReference();
}
