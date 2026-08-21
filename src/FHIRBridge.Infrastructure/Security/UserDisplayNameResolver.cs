using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Resolves stored provenance strings (CreatedBy/ModifiedBy/etc., stamped via CurrentUserInfo.AuditName) back
/// into display names. Works against IUserAccessRepository rather than FHIRBridgeDbContext directly so it
/// functions identically against the in-memory and EF-backed repositories.
/// </summary>
public sealed class UserDisplayNameResolver : IUserDisplayNameResolver
{
    private readonly IUserAccessRepository _userAccessRepository;

    public UserDisplayNameResolver(IUserAccessRepository userAccessRepository)
    {
        _userAccessRepository = userAccessRepository;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IEnumerable<string?> actorValues, CancellationToken cancellationToken)
    {
        var distinct = actorValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct()
            .ToArray();

        var result = new Dictionary<string, string>();
        if (distinct.Length == 0)
        {
            return result;
        }

        // Passthrough default: anything not a GUID (a pre-conversion email, "anonymous"/"system", or a Worker
        // automation label) is already the display value.
        foreach (var value in distinct)
        {
            result[value] = value;
        }

        var guidValues = distinct.Where(value => Guid.TryParse(value, out _)).ToArray();
        if (guidValues.Length == 0)
        {
            return result;
        }

        var users = await _userAccessRepository.GetUsersAsync(cancellationToken);
        var byId = users.ToDictionary(user => user.Id);

        foreach (var value in guidValues)
        {
            if (Guid.TryParse(value, out var id) && byId.TryGetValue(id, out var user))
            {
                result[value] = user.EffectiveDisplayName;
            }
        }

        return result;
    }
}
