using FHIRBridge.Application.Abstractions.Security;
using Serilog.Context;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Pairs <see cref="IAmbientActorContext.BeginScope"/> with a matching Serilog <c>LogContext</c> property, so a
/// single call gives a non-interactive execution (scheduled pipeline/workflow run, webhook ingestion,
/// background workflow run) both: (1) a <see cref="ICurrentUserService.CurrentUser"/> that carries the run's
/// CorrelationId for governance-table writes, and (2) a "CorrelationId" property on every Serilog line emitted
/// during the run, so it becomes filterable in Seq even for outcomes that never reach a governance table (e.g.
/// routine sub-500 rejections — see <c>Program.cs</c>'s exception handler).
/// </summary>
public static class AmbientActorContextExtensions
{
    public static IDisposable BeginCorrelatedScope(
        this IAmbientActorContext ambientActorContext, string actor, string? correlationId = null)
    {
        var actorScope = ambientActorContext.BeginScope(actor, correlationId);
        var logContextScope = LogContext.PushProperty("CorrelationId", correlationId);
        return new CombinedScope(actorScope, logContextScope);
    }

    private sealed class CombinedScope : IDisposable
    {
        private readonly IDisposable _actorScope;
        private readonly IDisposable _logContextScope;
        private bool _disposed;

        public CombinedScope(IDisposable actorScope, IDisposable logContextScope)
        {
            _actorScope = actorScope;
            _logContextScope = logContextScope;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _logContextScope.Dispose();
            _actorScope.Dispose();
        }
    }
}
