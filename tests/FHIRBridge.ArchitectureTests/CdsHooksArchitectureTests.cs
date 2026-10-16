using FluentAssertions;

namespace FHIRBridge.ArchitectureTests;

/// <summary>
/// Guardrails for the CDS Hooks feature (an EHR calls Segue synchronously and no EHR data is stored):
/// <list type="bullet">
/// <item>the built-in handler types work on the in-memory request only — no persistence, no outbound HTTP (v1 has
/// no FHIR callback) and no logging, since everything they see is PHI;</item>
/// <item>the public /cds-services controller stays anonymous to ASP.NET Core, CSRF-exempt and on the credential-less
/// CdsHooks CORS policy, which the browser-based CDS Hooks sandbox depends on.</item>
/// </list>
/// Source scans, like <see cref="DestinationWriterNoDdlTests"/>, because this project does not reference
/// FHIRBridge.Infrastructure or the Api host.
/// </summary>
public sealed class CdsHooksArchitectureTests
{
    private static readonly string[] ForbiddenInHandlers =
    [
        "Microsoft.EntityFrameworkCore",
        "FHIRBridge.Infrastructure.Persistence",
        "DbContext",
        "Application.Abstractions.Persistence",
        "HttpClient",
        "IHttpClientFactory",
        "ILogger",
        "Console.Write",
    ];

    [Fact]
    public void Cds_handlers_do_not_depend_on_persistence_http_or_logging()
    {
        var handlersDir = Path.Combine(LocateSourceRoot(), "FHIRBridge.Infrastructure", "Cds", "Handlers");
        Directory.Exists(handlersDir).Should().BeTrue($"expected the CDS handlers at {handlersDir}");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(handlersDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var forbidden in ForbiddenInHandlers.Where(f => code.Contains(f, StringComparison.Ordinal)))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: uses {forbidden}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "CDS handler types evaluate only the request's context and prefetch (PHI): they must not persist, " +
            "call out over HTTP, or log anything");
    }

    [Fact]
    public void The_public_cds_hooks_controller_is_anonymous_csrf_exempt_and_on_the_cds_cors_policy()
    {
        var path = Path.Combine(LocateSourceRoot(), "Api", "FHIRBridge.Api", "Controllers", "CdsHooksController.cs");
        File.Exists(path).Should().BeTrue($"expected CdsHooksController.cs at {path}");
        var text = File.ReadAllText(path);
        var classIndex = text.IndexOf("class CdsHooksController", StringComparison.Ordinal);
        classIndex.Should().BeGreaterThan(-1);
        // Declarations only: the XML remarks above the class discuss [ApiController] by name.
        var attributes = string.Join("\n", text[..classIndex]
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        attributes.Should().Contain("[AllowAnonymous]",
            "EHR calls carry a CDS client JWT, not a portal session; ICdsHooksService validates it per call");
        attributes.Should().Contain("[CsrfExempt]",
            "the CDS client can never hold the portal's CSRF token, and a stray portal cookie must not 403 it");
        attributes.Should().Contain("[EnableCors(DynamicPortalCorsPolicyProvider.CdsHooksPolicyName)]",
            "browser-based CDS clients (the public sandbox) call cross-origin; the Portal policy would block them");
        attributes.Should().NotContain("[ApiController]",
            "its automatic 400 would answer before the service records the call in the CDS call log");
    }

    [Fact]
    public void The_cds_hooks_cors_policy_never_allows_credentials()
    {
        var path = Path.Combine(LocateSourceRoot(), "Api", "FHIRBridge.Api", "Cors", "DynamicPortalCorsPolicyProvider.cs");
        var text = File.ReadAllText(path);
        var start = text.IndexOf("CdsHooksPolicy = new CorsPolicyBuilder()", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the CdsHooks policy is expected in DynamicPortalCorsPolicyProvider");
        var end = text.IndexOf(".Build()", start, StringComparison.Ordinal);
        var policy = text[start..end];

        policy.Should().Contain(".AllowAnyOrigin()");
        policy.Should().NotContain("AllowCredentials", "an any-origin policy with credentials would let any site ride a portal cookie");
    }

    private static string LocateSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FHIRBridge.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the FHIRBridge.sln anchor is required to locate the src tree");
        var src = Path.Combine(dir!.FullName, "src");
        Directory.Exists(src).Should().BeTrue("the src directory is expected next to FHIRBridge.sln");
        return src;
    }
}
