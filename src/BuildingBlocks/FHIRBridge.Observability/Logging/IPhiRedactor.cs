using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Observability.Logging;

/// <summary>
/// Shared PHI-redaction logic used by both <see cref="PhiMaskingEnricher"/> (structured log properties) and
/// <c>GlobalExceptionManager</c> (persisted <c>ErrorLogs</c> rows) — a single rule set so the two paths can't
/// drift apart, per HIPAA gap #10.
/// </summary>
public interface IPhiRedactor
{
    /// <summary>
    /// Best-effort redaction of free-text (exception messages, stack traces, rendered log messages): replaces
    /// the value half of any <c>key: value</c>/<c>key=value</c>/<c>"key":"value"</c> pair whose key matches a
    /// configured PHI field name. Not a substitute for keeping PHI out of exception messages in the first place —
    /// a defensive backstop for when it ends up there anyway.
    /// </summary>
    string? Redact(string? text);

    /// <summary>True if <paramref name="propertyName"/> is a configured PHI-sensitive field name.</summary>
    bool IsSensitive(string propertyName);
}

public sealed class PhiRedactor : IPhiRedactor
{
    private const string Mask = "***";

    public static readonly string[] DefaultMaskedProperties =
    [
        "ssn", "mrn", "birthDate", "birthdate", "email", "phone", "telecom",
        "givenName", "familyName", "patientName", "name", "address", "postalCode", "identifierValue"
    ];

    private readonly HashSet<string> _maskedNames;
    private readonly Regex _keyValuePattern;

    /// <summary>Uses <see cref="DefaultMaskedProperties"/> only — for callers with no <see cref="IConfiguration"/> at hand.</summary>
    public PhiRedactor() : this((IEnumerable<string>?)null)
    {
    }

    public PhiRedactor(IConfiguration configuration)
        : this(SplitConfiguredNames(configuration["Observability:Phi:MaskedProperties"]))
    {
    }

    private PhiRedactor(IEnumerable<string>? names)
    {
        _maskedNames = new HashSet<string>(names ?? DefaultMaskedProperties, StringComparer.OrdinalIgnoreCase);

        var alternation = string.Join('|', _maskedNames.Select(Regex.Escape));
        _keyValuePattern = new Regex(
            "(?<key>\"?(?:" + alternation + ")\"?)\\s*[:=]\\s*(?<quote>\"?)(?<value>[^\",\r\n}]*)\\k<quote>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public bool IsSensitive(string propertyName) => _maskedNames.Contains(propertyName);

    private static IEnumerable<string>? SplitConfiguredNames(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? null
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return _keyValuePattern.Replace(text, m => $"{m.Groups["key"].Value}={Mask}");
    }
}
