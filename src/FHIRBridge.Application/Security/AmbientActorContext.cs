using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.Application.Security;

public sealed class AmbientActorContext : IAmbientActorContext
{
    private static readonly AsyncLocal<string?> ActorLabel = new();
    private static readonly AsyncLocal<string?> RunCorrelationId = new();

    public string? Current => ActorLabel.Value;

    public string? CorrelationId => RunCorrelationId.Value;

    public IDisposable BeginScope(string actor, string? correlationId = null)
    {
        var previousActor = ActorLabel.Value;
        var previousCorrelationId = RunCorrelationId.Value;
        ActorLabel.Value = actor;
        RunCorrelationId.Value = correlationId;
        return new ScopeHandle(previousActor, previousCorrelationId);
    }

    private sealed class ScopeHandle : IDisposable
    {
        private readonly string? _previousActor;
        private readonly string? _previousCorrelationId;
        private bool _disposed;

        public ScopeHandle(string? previousActor, string? previousCorrelationId)
        {
            _previousActor = previousActor;
            _previousCorrelationId = previousCorrelationId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ActorLabel.Value = _previousActor;
            RunCorrelationId.Value = _previousCorrelationId;
        }
    }
}
