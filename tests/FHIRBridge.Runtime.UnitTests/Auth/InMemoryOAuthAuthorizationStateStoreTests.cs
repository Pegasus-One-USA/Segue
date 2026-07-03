using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Auth;

public sealed class InMemoryOAuthAuthorizationStateStoreTests
{
    private static PendingAuthorization Pending() => new(
        Guid.NewGuid(),
        RuntimeSourceType.Epic,
        "Epic Standalone",
        "verifier",
        "https://app.example.com/api/v1/oauth/callback",
        "https://auth.example.com/token",
        "client-id");

    [Fact]
    public async Task Take_returns_the_saved_pending_authorization()
    {
        var store = new InMemoryOAuthAuthorizationStateStore();
        var pending = Pending();
        await store.SaveAsync("state-1", pending, CancellationToken.None);

        var taken = await store.TakeAsync("state-1", CancellationToken.None);

        taken.Should().Be(pending);
    }

    [Fact]
    public async Task Take_is_single_use_so_a_state_cannot_be_replayed()
    {
        var store = new InMemoryOAuthAuthorizationStateStore();
        await store.SaveAsync("state-1", Pending(), CancellationToken.None);

        (await store.TakeAsync("state-1", CancellationToken.None)).Should().NotBeNull();
        (await store.TakeAsync("state-1", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Take_returns_null_for_an_unknown_state()
    {
        var store = new InMemoryOAuthAuthorizationStateStore();

        (await store.TakeAsync("never-saved", CancellationToken.None)).Should().BeNull();
    }
}
