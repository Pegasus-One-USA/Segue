using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.Application.Security;

public sealed class AmbientActorContext : IAmbientActorContext
{
    private static readonly AsyncLocal<string?> ActorLabel = new();

    public string? Current => ActorLabel.Value;

    public IDisposable BeginScope(string actor)
    {
        var previous = ActorLabel.Value;
        ActorLabel.Value = actor;
        return new ScopeHandle(previous);
    }

    private sealed class ScopeHandle : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public ScopeHandle(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ActorLabel.Value = _previous;
        }
    }
}
