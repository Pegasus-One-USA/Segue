using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Delivery;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

public sealed class SftpDeliveryStrategyTests
{
    private static DestinationConfiguration Destination() =>
        new("SFTP Export", DestinationType.Csv, new SecretReference("kv", "secret"), "export.csv");

    [Fact]
    public async Task Throws_a_clear_error_when_the_secret_is_not_an_sftp_uri()
    {
        var secretProvider = new Mock<ISecretProvider>();
        secretProvider
            .Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("not-a-valid-uri");

        var strategy = new SftpDeliveryStrategy(secretProvider.Object);
        var file = new GeneratedFile("export.csv", "text/csv", "a,b\n1,2\n"u8.ToArray());
        var context = new PipelineWriteContext(false, "Route", DateTimeOffset.UtcNow);

        var act = () => strategy.DeliverAsync(Destination(), file, 1, context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*sftp://user:password@host:port/path*");
    }
}
