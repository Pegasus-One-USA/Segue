using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// Test double for <see cref="IUserDisplayNameResolver"/> that echoes every value back unresolved (no Users table
/// to resolve against in these unit tests). Used by tests that construct services depending on this resolver
/// directly and don't care about actual GUID → display-name resolution.
/// </summary>
public sealed class PassthroughUserDisplayNameResolver : IUserDisplayNameResolver
{
    public Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IEnumerable<string?> actorValues, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> result = actorValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct()
            .ToDictionary(value => value, value => value);

        return Task.FromResult(result);
    }
}
