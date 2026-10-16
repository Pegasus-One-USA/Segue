using FHIRBridge.Application.Abstractions.Cds;

namespace FHIRBridge.Application.Services.Cds;

/// <summary>Registry over every DI-registered <see cref="ICdsServiceHandler"/>; a new handler type is one more registration.</summary>
public sealed class CdsServiceHandlerRegistry : ICdsServiceHandlerRegistry
{
    private readonly Dictionary<string, ICdsServiceHandler> _byKey;

    public CdsServiceHandlerRegistry(IEnumerable<ICdsServiceHandler> handlers)
    {
        All = handlers.OrderBy(handler => handler.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        _byKey = new Dictionary<string, ICdsServiceHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in All)
        {
            if (!_byKey.TryAdd(handler.Key, handler))
            {
                throw new InvalidOperationException($"Two CDS handlers are registered with the key '{handler.Key}'.");
            }
        }
    }

    public IReadOnlyList<ICdsServiceHandler> All { get; }

    public ICdsServiceHandler? Find(string? key) =>
        key is not null && _byKey.TryGetValue(key, out var handler) ? handler : null;
}
