using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace FHIRBridge.UnitTests.Licensing;

/// <summary>
/// Exercises <see cref="LicenseEnforcementSaveChangesInterceptor"/> against a real <see cref="FHIRBridgeDbContext"/>
/// backed by EF Core's InMemory provider — same pattern as
/// <c>FHIRBridge.UnitTests.Infrastructure.DestinationConfigurationSoftDeleteTests</c> uses for
/// <c>AuditingSaveChangesInterceptor</c>. Proves the interceptor delegates to <see cref="ILicenseQuotaGuard"/>
/// for exactly the right dimension whenever a watched entity type is Added, and touches the guard not at all
/// otherwise (the cheap, no-DB-query common case).
/// </summary>
public sealed class LicenseEnforcementSaveChangesInterceptorTests
{
    // Shared InMemoryDatabaseRoot: see DestinationConfigurationSoftDeleteTests' identical remark — avoids
    // EF's ManyServiceProvidersCreatedWarning once many per-test unique databases accumulate.
    private static readonly InMemoryDatabaseRoot _root = new();

    private FHIRBridgeDbContext CreateContext(string databaseName, ILicenseQuotaGuard guard)
    {
        // AuditingSaveChangesInterceptor is required alongside the interceptor under test: it's what
        // populates the required CreatedBy audit column on every AuditableChildEntity, exactly like
        // DestinationConfigurationSoftDeleteTests already does for the same reason - without it every save
        // in these tests fails EF's required-property check, unrelated to licensing.
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));

        // LicenseEnforcementSaveChangesInterceptor resolves ILicenseQuotaGuard from a fresh
        // IServiceScopeFactory-created scope at save-time (see its own remarks for why it can't take the
        // guard via constructor injection) - a tiny standalone service provider with just the mock guard
        // registered is enough to exercise that lazily-resolved path.
        var guardProvider = new ServiceCollection().AddSingleton(guard).BuildServiceProvider();

        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(databaseName, _root)
            .AddInterceptors(
                new AuditingSaveChangesInterceptor(currentUser.Object),
                new LicenseEnforcementSaveChangesInterceptor(
                    guardProvider.GetRequiredService<IServiceScopeFactory>(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<LicenseEnforcementSaveChangesInterceptor>.Instance))
            .Options;

        return new FHIRBridgeDbContext(options);
    }

    [Fact]
    public async Task Adding_a_user_calls_the_user_quota_check_and_aborts_the_save_when_it_throws()
    {
        var guard = new Mock<ILicenseQuotaGuard>();
        guard.Setup(x => x.EnsureUserQuotaAvailableAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LicenseQuotaExceededException("users", 1, 1));

        await using var context = CreateContext(Guid.NewGuid().ToString(), guard.Object);
        context.Users.Add(new User("local:new@x.io", "new@x.io", "New User", Guid.NewGuid()));

        var act = () => context.SaveChangesAsync();

        await act.Should().ThrowAsync<LicenseQuotaExceededException>();
        guard.Verify(x => x.EnsureUserQuotaAvailableAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Adding_a_user_passes_through_when_the_guard_passes()
    {
        var guard = new Mock<ILicenseQuotaGuard>();
        guard.Setup(x => x.EnsureUserQuotaAvailableAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await using var context = CreateContext(Guid.NewGuid().ToString(), guard.Object);
        context.Users.Add(new User("local:new@x.io", "new@x.io", "New User", Guid.NewGuid()));

        await context.SaveChangesAsync();

        guard.Verify(x => x.EnsureUserQuotaAvailableAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Adding_a_source_connection_calls_the_source_connection_check_with_its_vendor_and_url()
    {
        var guard = new Mock<ILicenseQuotaGuard>();
        guard.Setup(x => x.EnsureSourceConnectionQuotaAvailableAsync(
                SourceSystemType.Epic, "https://fhir.example.org", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var context = CreateContext(Guid.NewGuid().ToString(), guard.Object);
        context.SourceConnections.Add(new SourceConnection(
            "Epic Prod",
            SourceSystemType.Epic,
            "https://fhir.example.org",
            new SourceAuthenticationConfiguration(AuthenticationType.None, null, null, [], null, null, null)));

        await context.SaveChangesAsync();

        guard.Verify(x => x.EnsureSourceConnectionQuotaAvailableAsync(
            SourceSystemType.Epic, "https://fhir.example.org", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Adding_a_destination_calls_the_destination_type_check()
    {
        var guard = new Mock<ILicenseQuotaGuard>();
        guard.Setup(x => x.EnsureDestinationTypeAllowedAsync(DestinationType.SqlServer, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var context = CreateContext(Guid.NewGuid().ToString(), guard.Object);
        context.DestinationConfigurations.Add(new DestinationConfiguration(
            "Warehouse", DestinationType.SqlServer, new SecretReference("kv", "secret"), "dbo.Patients"));

        await context.SaveChangesAsync();

        guard.Verify(x => x.EnsureDestinationTypeAllowedAsync(DestinationType.SqlServer, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Adding_a_route_calls_the_workflow_quota_check_and_the_resource_type_check_via_its_mapping_profile()
    {
        var databaseName = Guid.NewGuid().ToString();
        var noOpGuard = new Mock<ILicenseQuotaGuard>();
        noOpGuard.Setup(x => x.EnsureWorkflowQuotaAvailableAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        noOpGuard.Setup(x => x.EnsureResourceTypeAllowedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Guid sourceConnectionId;
        Guid destinationId;
        Guid mappingProfileId;

        // Mapping profile (and its source/destination) already persisted in an earlier, separate save — mirrors
        // real usage, where a route always references an already-saved mapping profile.
        await using (var setupContext = CreateContext(databaseName, noOpGuard.Object))
        {
            var source = new SourceConnection(
                "Epic Prod", SourceSystemType.Epic, "https://fhir.example.org",
                new SourceAuthenticationConfiguration(AuthenticationType.None, null, null, [], null, null, null));
            var destination = new DestinationConfiguration(
                "Warehouse", DestinationType.SqlServer, new SecretReference("kv", "secret"), "dbo.Patients");
            setupContext.SourceConnections.Add(source);
            setupContext.DestinationConfigurations.Add(destination);
            await setupContext.SaveChangesAsync();

            var mappingProfile = new MappingProfile(
                "Patient Mapping", "Patient", source.Id, destination.Id, "dbo.Patients", []);
            setupContext.MappingProfiles.Add(mappingProfile);
            await setupContext.SaveChangesAsync();

            sourceConnectionId = source.Id;
            destinationId = destination.Id;
            mappingProfileId = mappingProfile.Id;
        }

        var guard = new Mock<ILicenseQuotaGuard>();
        guard.Setup(x => x.EnsureWorkflowQuotaAvailableAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        guard.Setup(x => x.EnsureResourceTypeAllowedAsync("Patient", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await using (var context = CreateContext(databaseName, guard.Object))
        {
            context.ResourcePipelineRoutes.Add(new ResourcePipelineRoute(
                webhookConfigurationId: null,
                mappingProfileId: mappingProfileId,
                ingestionMode: IngestionMode.ScheduledPull,
                scheduleExpression: "0 0 * * *",
                searchParameters: null,
                isEnabled: true,
                priority: 0));

            await context.SaveChangesAsync();
        }

        guard.Verify(x => x.EnsureWorkflowQuotaAvailableAsync(It.IsAny<CancellationToken>()), Times.Once);
        guard.Verify(x => x.EnsureResourceTypeAllowedAsync("Patient", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Saving_with_no_watched_entities_never_touches_the_guard()
    {
        var guard = new Mock<ILicenseQuotaGuard>(MockBehavior.Strict);

        await using var context = CreateContext(Guid.NewGuid().ToString(), guard.Object);
        // Sample destination — not a watched-type change (this test asserts the cheap, common no-op path).
        context.SystemSettings.Add(new SystemSetting("Some:Key", "value", null));

        await context.SaveChangesAsync();

        guard.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Adding_a_workflow_definition_flagged_as_a_genuine_create_calls_the_workflow_quota_check()
    {
        var guard = new Mock<ILicenseQuotaGuard>();
        guard.Setup(x => x.EnsureWorkflowQuotaAvailableAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await using var context = CreateContext(Guid.NewGuid().ToString(), guard.Object);
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "Test Workflow", 1);
        // EF InMemory doesn't honor the real providers' HasDefaultValue("system") for CreatedBy (see
        // WorkflowPersistenceConfigurations' remarks) - SqlWorkflowDefinitionStore.SaveAsync always stamps
        // this explicitly in real usage, so the test does the same rather than relying on a DB default that
        // only exists against a real relational provider.
        workflow.StampAudit(DateTime.UtcNow, "test", null, null);
        context.WorkflowDefinitions.Add(workflow);
        context.NextWorkflowDefinitionAddIsGenuineCreate = true;

        await context.SaveChangesAsync();

        guard.Verify(x => x.EnsureWorkflowQuotaAvailableAsync(It.IsAny<CancellationToken>()), Times.Once);
        // The flag is consumed and reset so it never leaks into a later, unrelated save.
        context.NextWorkflowDefinitionAddIsGenuineCreate.Should().BeFalse();
    }

    [Fact]
    public async Task Adding_a_workflow_definition_NOT_flagged_as_a_genuine_create_never_touches_the_workflow_quota_check()
    {
        // Mirrors SqlWorkflowDefinitionStore's edit path: a same-id edit's Add reaches SaveChangesAsync with the
        // flag left at its default (false) — the delete half of the swap already committed in an earlier, separate
        // SaveChangesAsync call, so this one carries no evidence it's an edit other than the absent flag.
        var guard = new Mock<ILicenseQuotaGuard>(MockBehavior.Strict);

        await using var context = CreateContext(Guid.NewGuid().ToString(), guard.Object);
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "Edited Workflow", 1);
        workflow.StampAudit(DateTime.UtcNow, "test", null, null);
        context.WorkflowDefinitions.Add(workflow);

        await context.SaveChangesAsync();

        guard.VerifyNoOtherCalls();
    }
}
