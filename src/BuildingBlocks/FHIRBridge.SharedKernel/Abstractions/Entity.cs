namespace FHIRBridge.SharedKernel.Abstractions;

public abstract class Entity<TId>
{
    public TId Id { get; protected set; } = default!;
}
