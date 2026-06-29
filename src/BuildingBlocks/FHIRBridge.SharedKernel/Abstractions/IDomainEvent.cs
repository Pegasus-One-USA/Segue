namespace FHIRBridge.SharedKernel.Abstractions;

public interface IDomainEvent
{
    DateTime OccurredOnUtc { get; }
}
