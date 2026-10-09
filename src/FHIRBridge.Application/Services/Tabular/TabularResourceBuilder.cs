using FHIRBridge.Application.Abstractions.Tabular;

namespace FHIRBridge.Application.Services.Tabular;

/// <summary>The resources built from a whole table, and why some rows built nothing.</summary>
/// <param name="Errors">At most <see cref="TabularResourceBuilder.MaxReportedErrors"/>, row and column only.</param>
public sealed record TabularBuildResult(
    IReadOnlyList<TabularRenderedResource> Resources,
    IReadOnlyList<string> Errors,
    int ErrorCount,
    int DuplicatesDropped);

/// <summary>
/// Runs every template over every row. A resource that appears on several rows with the same id (a patient repeated
/// on each of their allergy rows) is kept once, the first time: the rows describe one patient, not several.
/// </summary>
public static class TabularResourceBuilder
{
    public const int MaxReportedErrors = 20;

    public static TabularBuildResult Build(TabularRows table, IReadOnlyList<TabularFhirTemplate> templates)
    {
        var resources = new List<TabularRenderedResource>();
        var seen = new HashSet<(string Type, string Id)>();
        var errors = new List<string>();
        var errorCount = 0;
        var duplicates = 0;

        for (var i = 0; i < table.Rows.Count; i++)
        {
            var (rendered, rowErrors) = TabularFhirTemplateEngine.Render(templates, table.Rows[i], i + 1);
            errorCount += rowErrors.Count;
            foreach (var error in rowErrors)
            {
                if (errors.Count < MaxReportedErrors)
                {
                    errors.Add(error);
                }
            }

            foreach (var resource in rendered)
            {
                if (resource.ResourceId is { } id && !seen.Add((resource.ResourceType, id)))
                {
                    duplicates++;
                    continue;
                }

                resources.Add(resource);
            }
        }

        return new TabularBuildResult(resources, errors, errorCount, duplicates);
    }
}
