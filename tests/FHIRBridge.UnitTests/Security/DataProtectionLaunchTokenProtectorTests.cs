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
    public void Context_round_trips_the_route_id()
    {
        var protector = Protector();
        var routeId = Guid.NewGuid();

        var token = protector.ProtectContext(routeId);
        var context = protector.UnprotectContext(token);

        token.Should().NotContain(routeId.ToString());
        context.Should().NotBeNull();
        context!.RouteId.Should().Be(routeId);
    }

    [Fact]
    public void Context_round_trips_the_ehr_endpoint_id_without_a_caller_id()
    {
        var protector = Protector();
        var routeId = Guid.NewGuid();
        var ehrEndpointId = Guid.NewGuid();

        var context = protector.UnprotectContext(protector.ProtectContext(routeId, ehrEndpointId));

        context.Should().NotBeNull();
        context!.RouteId.Should().Be(routeId);
        context.EhrEndpointId.Should().Be(ehrEndpointId);
        context.CallerId.Should().BeNull();
    }

    [Fact]
    public void Workflow_context_round_trips_a_caller_id()
    {
        var protector = Protector();
        var workflowId = Guid.NewGuid();
        const string callerId = "https://healthapp.example.com/callback?x=1&y=2";

        var token = protector.ProtectWorkflowContext(workflowId, callerId: callerId);
        var context = protector.UnprotectContext(token);

        token.Should().NotContain(callerId);
        context.Should().NotBeNull();
        context!.WorkflowId.Should().Be(workflowId);
        context.CallerId.Should().Be(callerId);
        context.EhrEndpointId.Should().BeNull();
    }

    [Fact]
    public void Route_context_round_trips_a_caller_id_together_with_an_ehr_endpoint_id()
    {
        var protector = Protector();
        var routeId = Guid.NewGuid();
        var ehrEndpointId = Guid.NewGuid();
        const string callerId = "https://healthapp.example.com/callback";

        var context = protector.UnprotectContext(protector.ProtectContext(routeId, ehrEndpointId, callerId));

        context.Should().NotBeNull();
        context!.RouteId.Should().Be(routeId);
        context.EhrEndpointId.Should().Be(ehrEndpointId);
        context.CallerId.Should().Be(callerId);
    }

    [Fact]
    public void Workflow_context_round_trips_ehr_endpoint_id_caller_id_session_id_and_user_identity_together()
    {
        // Reproduces the Patient Standalone hospital-picker launch exactly: an EhrEndpointId (the selected MyChart
        // hospital) alongside a callerId, sessionId, and userIdentity all set on the same token.
        var protector = Protector();
        var workflowId = Guid.NewGuid();
        var ehrEndpointId = Guid.NewGuid();
        const string callerId = "https://healthapp.example.com/launchpatientstandalone";
        const string sessionId = "session-abc-123";
        const string userIdentity = "patient@healthapp.local";

        var token = protector.ProtectWorkflowContext(workflowId, ehrEndpointId, callerId, sessionId, userIdentity);
        var context = protector.UnprotectContext(token);

        context.Should().NotBeNull();
        context!.WorkflowId.Should().Be(workflowId);
        context.EhrEndpointId.Should().Be(ehrEndpointId);
        context.CallerId.Should().Be(callerId);
        context.SessionId.Should().Be(sessionId);
        context.UserIdentity.Should().Be(userIdentity);
    }

    [Fact]
    public void Route_context_round_trips_ehr_endpoint_id_caller_id_session_id_and_user_identity_together()
    {
        var protector = Protector();
        var routeId = Guid.NewGuid();
        var ehrEndpointId = Guid.NewGuid();
        const string callerId = "https://healthapp.example.com/callback";
        const string sessionId = "session-xyz-789";
        const string userIdentity = "providerstandalone@healthapp.local";

        var token = protector.ProtectContext(routeId, ehrEndpointId, callerId, sessionId, userIdentity);
        var context = protector.UnprotectContext(token);

        context.Should().NotBeNull();
        context!.RouteId.Should().Be(routeId);
        context.EhrEndpointId.Should().Be(ehrEndpointId);
        context.CallerId.Should().Be(callerId);
        context.SessionId.Should().Be(sessionId);
        context.UserIdentity.Should().Be(userIdentity);
    }

    [Fact]
    public void Tokens_minted_before_the_caller_id_feature_still_parse_with_a_null_caller_id()
    {
        // ProtectContext with no callerId reproduces exactly what every previously minted launch URL looks like.
        var protector = Protector();
        var routeId = Guid.NewGuid();

        var context = protector.UnprotectContext(protector.ProtectContext(routeId));

        context.Should().NotBeNull();
        context!.RouteId.Should().Be(routeId);
        context.CallerId.Should().BeNull();
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
