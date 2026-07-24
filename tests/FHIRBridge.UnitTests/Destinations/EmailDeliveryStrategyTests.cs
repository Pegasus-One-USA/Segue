using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Delivery;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

public sealed class EmailDeliveryStrategyTests
{
    private static DestinationConfiguration Destination(string metadataJson) =>
        new("Email Export", DestinationType.Csv, new SecretReference("kv", "secret"), "export.csv", metadataJson);

    private static GeneratedFile File() => new("export.csv", "text/csv", "a,b\n1,2\n"u8.ToArray());

    [Fact]
    public async Task Sends_the_file_as_an_attachment_to_the_configured_recipients()
    {
        var sender = new Mock<IEmailSender>();
        sender
            .Setup(s => s.SendAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<EmailAttachment>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var strategy = new EmailDeliveryStrategy(sender.Object);
        var destination = Destination("""{"dest_emailTo":"alice@example.com, bob@example.com","dest_emailCc":"carol@example.com"}""");
        var context = new PipelineWriteContext(AllowInlineDelivery: false, "Nightly Export Route", new DateTimeOffset(2026, 7, 16, 3, 0, 0, TimeSpan.Zero));

        var result = await strategy.DeliverAsync(destination, File(), 5, context, CancellationToken.None);

        result.Count.Should().Be(5);
        result.InlineDownload.Should().BeNull();
        sender.Verify(s => s.SendAsync(
            It.Is<IReadOnlyCollection<string>>(to => to.SequenceEqual(new[] { "alice@example.com", "bob@example.com" })),
            It.Is<IReadOnlyCollection<string>?>(cc => cc!.SequenceEqual(new[] { "carol@example.com" })),
            It.Is<string>(subject => subject.Contains("Nightly Export Route")),
            It.Is<string>(body => body.Contains("5 record")),
            It.Is<IReadOnlyCollection<EmailAttachment>?>(a => a!.Single().FileName == "export.csv"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Throws_when_no_recipient_is_configured()
    {
        var sender = new Mock<IEmailSender>();
        var strategy = new EmailDeliveryStrategy(sender.Object);
        var destination = Destination("{}");
        var context = new PipelineWriteContext(false, "Route", DateTimeOffset.UtcNow);

        var act = () => strategy.DeliverAsync(destination, File(), 1, context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
