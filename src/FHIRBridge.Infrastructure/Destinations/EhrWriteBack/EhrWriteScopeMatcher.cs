namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>
/// Does a granted SMART scope string allow creating a resource type? Accepts SMART v1 (<c>system/Type.write</c>,
/// <c>system/Type.*</c>) and v2 (<c>system/Type.c</c>, <c>.cruds</c> and any letter set containing c), with a
/// <c>*</c> resource wildcard. Epic grants v1 spellings even to a SMART v2 app, so both must pass.
/// </summary>
internal static class EhrWriteScopeMatcher
{
    public static bool AllowsCreate(string grantedScope, string resourceType)
    {
        foreach (var rawScope in grantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // A v2 query (?category=http://...) can contain dots and slashes of its own: drop it before splitting.
            var queryStart = rawScope.IndexOf('?');
            var scope = queryStart >= 0 ? rawScope[..queryStart] : rawScope;
            var slash = scope.IndexOf('/');
            var dot = scope.LastIndexOf('.');
            if (slash < 0 || dot <= slash)
            {
                continue;
            }

            var context = scope[..slash];
            if (context is not ("system" or "user" or "patient"))
            {
                continue;
            }

            var type = scope[(slash + 1)..dot];
            if (type != "*" && !string.Equals(type, resourceType, StringComparison.Ordinal))
            {
                continue;
            }

            var permission = scope[(dot + 1)..];
            if (permission is "write" or "*" || IsV2LetterSetWithCreate(permission))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsV2LetterSetWithCreate(string permission) =>
        permission.Length is > 0 and <= 5
        && permission.All(letter => "cruds".Contains(letter))
        && permission.Contains('c');
}
