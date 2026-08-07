namespace FHIRBridge.Domain.Enums;

/// <summary>What happens when a transform node throws/fails to produce a valid output.</summary>
public enum TransformErrorPolicy
{
    Fail = 0,
    NullOut = 1,
    PassThrough = 2,
    RouteToDeadLetter = 3
}
