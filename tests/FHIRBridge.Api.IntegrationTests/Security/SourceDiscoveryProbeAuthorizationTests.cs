using System.Security.Claims;
using FHIRBridge.Api.Controllers.V1;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Api.IntegrationTests.TestHelpers;
using FHIRBridge.SharedKernel.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Security;

/// <summary>
/// Focused tests for SourceDiscoveryController.Probe's vendor-aware authorization
/// (IsAuthorizedForProbeAsync): a caller may reach the probe via the generic
/// sourceconnections.create/edit permission (unchanged prior behavior) OR, when an existing connection
/// was selected, via that connection's own resolved vendor's create/edit permission — never a
/// different vendor's.
///
/// Built against a REAL IAuthorizationService (real policies, real handlers —
/// SourceDiscoveryAccessAuthorizationHandler, PermissionAuthorizationHandler) rather than a hand-rolled
/// fake of the authorization decision, so these exercise the actual registered policy machinery. Uses
/// the real InMemoryConfigurationRepository (the same class the app itself registers when no database
/// is configured) to resolve a connection's vendor, rather than a hand-written repository fake.
///
/// Does not use ApiFixture/WebApplicationFactory — this environment's integration-test host has
/// separate, pre-existing bootstrapping failures unrelated to this change (see the RBAC investigation
/// history). The controller is constructed directly with a real DI-built IAuthorizationService instead,
/// which needs no web host at all.
/// </summary>
public sealed class SourceDiscoveryProbeAuthorizationTests
{
    private static readonly SourceAuthenticationConfiguration Auth =
        new(AuthenticationType.OAuthClientCredentials, "client-1", null, ["system/*.read"], null, null, null);

    private sealed class FakeCurrentUserService : ICurrentUserService
    {
        public FakeCurrentUserService(Guid userId) =>
            CurrentUser = new CurrentUserInfo(null, "test@example.com", "Test User", [], true, UserId: userId);

        public CurrentUserInfo CurrentUser { get; }
    }

    private sealed class FakePermissionsProvider : IUserPermissionsProvider
    {
        private readonly string[] _codes;
        public FakePermissionsProvider(params string[] codes) => _codes = codes;

        public Task<IReadOnlyList<string>> GetEffectivePermissionCodesAsync(Guid userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(_codes);
    }

    private sealed class NoOpProbeService : ISourceEndpointProbeService
    {
        public Task<SmartConfigurationDto> ProbeSmartConfigurationAsync(string baseUrl, CancellationToken cancellationToken)
            => Task.FromResult(SmartConfigurationDto.Empty);

