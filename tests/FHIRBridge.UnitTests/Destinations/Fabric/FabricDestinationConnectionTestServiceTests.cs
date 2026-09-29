using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations.Fabric;

/// <summary>
/// Covers the validation and credential-construction half of the Fabric connection test — everything that runs
/// before a network call. The probes themselves need a live Fabric tenant, so they are deliberately not faked
/// here: a mocked BlobServiceClient would only prove this test's own assumptions about Fabric's behaviour.
/// </summary>
public sealed class FabricDestinationConnectionTestServiceTests
{
    private readonly Mock<IConfigurationRepository> _configurationRepository = new();
    private readonly Mock<ISecretProvider> _secretProvider = new();

    private FabricDestinationConnectionTestService CreateService() =>
        new(_configurationRepository.Object, _secretProvider.Object);

    private static FabricConnectionTestRequest Request(
        string mode = "oneLakeFiles",
        string authMode = "managedIdentity",
        string workspace = "Analytics",
        string itemName = "ClinicalLake",
        string? secret = null,
        string? tenantId = null,
        string? clientId = null,
        string? warehouseSqlEndpoint = null) =>
        new(
            Mode: mode,
            AuthMode: authMode,
            Workspace: workspace,
            ItemName: itemName,
            ItemType: "Lakehouse",
            Secret: secret,
            TenantId: tenantId,
            ClientId: clientId,
            ManagedIdentityClientId: null,
            EndpointSuffix: null,
            AuthorityHost: null,
            AccountUrl: null,
            WarehouseSqlEndpoint: warehouseSqlEndpoint);

    [Theory]
    [InlineData("", "ClinicalLake", "*Workspace is required*")]
    [InlineData("Analytics", "", "*Lakehouse (item) name is required*")]
    public async Task Missing_core_settings_fail_before_any_network_call(
        string workspace, string itemName, string expected)
    {
        var result = await CreateService().TestConnectionAsync(
            Request(workspace: workspace, itemName: itemName), CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.Error.Should().Match(expected);
        result.OneLakeReachable.Should().BeFalse();
    }

    /// <summary>
    /// The endpoint cannot be derived from the workspace — OneLake and the Warehouse are different services — so
    /// its absence is caught here rather than surfacing as a confusing connection error.
    /// </summary>
    [Fact]
    public async Task Warehouse_mode_requires_its_sql_endpoint()
    {
        var result = await CreateService().TestConnectionAsync(
            Request(mode: "warehouseTable"), CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.Error.Should().Match("*Warehouse SQL connection string is required*");
    }

    [Theory]
    [InlineData(null, "client-id", "*client secret is required*")]
    [InlineData("the-secret", null, "*Tenant ID and client ID are required*")]
    public async Task Service_principal_auth_requires_its_full_credential_set(
        string? secret, string? clientId, string expected)
    {
        var result = await CreateService().TestConnectionAsync(
            Request(authMode: "servicePrincipal", secret: secret, tenantId: "tenant-id", clientId: clientId),
            CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.Error.Should().Match(expected);
    }

    /// <summary>
    /// OneLake accepts Entra tokens only — no account key, no SAS — so a Blob-style auth mode is refused with
    /// that reason rather than failing later as an opaque authentication error.
    /// </summary>
    [Theory]
    [InlineData("accountKey")]
    [InlineData("sasUrl")]
    [InlineData("connectionString")]
    public async Task Auth_modes_OneLake_does_not_accept_are_refused_by_name(string authMode)
    {
        var result = await CreateService().TestConnectionAsync(
            Request(authMode: authMode), CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.Error.Should().Match("*Entra credentials only*");
    }

    /// <summary>
    /// A blank AuthMode means the form's default, not "no auth" — managed identity, matching
    /// FabricDestinationSettings.Parse. Reaching a credential-construction failure rather than a validation one
    /// proves it was accepted.
    /// </summary>
    [Fact]
    public async Task A_blank_auth_mode_defaults_to_managed_identity()
    {
        var result = await CreateService().TestConnectionAsync(
            Request(authMode: ""), CancellationToken.None);

        result.Error.Should().NotMatch("*Entra credentials only*");
    }
}
