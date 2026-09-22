using System.Data.Common;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Licensing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FHIRBridge.UnitTests.Licensing;

/// <summary>
/// Covers the PR #207 review finding: once <c>ReloadAsync</c> runs periodically (not just once, at
/// startup), a transient database failure reading the stored license token must not be treated the same
/// as "no token configured" — doing so would downgrade <c>Current</c> to Unlicensed and 403 every
/// <c>/api/v1</c> request behind the license gate for up to the reload interval, on every DB blip.
/// </summary>
public sealed class LicenseServiceTests
{
    [Fact]
    public async Task First_load_still_tolerates_a_DB_read_failure_exactly_like_before()
    {
        var repository = new FakeSystemSettingRepository(_ => throw new FakeDbException());
        var service = new LicenseService(BuildScopeFactory(repository), NullLogger<LicenseService>.Instance);

        await service.ReloadAsync(CancellationToken.None);

        // The genuine startup race (SystemSettings table not migrated yet) this tolerance exists for —
        // falls through to "no token", exactly as it always did.
        service.Current.State.Should().Be(FHIRBridge.Application.Abstractions.Licensing.LicenseState.Invalid);
        service.CurrentRawToken.Should().BeNull();
    }

    [Fact]
    public async Task A_later_DB_read_failure_leaves_the_current_license_state_unchanged()
    {
        var callCount = 0;
        var repository = new FakeSystemSettingRepository(_ =>
        {
            callCount++;
            // First call: a syntactically-real-but-unsigned-by-us token, so the first load resolves to a
            // distinctive Invalid reason (NOT "No license token was provided.", which is what a DB failure
            // would ALSO produce — using that value would make the bug and the fix look identical).
            return callCount == 1
                ? new SystemSetting("License:Token", "not-a-real-signed-token", null)
                : throw new FakeDbException();
        });
        var service = new LicenseService(BuildScopeFactory(repository), NullLogger<LicenseService>.Instance);

        await service.ReloadAsync(CancellationToken.None);
        var afterFirstLoad = service.Current;
        var afterFirstLoadRawToken = service.CurrentRawToken;

        afterFirstLoad.State.Should().Be(FHIRBridge.Application.Abstractions.Licensing.LicenseState.Invalid);
        afterFirstLoad.InvalidReason.Should().NotBe("No license token was provided.");

        // Second reload — the DB read now fails. Before the fix, this fell through to "no token", produced
        // a FRESH Invalid("No license token was provided.") status, and overwrote Current with it.
        await service.ReloadAsync(CancellationToken.None);

        service.Current.Should().BeSameAs(afterFirstLoad, "a transient DB failure must not replace the last known-good license state");
        service.CurrentRawToken.Should().Be(afterFirstLoadRawToken);
    }

    private static IServiceScopeFactory BuildScopeFactory(ISystemSettingRepository repository)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => repository);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class FakeSystemSettingRepository(Func<string, SystemSetting?> getByKey) : ISystemSettingRepository
    {
        public Task<IReadOnlyList<SystemSetting>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SystemSetting>>([]);

        public Task<SystemSetting?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(getByKey(key));

        public Task<SystemSetting> UpsertAsync(string key, string value, string? description, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    /// <summary>Minimal concrete <see cref="DbException"/> — the type is abstract, and no fake of it exists
    /// elsewhere in this suite. Stands in for SqlException/NpgsqlException, both of which derive from it.</summary>
    private sealed class FakeDbException : DbException;
}
