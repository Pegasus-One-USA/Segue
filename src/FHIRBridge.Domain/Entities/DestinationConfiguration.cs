using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class DestinationConfiguration : AuditableChildEntity<Guid>
{
    private DestinationConfiguration()
    {
    }

    public DestinationConfiguration(
        string name,
        DestinationType destinationType,
        SecretReference secretReference,
        string? target)
    {
        Id = Guid.NewGuid();
        Name = name;
        DestinationType = destinationType;
        SecretReference = secretReference;
        Target = target;
        IsEnabled = true;
    }

    public string Name { get; private set; } = default!;
    public DestinationType DestinationType { get; private set; }
    public SecretReference SecretReference { get; private set; } = default!;
    public string? Target { get; private set; }
    public bool IsEnabled { get; private set; }

    public void Update(
        string name,
        DestinationType destinationType,
        SecretReference secretReference,
        string? target)
    {
        Name = name;
        DestinationType = destinationType;
        SecretReference = secretReference;
        Target = target;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }
}
