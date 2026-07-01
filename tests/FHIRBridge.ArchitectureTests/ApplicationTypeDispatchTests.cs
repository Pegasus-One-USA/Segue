using System.Text.RegularExpressions;
using FluentAssertions;

namespace FHIRBridge.ArchitectureTests;

/// <summary>
/// The application-type axis must be dispatched through <c>ISourceApplicationStrategyRegistry</c>, never a switch
/// statement, case label, or switch-expression arm on <c>ApplicationType</c>. This keeps the engine closed for
/// modification — a new application type is a new strategy plus one registration. A failure here means someone
/// re-introduced centralized branching on the enum (the anti-pattern the strategy registry replaced).
/// </summary>
public sealed partial class ApplicationTypeDispatchTests
{
    // Matches: switch (…ApplicationType…), case ApplicationType.Foo, or "ApplicationType.Foo =>" (switch-expression arm).
    // Does NOT match "=> ApplicationType.Foo" (a strategy's Handles property), because the arrow precedes the enum.
    [GeneratedRegex(@"switch\s*\([^)]*ApplicationType[^)]*\)|case\s+ApplicationType\.|ApplicationType\.[A-Za-z]+\s*=>")]
    private static partial Regex ForbiddenDispatch();

    [Fact]
    public void No_engine_code_switches_on_ApplicationType()
    {
        var srcRoot = LocateSourceRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (ForbiddenDispatch().IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetRelativePath(srcRoot, file));
            }
        }

        offenders.Should().BeEmpty(
            "application-type behaviour must be resolved from ISourceApplicationStrategyRegistry, not a switch/case/switch-expression on ApplicationType");
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
