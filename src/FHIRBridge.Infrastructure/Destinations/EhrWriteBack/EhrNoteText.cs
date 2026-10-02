using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>
/// Converts a clinical note to the plain text an EHR's note API accepts (Epic files only base64 text/plain). Keeps
/// what a reader needs — paragraphs, line breaks, list items, table rows — and drops markup, styling, embedded
/// pictures and RTF header tables. Never logs the text.
/// </summary>
public static partial class EhrNoteText
{
    /// <summary>Content types this class can turn into plain text, lower-case, without parameters.</summary>
    public static bool CanConvert(string? contentType) =>
        contentType is "text/plain" or "text/html" or "application/xhtml+xml" or "text/rtf" or "application/rtf";

    /// <summary>The note as plain text, or null when the content type is not one <see cref="CanConvert"/> takes.</summary>
    public static string? ToPlainText(string contentType, string text) => contentType switch
    {
        "text/plain" => Normalize(text),
        "text/html" or "application/xhtml+xml" => FromHtml(text),
        "text/rtf" or "application/rtf" => FromRtf(text),
        _ => null,
    };

    public static string FromHtml(string html)
    {
        var text = HiddenBlocks().Replace(html, string.Empty);
        text = Comments().Replace(text, string.Empty);
        text = LineBreaks().Replace(text, "\n");
        text = BlockEnds().Replace(text, "\n");
        text = ListItems().Replace(text, "\n- ");
        text = CellEnds().Replace(text, "\t");
        text = Tags().Replace(text, string.Empty);
        return Normalize(WebUtility.HtmlDecode(text));
    }

    /// <summary>
    /// A small RTF reader: text, paragraph and line breaks, tabs, <c>\'hh</c> (Windows-1252) and <c>\uN</c>
    /// escapes. Destination groups (font and colour tables, stylesheets, document info, pictures, headers and
    /// anything marked <c>\*</c>) are skipped whole.
    /// </summary>
    public static string FromRtf(string rtf)
    {
        var output = new StringBuilder(rtf.Length);
        var skipDepth = new Stack<bool>();
        var skipping = false;
        var unicodeSkip = 1;
        var pendingSkip = 0;
        var i = 0;

        while (i < rtf.Length)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    skipDepth.Push(skipping);
                    i++;
                    continue;
                case '}':
                    skipping = skipDepth.Count > 0 && skipDepth.Pop();
                    i++;
                    continue;
                case '\r' or '\n':
                    i++;
                    continue;
                case '\\':
                    break;
                default:
                    if (pendingSkip > 0)
                    {
                        pendingSkip--;
                    }
                    else if (!skipping)
                    {
                        output.Append(c);
                    }

                    i++;
                    continue;
            }

            // A control sequence.
            if (i + 1 >= rtf.Length)
            {
                break;
            }

            var next = rtf[i + 1];
            if (next is '\\' or '{' or '}')
            {
                if (!skipping)
                {
                    output.Append(next);
                }

                i += 2;
                continue;
            }

            if (next == '*')
            {
                // An optional destination the reader does not know: skip the whole group.
                skipping = true;
                i += 2;
                continue;
            }

            if (next == '\'')
            {
                var hex = rtf.Substring(i + 2, Math.Min(2, Math.Max(rtf.Length - (i + 2), 0)));
                if (!skipping && pendingSkip == 0 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    output.Append(Windows1252(b));
                }
                else if (pendingSkip > 0)
                {
                    pendingSkip--;
                }

                i += 4;
                continue;
            }

            if (!char.IsLetter(next))
            {
                // A control symbol: \~ is a non-breaking space, \- and \_ are hyphen hints.
                if (!skipping && next == '~')
                {
                    output.Append(' ');
                }

                i += 2;
                continue;
            }

            var wordStart = i + 1;
            var j = wordStart;
            while (j < rtf.Length && char.IsLetter(rtf[j]))
            {
                j++;
            }

            var word = rtf[wordStart..j];
            var paramStart = j;
            if (j < rtf.Length && (rtf[j] == '-' || char.IsDigit(rtf[j])))
            {
                j++;
                while (j < rtf.Length && char.IsDigit(rtf[j]))
                {
                    j++;
                }
            }

            int? parameter = int.TryParse(rtf[paramStart..j], out var p) ? p : null;
            if (j < rtf.Length && rtf[j] == ' ')
            {
                j++;
            }

            i = j;

            if (SkippedDestinations.Contains(word))
            {
                skipping = true;
                continue;
            }

            if (skipping)
            {
                continue;
            }

            switch (word)
            {
                case "par" or "line" or "sect" or "page" or "row":
                    output.Append('\n');
                    break;
                case "tab" or "cell":
                    output.Append('\t');
                    break;
                case "uc":
                    unicodeSkip = Math.Max(parameter ?? 1, 0);
                    break;
                case "u" when parameter is { } code:
                    output.Append((char)(code < 0 ? code + 65536 : code));
                    pendingSkip = unicodeSkip;
                    break;
                case "emdash":
                    output.Append('—');
                    break;
                case "endash":
                    output.Append('–');
                    break;
                case "bullet":
                    output.Append('•');
                    break;
            }
        }

        return Normalize(output.ToString());
    }

    /// <summary>Unix line breaks, no trailing spaces, at most one blank line in a row, no leading or trailing blank
    /// lines.</summary>
    public static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ')
            .Split('\n')
            .Select(line => line.TrimEnd());
        var result = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                blank++;
                continue;
            }

            if (result.Length > 0)
            {
                result.Append(blank > 0 ? "\n\n" : "\n");
            }

            result.Append(line);
            blank = 0;
        }

        return result.ToString();
    }

    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "header", "footer", "headerl", "headerr", "headerf",
        "footerl", "footerr", "footerf", "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl",
        "themedata", "colorschememapping", "datastore", "latentstyles", "object", "fldinst", "filetbl", "revtbl",
    };

    // Windows-1252 matches Latin-1 outside 0x80-0x9F; that range holds typographic punctuation, mapped here so the
    // code pages provider is not needed.
    private static readonly char[] Windows1252High =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    ];

    private static char Windows1252(int value) =>
        value is >= 0x80 and <= 0x9F ? Windows1252High[value - 0x80] : (char)value;

    [GeneratedRegex(@"<(script|style|head|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"</(p|div|h[1-6]|tr|table|ul|ol|blockquote|pre|section|article)\s*>|<(p|div|h[1-6]|tr|table|blockquote|pre|section|article)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnds();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CellEnds();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();
}
