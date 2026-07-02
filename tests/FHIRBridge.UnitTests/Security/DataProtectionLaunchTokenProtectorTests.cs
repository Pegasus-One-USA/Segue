using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace FHIRBridge.UnitTests.Security;

public sealed class DataProtectionLaunchTokenProtectorTests
{
    private static ILaunchTokenProtector Protector() =>
        new DataProtectionLaunchTokenProtector(new EphemeralDataProtectionProvider());

    [Fact]
    public void Context_round_trips_the_tenant_and_route_ids()
    {
        var protector = Protector();
        var tenantId = Guid.NewGuid();
        var routeId = Guid.NewGuid();

        var token = protector.ProtectContext(tenantId, routeId);
        var context = protector.UnprotectContext(token);

        token.Should().NotContain(tenantId.ToString());
        context.Should().NotBeNull();
        context!.TenantId.Should().Be(tenantId);
        context.RouteId.Should().Be(routeId);
    }

    [Fact]
    public void State_round_trips_the_nonce()
    {
        var protector = Protector();

        var token = protector.ProtectState("nonce-123");

        token.Should().NotBe("nonce-123");
        protector.UnprotectState(token).Should().Be("nonce-123");
    }

    [Fact]
    public void Tampered_or_foreign_tokens_are_rejected()
    {
        var protector = Protector();

        protector.UnprotectContext("garbage").Should().BeNull();
        protector.UnprotectState("garbage").Should().BeNull();

        // A token minted for one purpose must not validate under the other.
        var stateToken = protector.ProtectState("nonce-123");
        protector.UnprotectContext(stateToken).Should().BeNull();
    }
}
