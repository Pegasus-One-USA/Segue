using FHIRBridge.Api.Workflows;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace FHIRBridge.UnitTests.Workflows;

/// <summary>The signed token that carries an external trigger's Group ID / Search Criteria across the EHR sign-in redirect.</summary>
public sealed class ExternalRunOverridesTokenTests
{
    private static readonly Guid WorkflowId = Guid.NewGuid();
    private readonly IDataProtectionProvider _provider = new EphemeralDataProtectionProvider();

    [Fact]
    public void Round_trips_both_values()
    {
        var token = ExternalRunOverridesToken.Protect(_provider, WorkflowId, "group-1", "name=brown");

        token.Should().NotBeNull().And.NotContain("brown");
        ExternalRunOverridesToken.TryRead(_provider, token!, WorkflowId, out var group, out var search).Should().BeTrue();
        group.Should().Be("group-1");
        search.Should().Be("name=brown");
    }

    [Fact]
    public void Carries_only_the_value_that_was_supplied()
    {
        var token = ExternalRunOverridesToken.Protect(_provider, WorkflowId, null, "name=brown");

        ExternalRunOverridesToken.TryRead(_provider, token!, WorkflowId, out var group, out var search).Should().BeTrue();
        group.Should().BeNull();
        search.Should().Be("name=brown");
    }

    [Fact]
    public void No_token_when_there_is_nothing_to_carry() =>
        ExternalRunOverridesToken.Protect(_provider, WorkflowId, null, null).Should().BeNull();

    [Fact]
    public void Another_workflows_token_is_refused()
    {
        var token = ExternalRunOverridesToken.Protect(_provider, WorkflowId, null, "name=brown");

        ExternalRunOverridesToken.TryRead(_provider, token!, Guid.NewGuid(), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_tampered_or_foreign_token_is_refused()
    {
        var token = ExternalRunOverridesToken.Protect(_provider, WorkflowId, null, "name=brown")!;

        ExternalRunOverridesToken.TryRead(_provider, token[..^4] + "AAAA", WorkflowId, out _, out _).Should().BeFalse();
        ExternalRunOverridesToken.TryRead(_provider, "not-a-token", WorkflowId, out _, out _).Should().BeFalse();
        ExternalRunOverridesToken.TryRead(new EphemeralDataProtectionProvider(), token, WorkflowId, out _, out _).Should().BeFalse();
    }
}
