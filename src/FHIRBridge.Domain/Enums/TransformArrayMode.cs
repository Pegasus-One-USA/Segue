namespace FHIRBridge.Domain.Enums;

/// <summary>Whether a transform node applies once to the whole value, or once per item when the mapped
/// value is a real collection (not a string). Defaults to <see cref="Whole"/> so every rule saved before
/// this existed keeps behaving exactly as it did — per-item is opt-in, not a silent behavior change.</summary>
public enum TransformArrayMode
{
    Whole = 0,
    PerItem = 1
}
