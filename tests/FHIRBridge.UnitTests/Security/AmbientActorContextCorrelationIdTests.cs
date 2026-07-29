using FHIRBridge.Application.Security;
using FHIRBridge.Infrastructure.Security;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// Guards the fix for Worker-hosted (scheduled pipeline/workflow, webhook) executions silently losing their
/// CorrelationId on AuditLog/AuthenticationLog/SecurityEvent rows — those all fall back to
/// <see cref="SystemCurrentUserService.CurrentUser"/>, which only carries a real value when
/// <see cref="AmbientActorContext.BeginScope"/> was given one at the top of the run.
/// </summary>
public sealed class AmbientActorContextCorrelationIdTests
{
    [Fact]
    public void BeginScope_flows_correlationId_through_SystemCurrentUserService()
    {
        var ambientActorContext = new AmbientActorContext();
        var currentUserService = new SystemCurrentUserService(ambientActorContext);

        using (ambientActorContext.BeginScope("Scheduler (Legacy Poll)", "corr-123"))
        {
            currentUserService.CurrentUser.CorrelationId.Should().Be("corr-123");
            currentUserService.CurrentUser.ExternalUserId.Should().Be("Scheduler (Legacy Poll)");
        }

        currentUserService.CurrentUser.CorrelationId.Should().BeNull();
        currentUserService.CurrentUser.ExternalUserId.Should().Be("system");
    }

    [Fact]
    public void BeginScope_restores_previous_actor_and_correlationId_on_dispose()
    {
        var ambientActorContext = new AmbientActorContext();

        using (ambientActorContext.BeginScope("Outer", "outer-corr"))
        {
            using (ambientActorContext.BeginScope("Inner", "inner-corr"))
            {
                ambientActorContext.Current.Should().Be("Inner");
                ambientActorContext.CorrelationId.Should().Be("inner-corr");
            }

            ambientActorContext.Current.Should().Be("Outer");
            ambientActorContext.CorrelationId.Should().Be("outer-corr");
        }

        ambientActorContext.Current.Should().BeNull();
        ambientActorContext.CorrelationId.Should().BeNull();
    }

    [Fact]
    public void BeginScope_without_a_correlationId_leaves_it_null()
    {
        var ambientActorContext = new AmbientActorContext();
        var currentUserService = new SystemCurrentUserService(ambientActorContext);

        using (ambientActorContext.BeginScope("Webhook Ingestion (Automated)"))
        {
            currentUserService.CurrentUser.CorrelationId.Should().BeNull();
        }
    }
}
