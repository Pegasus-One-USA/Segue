using FHIRBridge.Application.Abstractions.Aggregation;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Licensing;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Aggregation;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Sources only read and destinations only write: a write-only connection (an EHR Write-Back target listed under
/// Destination Connections) is kept out of every read path and the source-connection licence count, the lists filter
/// on which side a connection serves, and an athenaOne write connection carries its own department.
/// </summary>
public sealed class SourceConnectionWriteSideTests
{
    private readonly InMemoryConfigurationRepository _repository = new(TestHelpers.LicenseTestScopeFactory.Create());
    private readonly ConfigurationService _sut;

    public SourceConnectionWriteSideTests()
    {
        _sut = new ConfigurationService(
            _repository,
            new InMemorySourceCapabilityRepository(),
            Mock.Of<ISourceCapabilityDiscoveryService>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Security.ISecretWriter>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Security.ISecretProvider>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Destinations.ISqlConnectionSecretMerger>(),
            new FHIRBridge.UnitTests.Security.PassthroughTenantSecretVaultResolver(),
            Mock.Of<IParentReferenceResolver>(),
            new CreateMappingProfileRequestValidator(
                new FHIRBridge.UnitTests.Validation.NoOpDestinationSchemaService(),
                _repository,
                new FHIRBridge.UnitTests.Validation.NoOpEffectiveRuleResolver()),
            new CreateDestinationConfigurationRequestValidator(),
            new FHIRBridge.UnitTests.Security.PassthroughUserDisplayNameResolver(),
            NullLogger<ConfigurationService>.Instance);
    }

    private static readonly SourceAuthenticationDto BackendAuth = new(
        AuthenticationType.SmartBackendServices,
        ClientId: "client-1",
        TokenEndpoint: "https://auth.example.com/token",
        Scopes: ["system/Patient.read"],
        ClientSecretKeyVaultName: null,
        ClientSecretName: null,
        PrivateKeyKeyVaultName: "signing-keys",
        PrivateKeySecretName: "athena-key",
        KeyId: "key-1");

    private static CreateSourceConnectionRequest AthenaRequest(
        string name, SourceConnectionAccess? access, string? departmentId) =>
        new(name, SourceSystemType.Athenahealth, "https://api.preview.platform.athenahealth.com/fhir/r4", BackendAuth,
            ApplicationType.Backend, Access: access, DepartmentId: departmentId);

    private static SourceConnection Connection(string name, SourceConnectionAccess access) =>
        new(name, SourceSystemType.Epic, "https://fhir.example.com",
            new SourceAuthenticationConfiguration(AuthenticationType.SmartBackendServices, "client", "https://auth/token", [], null, null, "kid"),
            ApplicationType.Backend,
            access: access);

    // ── DepartmentId ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_write_connection_keeps_its_department_until_an_update_clears_it()
    {
        var name = $"athena write {Guid.NewGuid():N}";
        var created = await _sut.AddSourceConnectionAsync(AthenaRequest(name, SourceConnectionAccess.Write, " 150 "), CancellationToken.None);
        created.DepartmentId.Should().Be("150");

        // Null keeps the saved value.
        var kept = await _sut.UpdateSourceConnectionAsync(created.Id, AthenaRequest(name, null, null), CancellationToken.None);
        kept.DepartmentId.Should().Be("150");
        kept.Access.Should().Be(SourceConnectionAccess.Write);

        var changed = await _sut.UpdateSourceConnectionAsync(created.Id, AthenaRequest(name, null, "2"), CancellationToken.None);
        changed.DepartmentId.Should().Be("2");

        // Empty clears it.
        var cleared = await _sut.UpdateSourceConnectionAsync(created.Id, AthenaRequest(name, null, ""), CancellationToken.None);
        cleared.DepartmentId.Should().BeNull();
        (await _sut.GetSourceConnectionByIdAsync(created.Id, CancellationToken.None))!.DepartmentId.Should().BeNull();
    }

    [Fact]
    public async Task A_connection_that_cannot_write_keeps_no_department()
    {
        var name = $"athena read {Guid.NewGuid():N}";
        var created = await _sut.AddSourceConnectionAsync(AthenaRequest(name, null, "150"), CancellationToken.None);
        created.Access.Should().Be(SourceConnectionAccess.Read);
        created.DepartmentId.Should().BeNull();

        var updated = await _sut.UpdateSourceConnectionAsync(created.Id, AthenaRequest(name, null, "150"), CancellationToken.None);
        updated.DepartmentId.Should().BeNull();
    }

    [Fact]
    public async Task Dropping_write_access_clears_the_department()
    {
        var name = $"athena rw {Guid.NewGuid():N}";
        var created = await _sut.AddSourceConnectionAsync(AthenaRequest(name, SourceConnectionAccess.ReadWrite, "150"), CancellationToken.None);

        var updated = await _sut.UpdateSourceConnectionAsync(created.Id, AthenaRequest(name, SourceConnectionAccess.Read, null), CancellationToken.None);

        updated.DepartmentId.Should().BeNull();
    }

    [Theory]
    [InlineData("15 0")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public async Task A_department_that_is_not_an_ehr_id_is_refused(string departmentId)
    {
        var act = () => _sut.AddSourceConnectionAsync(
            AthenaRequest($"athena bad {Guid.NewGuid():N}", SourceConnectionAccess.Write, departmentId), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Department ID*");
    }

    [Fact]
    public void The_entity_refuses_a_department_on_a_read_only_connection()
    {
        var act = () => Connection("read", SourceConnectionAccess.Read).SetDepartmentId("150");

        act.Should().Throw<InvalidOperationException>();
    }

    // ── Access filter ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SourceConnectionAccessFilter.Read, new[] { "read", "readwrite" })]
    [InlineData(SourceConnectionAccessFilter.Write, new[] { "readwrite", "write" })]
    [InlineData(null, new[] { "read", "readwrite", "write" })]
    public async Task The_paged_list_filters_on_the_side_a_connection_serves(SourceConnectionAccessFilter? access, string[] expected)
    {
        await _repository.AddSourceConnectionAsync(Connection("read", SourceConnectionAccess.Read), CancellationToken.None);
        await _repository.AddSourceConnectionAsync(Connection("write", SourceConnectionAccess.Write), CancellationToken.None);
        await _repository.AddSourceConnectionAsync(Connection("readwrite", SourceConnectionAccess.ReadWrite), CancellationToken.None);

        var page = await _repository.GetSourceConnectionsPagedAsync(
            new SourceConnectionFilter(null, null, null, null, access), 1, 25, null, null, CancellationToken.None);

        page.Items.Select(x => x.Name).Should().BeEquivalentTo(expected);
        page.TotalCount.Should().Be(expected.Length);
    }

    [Fact]
    public async Task The_ef_repository_filters_on_the_side_a_connection_serves()
    {
        await using var context = EfContext();
        var repository = new EfConfigurationRepository(context);
        await repository.AddSourceConnectionAsync(Connection("read", SourceConnectionAccess.Read), CancellationToken.None);
        await repository.AddSourceConnectionAsync(Connection("write", SourceConnectionAccess.Write), CancellationToken.None);
        await repository.AddSourceConnectionAsync(Connection("readwrite", SourceConnectionAccess.ReadWrite), CancellationToken.None);

        var readable = await repository.GetSourceConnectionsPagedAsync(
            new SourceConnectionFilter(null, null, null, null, SourceConnectionAccessFilter.Read), 1, 25, null, null, CancellationToken.None);
        var writable = await repository.GetSourceConnectionsPagedAsync(
            new SourceConnectionFilter(null, null, null, null, SourceConnectionAccessFilter.Write), 1, 25, null, null, CancellationToken.None);

        readable.Items.Select(x => x.Name).Should().BeEquivalentTo("read", "readwrite");
        writable.Items.Select(x => x.Name).Should().BeEquivalentTo("readwrite", "write");
    }

    private static FHIRBridgeDbContext EfContext()
    {
        var currentUser = new Mock<FHIRBridge.Application.Abstractions.Security.ICurrentUserService>();
        currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new FHIRBridge.Application.Abstractions.Security.CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));
        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            // Shares an existing root: each distinct root makes EF build another internal service provider, and past
            // twenty EF fails whichever test crosses the line (see DestinationConfigurationSoftDeleteTests).
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), FHIRBridge.UnitTests.Licensing.LicenseEnforcementSaveChangesInterceptorTests._root)
            .AddInterceptors(new AuditingSaveChangesInterceptor(currentUser.Object))
            .Options;
        return new FHIRBridgeDbContext(options);
    }


    // ── Licence ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_only_connections_are_not_counted_as_source_connections()
    {
        var configuration = new Mock<IConfigurationRepository>();
        configuration.Setup(x => x.GetSourceConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Connection("read", SourceConnectionAccess.Read),
            Connection("write", SourceConnectionAccess.Write),
            Connection("readwrite", SourceConnectionAccess.ReadWrite),
        ]);
        configuration.Setup(x => x.GetRoutesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var users = new Mock<IUserAccessRepository>();
        users.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var tenants = new Mock<ITenantRepository>();
        tenants.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var workflows = new Mock<IWorkflowDefinitionStore>();
        workflows.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<WorkflowDefinition>());

        var counts = await new LicenseUsageCountsProvider(users.Object, configuration.Object, tenants.Object, workflows.Object)
            .GetCurrentCountsAsync(CancellationToken.None);

        counts.SourceConnectionCount.Should().Be(2);
    }

    // ── Read guards ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patient_aggregation_refuses_a_write_only_connection()
    {
        var write = Connection("write", SourceConnectionAccess.Write);
        var service = AggregationOver(write);

        var act = () => service.GetEverythingAsync("p1", [], write.Id, CancellationToken.None);

        await act.Should().ThrowAsync<SourceConnectionUnavailableException>().WithMessage("*write-only*");
    }

    [Fact]
    public async Task Patient_aggregation_never_picks_a_write_only_connection_by_itself()
    {
        var service = AggregationOver(Connection("write", SourceConnectionAccess.Write));

        var act = () => service.GetEverythingAsync("p1", [], null, CancellationToken.None);

        await act.Should().ThrowAsync<SourceConnectionUnavailableException>().WithMessage("*no enabled source connection*");
    }

    [Fact]
    public async Task A_scheduled_route_on_a_write_only_connection_reports_why_it_did_not_run()
    {
        var write = Connection("ehr write", SourceConnectionAccess.Write);
        var destination = new DestinationConfiguration(
            "SQL", DestinationType.SqlServer, new SecretReference("vault", "sql-conn"), "dbo.Observations");
        var mapping = new MappingProfile(
            "Observation to SQL", "Observation", write.Id, destination.Id, "dbo.Observations", Array.Empty<MappingField>());
        var route = new ResourcePipelineRoute(null, mapping.Id, IngestionMode.ScheduledPull, "0 * * * *", null, true, 1);
        var configuration = new Mock<IConfigurationRepository>();
        configuration.Setup(x => x.GetSourceConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([write]);
        configuration.Setup(x => x.GetSourceConfigurationsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        configuration.Setup(x => x.GetDestinationsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([destination]);
        configuration.Setup(x => x.GetMappingProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([mapping]);
        configuration.Setup(x => x.GetRoutesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([route]);
        configuration.Setup(x => x.GetWebhooksAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var sourceClients = new Mock<IFhirSourceClientFactory>(MockBehavior.Strict);
        var licenceGuard = new Mock<FHIRBridge.Application.Abstractions.Licensing.ILicenseQuotaGuard>();
        var service = new FHIRBridge.Infrastructure.Pipeline.ConfiguredPipelineService(
            configuration.Object,
            sourceClients.Object,
            Mock.Of<IJsonMappingEngine>(),
            Mock.Of<IMappingMaterializer>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Destinations.IConfiguredDestinationWriterFactory>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Security.ISecretProvider>(),
            Mock.Of<IConfiguredPipelineRunRepository>(),
            Mock.Of<IPipelineRunRouteExecutionRepository>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Pipeline.IExecutionResourceHistoryRecorder>(),
            NullLogger<FHIRBridge.Infrastructure.Pipeline.ConfiguredPipelineService>.Instance,
            Mock.Of<FHIRBridge.Application.Abstractions.Pipeline.IPipelineRunTracker>(),
            licenceGuard.Object);

        // As the worker's dispatcher sends it: due routes only, by id.
        var run = await service.StartAsync(
            new StartConfiguredPipelineRunRequest(null, "scheduler", null) { RunDueSchedulesOnly = true, RouteIds = [route.Id] },
            CancellationToken.None);

        run.Status.Should().Be("Failed");
        run.Errors.Should().ContainSingle().Which.Should().Contain("'ehr write' is write-only");
        // The licence pre-check only covers what the run reads from and writes to, so the skipped route is left out.
        licenceGuard.Verify(x => x.EnsureSourceConnectionStillAllowedAsync(
            It.IsAny<SourceSystemType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        licenceGuard.Verify(x => x.EnsureDestinationTypeAllowedAsync(
            It.IsAny<DestinationType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_in_memory_store_counts_a_write_only_connection_only_once_it_can_read()
    {
        var guard = new Mock<FHIRBridge.Application.Abstractions.Licensing.ILicenseQuotaGuard>();
        guard.Setup(x => x.EnsureSourceConnectionQuotaAvailableAsync(
                It.IsAny<SourceSystemType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Source connection quota reached."));
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, guard.Object);
        var scopeFactory = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));
        var repository = new InMemoryConfigurationRepository(scopeFactory);
        var write = Connection("write", SourceConnectionAccess.Write);

        await repository.AddSourceConnectionAsync(write, CancellationToken.None);
        write.SetAccess(SourceConnectionAccess.ReadWrite);
        var promote = () => repository.UpdateSourceConnectionAsync(write, CancellationToken.None);
        var addRead = () => repository.AddSourceConnectionAsync(Connection("read", SourceConnectionAccess.Read), CancellationToken.None);

        await promote.Should().ThrowAsync<InvalidOperationException>().WithMessage("*quota*");
        await addRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("*quota*");
        (await repository.GetSourceConnectionAsync(write.Id, CancellationToken.None))!.Access.Should().Be(SourceConnectionAccess.Write);
    }

    [Fact]
    public async Task A_read_only_connection_ignores_a_malformed_department_instead_of_refusing_the_save()
    {
        var created = await _sut.AddSourceConnectionAsync(
            AthenaRequest($"athena read {Guid.NewGuid():N}", SourceConnectionAccess.Read, "15 0"), CancellationToken.None);
        var updated = await _sut.UpdateSourceConnectionAsync(
            created.Id, AthenaRequest(created.Name, null, "15 0"), CancellationToken.None);

        created.DepartmentId.Should().BeNull();
        updated.DepartmentId.Should().BeNull();
    }

    // ── Which edits touch the write side ─────────────────────────────────────────

    private static SourceConnectionDto SavedWriteConnection(SourceConnectionAccess access = SourceConnectionAccess.ReadWrite) =>
        new(Guid.NewGuid(), "athena", SourceSystemType.Athenahealth, "https://api.preview.platform.athenahealth.com/fhir/r4",
            BackendAuth with { PrivateKeyKeyVaultName = "signing-keys", PrivateKeySecretName = "athena-key" }, true,
            ApplicationType.Backend, Access: access, DepartmentId: "150");

    [Fact]
    public void Resaving_a_write_connection_from_a_source_form_does_not_touch_its_write_side()
    {
        var saved = SavedWriteConnection();
        // A source form: no access, department or activation, no secret reference (keeps the saved one), new scopes,
        // and endpoint/credential values that need not match the saved row exactly (a legacy null AuthPlacement
        // against the portal's 'post', another base URL or client id): those stay under the vendor's own right.
        var request = AthenaRequest("athena", null, null) with
        {
            BaseUrl = "https://elsewhere.example.com/fhir",
            Authentication = BackendAuth with
            {
                PrivateKeyKeyVaultName = null, PrivateKeySecretName = null, Scopes = ["system/*.read"], ClientId = "other", AuthPlacement = "post",
            },
        };

        SourceConnectionWriteSide.UpdateChangesWriteSide(saved, request).Should().BeFalse();
        SourceConnectionWriteSide.UpdateChangesWriteSide(saved, request with { Access = SourceConnectionAccess.ReadWrite, DepartmentId = " 150 " })
            .Should().BeFalse();
    }

    [Fact]
    public void Access_department_and_activation_changes_touch_the_write_side()
    {
        var saved = SavedWriteConnection();
        var same = AthenaRequest("athena", null, null);

        SourceConnectionWriteSide.UpdateChangesWriteSide(saved, same with { Access = SourceConnectionAccess.Read }).Should().BeTrue();
        SourceConnectionWriteSide.UpdateChangesWriteSide(saved, same with { DepartmentId = "151" }).Should().BeTrue();
        SourceConnectionWriteSide.UpdateChangesWriteSide(saved, same with { DepartmentId = "" }).Should().BeTrue();
        SourceConnectionWriteSide.UpdateChangesWriteSide(saved with { VendorWriteApisActivated = true }, same with { VendorWriteApisActivated = false })
            .Should().BeTrue();
        SourceConnectionWriteSide.UpdateChangesWriteSide(saved with { VendorWriteApisActivated = true }, same with { VendorWriteApisActivated = true })
            .Should().BeFalse();
    }

    [Fact]
    public void A_read_only_connection_has_no_write_side()
    {
        var saved = SavedWriteConnection(SourceConnectionAccess.Read);

        SourceConnectionWriteSide.UpdateChangesWriteSide(saved, AthenaRequest("athena", null, "151")).Should().BeFalse();
    }

    private static PatientAggregationService AggregationOver(params SourceConnection[] connections)
    {
        var configuration = new Mock<IConfigurationRepository>();
        configuration.Setup(x => x.GetSourceConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(connections);
        return new PatientAggregationService(
            configuration.Object,
            Mock.Of<IFhirSourceClientFactory>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Security.ISecretProvider>(),
            NullLogger<PatientAggregationService>.Instance);
    }
}
