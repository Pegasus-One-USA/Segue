using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Terminology;
using FHIRBridge.Infrastructure.Terminology.Hapi;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Covers the local MSSQL terminology cache that replaced the old "PUT the CodeSystem to the remote
/// HAPI server, then $lookup it over HTTP" path: <see cref="HapiLocalTerminologyWriter"/> writes a
/// synced CodeSystem's concepts locally (mirroring HAPI's own trm_codesystem/trm_codesystem_ver/
/// trm_concept schema), and <see cref="HapiLocalTerminologyLookupService"/> reads them back with one
/// indexed query — no network call.
/// </summary>
public sealed class HapiLocalTerminologyTests
{
    private static FHIRBridgeDbContext CreateContext(string databaseName, InMemoryDatabaseRoot root)
    {
        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(databaseName, root)
            .Options;
        return new FHIRBridgeDbContext(options);
    }

    private static HapiLocalTerminologyWriter CreateWriter(FHIRBridgeDbContext context) =>
        new(context, NullLogger<HapiLocalTerminologyWriter>.Instance);

    [Fact]
    public async Task Lookup_resolves_a_concept_written_by_the_writer()
    {
        var databaseName = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();

        await using (var writeContext = CreateContext(databaseName, root))
        {
            await CreateWriter(writeContext).WriteConceptsAsync(
                "http://loinc.org",
                "LOINC",
                "2.78",
                [("3141-9", "Body weight Measured")],
                CancellationToken.None);
        }

        await using var readContext = CreateContext(databaseName, root);
        var result = await new HapiLocalTerminologyLookupService(readContext)
            .LookupAsync("http://loinc.org", "3141-9", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Display.Should().Be("Body weight Measured");
        result.Version.Should().Be("2.78");
        result.Source.Should().Be("HapiLocalTerminologyDatabase");
    }

    [Fact]
    public async Task Lookup_returns_null_for_an_unsynced_code()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot());

