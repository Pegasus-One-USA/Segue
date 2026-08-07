namespace FHIRBridge.Domain.Enums;

/// <summary>What a transform node does when its input is missing/null.</summary>
public enum NullPolicy
{
    Skip = 0,
    Default = 1,
    Error = 2
}
