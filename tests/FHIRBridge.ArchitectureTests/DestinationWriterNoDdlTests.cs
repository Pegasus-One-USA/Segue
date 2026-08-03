using FluentAssertions;

namespace FHIRBridge.ArchitectureTests;

/// <summary>
/// FHIRBridge never owns a customer's destination schema (docs/backend/11-destination-schema-ownership-plan.md):
/// destination writers and SQL connection factories must never create, alter, or drop objects in the customer's
/// database — only write into columns the customer explicitly mapped. A failure here means DDL crept back into
/// a writer, the exact class of bug this guardrail exists to catch.
///
/// The one known, tracked exception is CDC mode's companion "{Table}_Cdc" table (section 3.A.3, an open decision
/// on whether to retire CDC or require a customer-provisioned table). Until that decision lands, its DDL line
/// must carry a "// ddl-allowed: &lt;reason&gt;" comment within a few lines above it — any other DDL keyword found
/// anywhere else in these files fails the test unsuppressed.
/// </summary>
public sealed class DestinationWriterNoDdlTests
{
    private static readonly string[] DdlKeywords =
        ["CREATE TABLE", "CREATE DATABASE", "CREATE SCHEMA", "DROP TABLE", "DROP DATABASE", "DROP SCHEMA", "ALTER TABLE"];

    private const string SuppressionMarker = "// ddl-allowed:";
    private const int SuppressionLookbackLines = 15;

    // SqlDestinationSchemaService is not a "destination writer" or "connection factory" in the sense this
    // guardrail protects — it's the mapping canvas's design-time schema-authoring service (Add to Pipeline:
    // create table, add/alter/drop column), whose entire purpose is executing DDL the user explicitly requested.
    // Marking every DDL line in it "ddl-allowed" would be noise, not a meaningful exception list.
    private static readonly string[] ExcludedFileNames =
        ["SqlDestinationSchemaService.cs", "SqlServerMappingSchemaTransaction.cs"];

    [Fact]
    public void Destination_writers_contain_no_unsuppressed_DDL()
    {
        var srcRoot = LocateSourceRoot();
        var targetDirs = new[]
        {
            Path.Combine(srcRoot, "FHIRBridge.Infrastructure", "Destinations"),
            Path.Combine(srcRoot, "BuildingBlocks", "FHIRBridge.Integration", "Sql"),
        };

        var offenders = new List<string>();

        foreach (var dir in targetDirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(file => !ExcludedFileNames.Contains(Path.GetFileName(file))))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!DdlKeywords.Any(keyword => lines[i].Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (IsSuppressed(lines, i))
                    {
                        continue;
                    }

                    offenders.Add($"{Path.GetRelativePath(srcRoot, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "destination writers/connection factories must never create, alter, or drop customer database objects — " +
            "FHIRBridge only writes into explicitly mapped columns; any exception must carry a " +
            "'// ddl-allowed: <reason>' comment above it");
    }

    private static bool IsSuppressed(string[] lines, int lineIndex)
    {
        var start = Math.Max(0, lineIndex - SuppressionLookbackLines);
        for (var i = start; i <= lineIndex; i++)
        {
            if (lines[i].Contains(SuppressionMarker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