        public Task<IReadOnlyList<string>> ProbeSupportedResourceTypesAsync(string baseUrl, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoOpBackendAuthScopeProbeService : IBackendAuthScopeProbeService
    {
        public Task<BackendAuthScopesResult> ProbeGrantedScopesAsync(BackendAuthScopesRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by these tests — Probe is the only action under test.");
    }

    /// <summary>Builds a real IAuthorizationService wired with the actual policy/handler pair this
    /// controller depends on (SourceDiscoveryAccess) plus a HasPermission:{code} policy for every code
    /// a test grants — the same shape Program.cs registers, scoped down to what these tests need.</summary>
    private static IAuthorizationService BuildRealAuthorizationService(Guid userId, params string[] grantedCodes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUserService>(new FakeCurrentUserService(userId));
        services.AddSingleton<IUserPermissionsProvider>(new FakePermissionsProvider(grantedCodes));
        services.AddScoped<IAuthorizationHandler, SourceDiscoveryAccessAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicies.SourceDiscoveryAccess, policy =>
                policy.AddRequirements(new SourceDiscoveryAccessRequirement()));

            foreach (var code in new[]
            {
                "epic.create", "epic.edit", "athenahealth.create", "athenahealth.edit",
                "sourceconnections.create", "sourceconnections.edit",
            })
            {
                options.AddPolicy(AuthorizationPolicies.HasPermission(code), policy =>
                    policy.AddRequirements(new PermissionRequirement(code)));
            }
        });

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static SourceDiscoveryController BuildController(
        IAuthorizationService authorizationService,
        IConfigurationRepository configurationRepository)
    {
        var controller = new SourceDiscoveryController(
            new NoOpProbeService(),
            new NoOpBackendAuthScopeProbeService(),
            configurationRepository,
            authorizationService);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "Test")),
            },
        };
        return controller;
    }

    /// <summary>A real InMemoryConfigurationRepository seeded with one Epic connection — returns the
    /// repository plus the connection's own (auto-assigned) id.</summary>
    private static async Task<(IConfigurationRepository Repository, Guid ConnectionId)> RepositoryWithEpicConnectionAsync()
    {
        var repository = new InMemoryConfigurationRepository(LicenseTestScopeFactory.Create());
        var connection = new SourceConnection("Segue Epic Backend", SourceSystemType.Epic, "https://fhir.example.com/epic", Auth);
        await repository.AddSourceConnectionAsync(connection, CancellationToken.None);
        return (repository, connection.Id);
    }

    [Fact]
    public async Task Epic_create_with_the_Epic_connection_selected__is_allowed()
    {
        var (repository, connectionId) = await RepositoryWithEpicConnectionAsync();
        var authorizationService = BuildRealAuthorizationService(Guid.NewGuid(), "epic.create");
        var controller = BuildController(authorizationService, repository);

        var result = await controller.Probe(
            new SourceDiscoveryProbeRequest("https://fhir.example.com/epic", connectionId), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Athenahealth_create_with_the_Epic_connection_selected__is_denied()
    {
        var (repository, connectionId) = await RepositoryWithEpicConnectionAsync();
        var authorizationService = BuildRealAuthorizationService(Guid.NewGuid(), "athenahealth.create");
        var controller = BuildController(authorizationService, repository);

        var result = await controller.Probe(
            new SourceDiscoveryProbeRequest("https://fhir.example.com/epic", connectionId), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Generic_sourceconnectionsCreate_with_the_Epic_connection_selected__is_allowed()
    {
        var (repository, connectionId) = await RepositoryWithEpicConnectionAsync();
        var authorizationService = BuildRealAuthorizationService(Guid.NewGuid(), "sourceconnections.create");
        var controller = BuildController(authorizationService, repository);

        var result = await controller.Probe(
            new SourceDiscoveryProbeRequest("https://fhir.example.com/epic", connectionId), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task No_relevant_permission_with_the_Epic_connection_selected__is_denied()
    {
        var (repository, connectionId) = await RepositoryWithEpicConnectionAsync();
        // "workflow.edit" is unrelated to source connections — proves a permission the user genuinely
        // lacks (rather than a missing/misconfigured policy) is what's producing the denial.
        var authorizationService = BuildRealAuthorizationService(Guid.NewGuid(), "workflow.edit");
        var controller = BuildController(authorizationService, repository);

        var result = await controller.Probe(
            new SourceDiscoveryProbeRequest("https://fhir.example.com/epic", connectionId), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task New_connection_flow__no_SourceConnectionId__generic_permission_still_works()
    {
        var repository = new InMemoryConfigurationRepository(LicenseTestScopeFactory.Create());
        var authorizationService = BuildRealAuthorizationService(Guid.NewGuid(), "sourceconnections.edit");
        var controller = BuildController(authorizationService, repository);

        var result = await controller.Probe(
            new SourceDiscoveryProbeRequest("https://fhir.example.com/new"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task New_connection_flow__no_SourceConnectionId__vendor_permission_alone_is_not_enough()
    {
        // Preserves the exact prior behavior for a brand-new connection: with nothing to resolve a
        // vendor from, only the generic SourceDiscoveryAccess check applies — epic.create alone (no
        // generic sourceconnections.create/edit) must NOT be sufficient here, unlike the
        // existing-connection case above.
        var repository = new InMemoryConfigurationRepository(LicenseTestScopeFactory.Create());
        var authorizationService = BuildRealAuthorizationService(Guid.NewGuid(), "epic.create");
        var controller = BuildController(authorizationService, repository);

        var result = await controller.Probe(
            new SourceDiscoveryProbeRequest("https://fhir.example.com/new"), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task SourceConnectionId_that_does_not_resolve__falls_back_to_denied_rather_than_erroring()
    {
        var repository = new InMemoryConfigurationRepository(LicenseTestScopeFactory.Create()); // empty — the id below resolves to nothing
        var authorizationService = BuildRealAuthorizationService(Guid.NewGuid(), "epic.create");
        var controller = BuildController(authorizationService, repository);

        var result = await controller.Probe(
            new SourceDiscoveryProbeRequest("https://fhir.example.com/epic", Guid.NewGuid()), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }
}
