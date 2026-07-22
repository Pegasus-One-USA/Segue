namespace FHIRBridge.Governance;

/// <summary>
/// Default rule-based classifier. Matches on exception type-hierarchy names and namespaces (rather than
/// hard type references) so it stays free of dependencies on provider assemblies — a SQL, HTTP, or auth
/// exception is recognized by shape, keeping the classifier cloud-/provider-agnostic. Extra rules can be
/// supplied via the constructor; supplied rules are evaluated before the built-in defaults.
/// </summary>
public sealed class DefaultExceptionClassifier : IExceptionClassifier
{
    private readonly IReadOnlyList<ExceptionCategoryRule> _rules;

    public DefaultExceptionClassifier(IEnumerable<ExceptionCategoryRule>? additionalRules = null)
    {
        var rules = new List<ExceptionCategoryRule>();
        if (additionalRules is not null)
        {
            rules.AddRange(additionalRules);
        }

        rules.AddRange(DefaultRules);
        _rules = rules;
    }

    public ErrorCategory Classify(Exception exception)
    {
        foreach (var rule in _rules)
        {
            if (rule.Matches(exception))
            {
                return rule.Category;
            }
        }

        return ErrorCategory.Unknown;
    }

    private static IReadOnlyList<ExceptionCategoryRule> DefaultRules { get; } = new List<ExceptionCategoryRule>
    {
        new(ErrorCategory.Validation, ex => NameContains(ex, "Validation") || TypeIs(ex, "ArgumentException", "ArgumentNullException", "FormatException")),
        new(ErrorCategory.Authentication, ex => NameContains(ex, "Authentication") || TypeIs(ex, "AuthenticationException")),
        new(ErrorCategory.Authorization, ex => NameContains(ex, "Authorization", "Forbidden") || TypeIs(ex, "UnauthorizedAccessException")),
        new(ErrorCategory.Database, ex => NameContains(ex, "SqlException", "DbUpdate", "DbException", "EntityFramework") || NamespaceContains(ex, "Microsoft.Data.SqlClient", "Microsoft.EntityFrameworkCore")),
        new(ErrorCategory.Network, ex => TypeIs(ex, "HttpRequestException", "SocketException", "WebException", "TimeoutException", "TaskCanceledException") || NamespaceContains(ex, "System.Net.Sockets")),
        new(ErrorCategory.ExternalSystem, ex => NameContains(ex, "Fhir", "Sftp", "Ssh", "Smtp", "Storage", "Blob") || NamespaceContains(ex, "Renci.SshNet")),
        new(ErrorCategory.Business, ex => NameContains(ex, "FHIRBridge", "Domain", "BusinessRule") || TypeIs(ex, "InvalidOperationException")),
        new(ErrorCategory.Infrastructure, ex => TypeIs(ex, "IOException", "OperationCanceledException")),
    };

    private static bool TypeIs(Exception exception, params string[] typeNames)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var name in typeNames)
            {
                if (string.Equals(type.Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool NameContains(Exception exception, params string[] fragments)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var fragment in fragments)
            {
                if (type.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool NamespaceContains(Exception exception, params string[] fragments)
    {
        var ns = exception.GetType().Namespace ?? string.Empty;
        foreach (var fragment in fragments)
        {
            if (ns.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
