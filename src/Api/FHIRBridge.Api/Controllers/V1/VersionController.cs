using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// What build is actually running. Every support conversation starts with "which version are you on?" —
/// for a product a customer deploys into their OWN Azure/AWS subscription, that question is otherwise
/// unanswerable by us and guesswork for them.
///
/// Anonymous on purpose: an operator staring at a deployment that will not let anyone log in is exactly
/// who needs this most, and it discloses nothing an attacker could not infer from the served portal
/// bundle. Only the version/commit/build-time of this binary are exposed here — never configuration,
/// connection strings, or environment names.
/// </summary>
[ApiController]
[Route("api/v1/version")]
public sealed class VersionController : ControllerBase
{
    // Read once — the values are baked into the assembly at build time and cannot change while the
    // process lives.
    private static readonly VersionResponse Cached = Build();

    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(VersionResponse), StatusCodes.Status200OK)]
    public ActionResult<VersionResponse> Get() => Ok(Cached);

    private static VersionResponse Build()
    {
        var assembly = typeof(VersionController).Assembly;

        // InformationalVersion is the precise one — Directory.Build.props feeds it VersionPrefix from
        // the repo-root VERSION file, and the SDK appends the git sha: "1.4.0+abc1234". AssemblyVersion
        // is deliberately only MAJOR.0.0.0 (binding identity), so it is useless here.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        // Split "1.4.0+abc1234" into its version and commit halves. A local build with no SourceLink
        // sha has no '+' at all, in which case the commit is simply unknown.
        var plus = informational.IndexOf('+');
        var version = plus >= 0 ? informational[..plus] : informational;
        var commit = plus >= 0 ? informational[(plus + 1)..] : string.Empty;

        return new VersionResponse
        {
            Version = version,
            Commit = commit,
            InformationalVersion = informational,
            // The assembly's own mtime: when this binary was produced. Not process start time — that
            // would change on every restart and tell an operator nothing about which build they have.
            BuildDateUtc = GetBuildDateUtc(assembly),
        };
    }

    private static DateTime? GetBuildDateUtc(Assembly assembly)
    {
        try
        {
            var path = assembly.Location;
            // Single-file/trimmed publishes report an empty Location — there is no file to stat, so the
            // build date is simply unavailable rather than an error.
            return string.IsNullOrEmpty(path) ? null : System.IO.File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Wire shape of GET /api/v1/version.</summary>
    public sealed class VersionResponse
    {
        /// <summary>Product version, e.g. "1.4.0" or "1.4.0-rc.1". Matches the deployed image tag.</summary>
        public required string Version { get; init; }

        /// <summary>Git commit this binary was built from. Empty when built without source information.</summary>
        public required string Commit { get; init; }

        /// <summary>Version and commit exactly as stamped, e.g. "1.4.0+abc1234".</summary>
        public required string InformationalVersion { get; init; }

        /// <summary>When this binary was produced (UTC). Null when it cannot be determined.</summary>
        public DateTime? BuildDateUtc { get; init; }
    }
}
