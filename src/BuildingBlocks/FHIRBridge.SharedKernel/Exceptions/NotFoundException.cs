using System.Text.RegularExpressions;

namespace FHIRBridge.SharedKernel.Exceptions;

public sealed class NotFoundException : FHIRBridgeException
{
    // Diagnostic Message keeps the entity name + raw id for logs; UserMessage (what the client sees)
    // never carries either — see FHIRBridgeException's remarks on why the split exists.
    public NotFoundException(string entityName, object id)
        : base(
            $"{entityName} with id '{id}' was not found.",
            $"The requested {Humanize(entityName)} could not be found.")
    {
    }

    /// <summary>"SourceConnection" -> "source connection", "AppSecretDto" -> "app secret". Generic
    /// PascalCase/Dto-suffix humanizer — not a switch on entity type, so it needs no maintenance as new
    /// entities are added and stays clear of the ApplicationType no-switch architecture rule.</summary>
    private static string Humanize(string entityName)
    {
        var name = entityName.EndsWith("Dto", StringComparison.Ordinal)
            ? entityName[..^3]
            : entityName;
        return Regex.Replace(name, "(?<!^)([A-Z])", " $1").ToLowerInvariant();
    }
}