        var result = await new HapiLocalTerminologyLookupService(context)
            .LookupAsync("http://loinc.org", "unknown-code", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Re_syncing_the_same_version_replaces_its_concepts_rather_than_accumulating_duplicates()
    {
        var databaseName = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();
        var writer1 = CreateWriter(CreateContext(databaseName, root));
        await writer1.WriteConceptsAsync(
            "http://hl7.org/fhir/sid/cvx", "CVX", version: null, [("140", "Influenza, seasonal, injectable")], CancellationToken.None);

        // A second "Run Now" for the same (versionless) CodeSystem should supersede the prior concepts,
        // not duplicate them — this is what makes repeated manual syncs idempotent.
        var writer2 = CreateWriter(CreateContext(databaseName, root));
        await writer2.WriteConceptsAsync(
            "http://hl7.org/fhir/sid/cvx", "CVX", version: null, [("140", "Influenza, seasonal, injectable, preservative free")], CancellationToken.None);

        await using var readContext = CreateContext(databaseName, root);
        readContext.TrmConcepts.Count().Should().Be(1);
        var result = await new HapiLocalTerminologyLookupService(readContext)
            .LookupAsync("http://hl7.org/fhir/sid/cvx", "140", CancellationToken.None);
        result!.Display.Should().Be("Influenza, seasonal, injectable, preservative free");
    }

    [Fact]
    public async Task Writing_a_large_batch_does_not_detach_other_entities_the_caller_is_tracking_on_the_same_context()
    {
        // Regression test: HapiTerminologyConfigurationService.RunAndRecordHistoryAsync tracks a
        // HapiTerminologyImportHistory row on the SAME scoped DbContext that HapiLocalTerminologyWriter
        // uses for its own batched inserts. The writer used to call ChangeTracker.Clear() to bound
        // memory across large batches — which detached EVERY tracked entity on the shared context,
        // including that history row, so its later history.Complete(...) + SaveChangesAsync silently
        // wrote nothing and the row stayed stuck at Status="Running" forever even though the concepts
        // themselves synced successfully. The fix detaches only the writer's own batch entities.
        await using var context = CreateContext(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot());

        var history = new HapiTerminologyImportHistory("Snomed");
        context.HapiTerminologyImportHistory.Add(history);
        await context.SaveChangesAsync();

        // More than one insert batch's worth of concepts, to exercise FlushBatchAsync more than once.
        var concepts = Enumerable.Range(0, 12_000).Select(i => (Code: i.ToString(), Display: $"Concept {i}"));
        await CreateWriter(context).WriteConceptsAsync(
            "http://snomed.info/sct", "SNOMEDCT", "20260901", concepts, CancellationToken.None);

        history.Complete(12_000, "20260901");
        await context.SaveChangesAsync();

        var persisted = await context.HapiTerminologyImportHistory.AsNoTracking().SingleAsync(x => x.Id == history.Id);
        persisted.Status.Should().Be("Succeeded");
        persisted.CompletedOnUtc.Should().NotBeNull();
        persisted.ImportedConceptCount.Should().Be(12_000);
    }

    [Fact]
    public async Task RunAndRecordHistoryAsync_marks_history_failed_even_when_the_failed_sync_leaves_poisoned_tracked_entities()
    {
        // Regression test for the real bug behind every "stuck at Running forever" row this session
        // (e.g. the HCPCS Display-truncation failure): when a sync's own SaveChangesAsync call throws
        // partway through a batch, the entities in that batch stay tracked as "Added" — EF doesn't roll
        // that tracking back on failure. Without discarding them, the catch block's history.Fail(...)
        // never reaches the database: the finally block's own SaveChangesAsync tries to re-save the
        // same still-broken batch, hits the identical error a second time, and THAT exception
        // propagates out uncaught, silently discarding the history.Fail(...) write along with it.
        await using var db = CreateContext(Guid.NewGuid().ToString(), new InMemoryDatabaseRoot());

        var services = new ServiceCollection();
        services.AddSingleton<IHapiCvxTerminologySyncService>(new ThrowingCvxSyncService(db));
        var serviceProvider = services.BuildServiceProvider();

        var service = new HapiTerminologyConfigurationService(
            new HapiTerminologySystemRegistry(),
            Mock.Of<ISystemSettingsCache>(),
            Mock.Of<ISystemSettingsService>(),
            Mock.Of<ISecretWriter>(),
            Mock.Of<IAppSecretMetadataProvider>(),
            db,
            serviceProvider,
            NullLogger<HapiTerminologyConfigurationService>.Instance);

        await service.RunAndRecordHistoryAsync("Cvx", CancellationToken.None);

        var history = await db.HapiTerminologyImportHistory.AsNoTracking().SingleAsync();
        history.Status.Should().Be("Failed");
        history.CompletedOnUtc.Should().NotBeNull();
    }

    /// <summary>Forces a null into a required (NOT NULL) column on an otherwise normal, non-conflicting
    /// tracked entity — a plain data-validity rejection, not an identity/key conflict — matching how the
    /// real HCPCS Display-truncation failure behaved: the row is perfectly valid as far as the local
    /// change tracker is concerned, but the store rejects its data, leaving it stuck tracked as "Added".</summary>
    private sealed class ThrowingCvxSyncService(FHIRBridgeDbContext db) : IHapiCvxTerminologySyncService
    {
        public async Task<HapiCvxSyncResult> SyncAsync(CancellationToken cancellationToken)
        {
            var codeSystem = new TrmCodeSystem("http://hl7.org/fhir/sid/cvx", "CVX");
            db.TrmCodeSystems.Add(codeSystem);
            await db.SaveChangesAsync(cancellationToken);

            var invalidVersion = new TrmCodeSystemVer(codeSystem.Pid, "placeholder", "CVX");
            db.TrmCodeSystemVers.Add(invalidVersion);
            db.Entry(invalidVersion).Property(nameof(TrmCodeSystemVer.CsVersionId)).CurrentValue = null;
            await db.SaveChangesAsync(cancellationToken);

            return new HapiCvxSyncResult(0, 0, TimeSpan.Zero);
        }
    }
}
