using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Delivery;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// Download mode hands the generated file straight back as the pipeline run's result — but only when the caller
/// is the one synchronous, non-bulk-export trigger path that actually has an HTTP response to carry it through
/// (see <see cref="PipelineWriteContext.AllowInlineDelivery"/>'s doc comment). Every other trigger must fail loudly
/// rather than silently drop the export.
/// </summary>
public sealed class DownloadDeliveryStrategyTests
{
    private static DestinationConfiguration Destination() =>
        new("Download Export", DestinationType.Csv, new SecretReference("kv", "secret"), "export.csv");

    private static GeneratedFile File() => new("export.csv", "text/csv", "a,b\n1,2\n"u8.ToArray());

    [Fact]
    public async Task Returns_the_file_as_InlineDownload_when_the_run_allows_inline_delivery()
    {
        var strategy = new DownloadDeliveryStrategy();
        var context = new PipelineWriteContext(AllowInlineDelivery: true, "Route", DateTimeOffset.UtcNow);

        var result = await strategy.DeliverAsync(Destination(), File(), 2, context, CancellationToken.None);

        result.Count.Should().Be(2);
        result.InlineDownload.Should().NotBeNull();
        result.InlineDownload!.FileName.Should().Be("export.csv");
        result.DownloadUrl.Should().BeNull();
    }

    [Fact]
    public async Task Throws_when_the_run_does_not_allow_inline_delivery()
    {
        var strategy = new DownloadDeliveryStrategy();
        var context = new PipelineWriteContext(AllowInlineDelivery: false, "Route", DateTimeOffset.UtcNow);

        var act = () => strategy.DeliverAsync(Destination(), File(), 2, context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
