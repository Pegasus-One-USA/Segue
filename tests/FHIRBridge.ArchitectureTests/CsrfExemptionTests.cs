using FluentAssertions;

namespace FHIRBridge.ArchitectureTests;

/// <summary>
/// Guards the CSRF double-submit exemption against the specific way it silently broke in production.
///
/// The middleware in Program.cs exempted only endpoints carrying <c>[AllowAnonymous]</c>, and its comment claimed
/// this covered "the public workflow /run endpoints that third-party apps like Demo_TestApp call cross-origin".
/// It did not: <c>POST /workflows/{workflowId:guid}/run</c> was declared with
/// <c>RequireAuthorization(HasPermission(workflow.run))</c>, so the exemption never applied to the one endpoint it
/// named. Nothing failed at build time, and nothing failed at runtime either — until a portal session cookie rode
/// along on a same-site third-party call, at which point the check engaged and answered 403 "CSRF token missing or
/// invalid." to a caller that can never hold that token.
///
/// The root cause was that a prose comment and the route declaration were free to disagree. These tests remove that
/// freedom by asserting the declaration itself.
/// </summary>
public sealed class CsrfExemptionTests
{
    /// <summary>
    /// Route patterns reachable by a caller that presents no portal session cookie. Each must declare its CSRF
    /// exemption, or a stray same-site cookie will 403 a legitimate third-party call.
    /// </summary>
    private static readonly string[] NonCookieStateChangingRoutes =
        ["/workflows/{workflowId:guid}/run"];

    [Fact]
    public void Non_cookie_state_changing_endpoints_declare_a_csrf_exemption()
    {
        var text = File.ReadAllText(LocateWorkflowEndpointsFile());

        foreach (var route in NonCookieStateChangingRoutes)
        {
            var mapIndex = text.IndexOf($"MapPost(\"{route}\"", StringComparison.Ordinal);
            mapIndex.Should().BeGreaterThan(
                -1,
                $"'{route}' is expected to exist; if it was renamed, update this test rather than deleting it — " +
                "it guards a production 403 regression");

            var declaration = ExtractDeclaration(text, mapIndex);

            var isExempt = declaration.Contains("AllowAnonymous", StringComparison.Ordinal)
                           || declaration.Contains("CsrfExempt", StringComparison.Ordinal);

            isExempt.Should().BeTrue(
                $"'{route}' is callable without a portal session cookie, so it must be CSRF-exempt. The portal and " +
                "third-party apps are same-site (seguedemo.pegasusone.com / segue.pegasusone.com:3011 share the " +
                "pegasusone.com registrable domain, and SameSite ignores port and subdomain), so a portal cookie " +
                "DOES ride along on these calls and trips the double-submit check. Mark it [CsrfExempt] — see " +
                "CsrfExemptAttribute");
        }
    }

    /// <summary>
    /// The converse guard: a blanket route-level <c>RequireAuthorization</c> on /run would 401 the anonymous
    /// standalone caller before its handler ever ran. Its authorization is deliberately performed INSIDE the
    /// handler instead, which lets the cookie-authenticated portal caller keep the full workflow.run + per-vendor
    /// RBAC while the standalone caller is gated on IsPubliclyLaunchable and its callerId.
    /// </summary>
    [Fact]
    public void Workflow_run_does_not_reintroduce_a_route_level_authorization_gate()
    {
        var text = File.ReadAllText(LocateWorkflowEndpointsFile());

        var mapIndex = text.IndexOf("MapPost(\"/workflows/{workflowId:guid}/run\"", StringComparison.Ordinal);
        var declaration = ExtractDeclaration(text, mapIndex);

        // Comment lines are stripped first, and the match is on a CHAINED ".RequireAuthorization(" call. A bare
        // substring search over the whole declaration would also hit this endpoint's own comments, which discuss
        // the route-level gate it deliberately no longer has.
        var codeOnly = declaration
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

        string.Join("\n", codeOnly).Should().NotContain(
            ".RequireAuthorization(",
            "a route-level authorization gate on /run 401s the anonymous standalone caller before the handler " +
            "runs; /run authorizes in-handler by caller kind instead (see the endpoint's own comments)");
    }

    /// <summary>
    /// Returns just this endpoint's own declaration: from its Map* call to the start of the NEXT endpoint's.
    /// The terminating "});" cannot be found by a simple forward search, because a handler body contains its own
    /// nested "});" from inner lambdas — and stopping at the FOLLOWING endpoint's declaration would pick up that
    /// endpoint's AllowAnonymous, which is exactly the confusion that produced the original bug (the
    /// .AllowAnonymous() sitting directly above /run belongs to validate-run, not to /run).
    /// </summary>
    private static string ExtractDeclaration(string text, int mapIndex)
    {
        string[] mapTokens =
            ["group.MapPost(", "group.MapGet(", "group.MapPut(", "group.MapDelete(", "group.MapPatch("];

        var nextMapIndex = mapTokens
            .Select(token => text.IndexOf(token, mapIndex + 1, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(text.Length)
            .Min();

        return text[mapIndex..nextMapIndex];
    }

    private static string LocateWorkflowEndpointsFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FHIRBridge.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the FHIRBridge.sln anchor is required to locate the src tree");

        var path = Path.Combine(
            dir!.FullName, "src", "Api", "FHIRBridge.Api", "Workflows", "WorkflowEndpoints.cs");
        File.Exists(path).Should().BeTrue($"expected WorkflowEndpoints.cs at {path}");
        return path;
    }
}
