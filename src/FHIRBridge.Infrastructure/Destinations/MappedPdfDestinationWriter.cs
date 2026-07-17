using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Renders mapped records into a tabular PDF report written to the target directory/path. Useful for human-readable
/// summaries (e.g. census or quality reports). The destination secret holds the output directory.
/// </summary>
public sealed class MappedPdfDestinationWriter : IConfiguredDestinationWriter
{
    static MappedPdfDestinationWriter()
    {
        // QuestPDF Community license (free for small businesses / open source).
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private readonly ISecretProvider _secretProvider;

    public MappedPdfDestinationWriter(ISecretProvider secretProvider)
    {
        _secretProvider = secretProvider;
    }

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return new DestinationWriteResult(0);
        }

        var targetRoot = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var columns = MappedDestinationSerialization.GetColumns(records);
        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "pdf");

        Directory.CreateDirectory(targetRoot);
        var path = Path.Combine(targetRoot, fileName);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(20);
                page.Header().Text($"{mappingProfile.ResourceType} export — {records.Count} record(s)").FontSize(14).Bold();

                page.Content().Table(table =>
                {
                    table.ColumnsDefinition(definition =>
                    {
                        foreach (var _ in columns)
                        {
                            definition.RelativeColumn();
                        }
                    });

                    foreach (var column in columns)
                    {
                        table.Cell().Background(Colors.Grey.Lighten2).Padding(3).Text(column).Bold().FontSize(7);
                    }

                    foreach (var record in records)
                    {
                        foreach (var column in columns)
                        {
                            table.Cell().Padding(3).Text(MappedDestinationSerialization.GetCell(record, column)).FontSize(7);
                        }
                    }
                });

                page.Footer().AlignRight().Text(x => x.CurrentPageNumber());
            });
        });

        await Task.Run(() => document.GeneratePdf(path), cancellationToken);
        return new DestinationWriteResult(records.Count);
    }
}
