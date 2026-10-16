using FHIRBridge.Api.Workflows;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.ApiClients;

public sealed class ExternalRunTicketServiceTests
{
    private static readonly ExternalRunTicket Ticket = new(
        Guid.NewGuid(), "cid_1", Guid.NewGuid(), null, null, "async", null, null, string.Empty);

    [Fact]
    public void Reading_a_ticket_does_not_use_it_up()
    {
        var service = Service();
        var issued = service.Issue(Ticket);

        service.Read(issued).Should().NotBeNull();
        service.Read(issued).Should().NotBeNull();
    }

    [Fact]
    public void A_tampered_ticket_is_not_read()
    {
        var service = Service();
        var issued = service.Issue(Ticket);

        service.Read(issued[..^4] + "AAAA").Should().BeNull();
    }

    [Fact]
    public async Task Simultaneous_claims_of_one_ticket_start_at_most_one_run()
    {
        var service = Service();
        var ticket = service.Read(service.Issue(Ticket))!;

        var claims = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => Task.Run(() => service.TryClaimAsync(ticket, CancellationToken.None))));

        claims.Count(claimed => claimed).Should().Be(1);
    }

    [Fact]
    public async Task An_unreachable_cache_refuses_the_ticket_instead_of_throwing()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Redis down"));
        var service = Service(cache.Object);
        var ticket = service.Read(service.Issue(Ticket))!;

        (await service.TryClaimAsync(ticket, CancellationToken.None)).Should().BeFalse();
    }

    private static ExternalRunTicketService Service(IDistributedCache? cache = null) =>
        new(
            new EphemeralDataProtectionProvider(),
            cache ?? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLogger<ExternalRunTicketService>.Instance);
}
