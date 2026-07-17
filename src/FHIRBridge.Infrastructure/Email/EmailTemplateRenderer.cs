namespace FHIRBridge.Infrastructure.Email;

/// <summary>
/// Plain <c>{{Key}}</c> placeholder substitution for destination-authored email subject/body templates (e.g.
/// <c>"Patient Export - {{RouteName}}"</c>). Deliberately not a templating engine — this is the smallest thing that
/// covers the wizard's placeholder fields ({{RouteName}}, {{RunDate}}, {{RowCount}}).
/// </summary>
internal static class EmailTemplateRenderer
{
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = template;
        foreach (var (key, value) in values)
        {
            result = result.Replace("{{" + key + "}}", value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}
