using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// One process-wide <see cref="InMemoryDatabaseRoot"/> for test classes that only need an isolated DATABASE,
/// not an isolated EF service provider.
///
/// EF builds a distinct internal <c>IServiceProvider</c> per distinct options configuration and throws
/// <c>ManyServiceProvidersCreatedWarning</c> as an error once more than twenty exist in one process. This suite
/// already sits close to that ceiling, so every test class that news up its own root pushes an unrelated test
/// over it — the failure surfaces in whichever test happens to construct the twenty-first context, not in the
/// class that added one. Sharing this root keeps the provider count flat; isolation still comes from passing a
/// unique database NAME, which is all these tests actually need.
/// </summary>
internal static class SharedInMemoryDatabase
{
    public static readonly InMemoryDatabaseRoot Root = new();

    /// <summary>
    /// One explicit internal service provider, reused by every context built through <see cref="Options"/>.
    /// Passing it via <c>UseInternalServiceProvider</c> is what actually keeps EF from building (and counting)
    /// a fresh provider per options instance — sharing only the database root does not, since EF keys its
    /// provider cache on the options configuration rather than on the root.
    /// </summary>
    private static readonly IServiceProvider _internalServiceProvider =
        new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();

    /// <summary>A fresh database name, isolated from every other test's rows.</summary>
    public static string NewDatabaseName() => Guid.NewGuid().ToString();

    /// <summary>Options for an isolated in-memory database that costs the suite no extra service provider.</summary>
    public static DbContextOptions<TContext> Options<TContext>(string databaseName)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase(databaseName, Root)
            .UseInternalServiceProvider(_internalServiceProvider)
            // The shared provider makes this warning unreachable for these contexts, but a test that opts in
            // here should never be the one that trips it for someone else either.
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
}
