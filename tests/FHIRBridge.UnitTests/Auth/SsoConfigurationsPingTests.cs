using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Auth;

public sealed class SsoConfigurationsPingTests
{
    private const string EnvId = "8cfc0a0d-529f-4616-aab1-bd8c963bdb1a";

    private readonly Mock<ISystemSettingsCache> _settingsCache = new();
    private readonly Mock<ISystemSettingsService> _systemSettings = new();
    private readonly Mock<ISecretWriter> _secretWriter = new();
    private readonly Mock<IPingConnectionTester> _tester = new();

    private SsoConfigurationsService Service()
    {
        _settingsCache.Setup(x => x.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, bool d, CancellationToken _) => d);
        _settingsCache.Setup(x => x.GetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string d, CancellationToken _) => d);

        return new SsoConfigurationsService(
            _settingsCache.Object,
            _systemSettings.Object,
            Options.Create(new SamlAuthenticationOptions()),
            Options.Create(new LocalAuthOptions()),
            Options.Create(new EntraAuthenticationOptions()),
            Options.Create(new GoogleAuthenticationOptions()),
            Options.Create(new PingAuthenticationOptions()),
            _secretWriter.Object,
            _tester.Object);
    }

    private static UpdateSsoConfigurationsRequest Request(UpdatePingConfigurationRequest? ping) => new(
        false, "", "", "", "", "", "", false, false, "", "", "", ping);

    private static UpdatePingConfigurationRequest Ping(
        string? secret = "worker-secret",
        string apiDomain = "api.pingone.sg",
        string environmentId = EnvId,
        string? roleSyncMode = null,
        string? conflictMode = null,
        string? noMappedGroupMode = null,
        params PingGroupRoleMappingDto[] mappings) => new(
        Enabled: true,
        EnvironmentId: environmentId,
        AuthDomain: "auth.pingone.sg",
        ApiDomain: apiDomain,
        ClientId: "portal-client",
        WorkerClientId: "worker-client",
        WorkerClientSecret: secret,
        JitProvisioning: true,
        ApplyToSaml: true,
        GroupRoleMappings: mappings.Length > 0 ? mappings : [new PingGroupRoleMappingDto("group-1", "Admin")],
        RoleSyncMode: roleSyncMode,
        ConflictMode: conflictMode,
        NoMappedGroupMode: noMappedGroupMode);

    [Fact]
    public async Task Saving_without_a_ping_section_leaves_ping_settings_untouched()
    {
        await Service().UpdateAsync(Request(null), CancellationToken.None);

        _systemSettings.Verify(x => x.SetAsync(
            It.Is<string>(k => k.StartsWith("Authentication:Ping:")), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Saving_ping_persists_settings_and_stores_the_secret_only_in_the_secret_store()
    {
        await Service().UpdateAsync(Request(Ping()), CancellationToken.None);

        _systemSettings.Verify(x => x.SetAsync(
            "Authentication:Ping:EnvironmentId", EnvId, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _systemSettings.Verify(x => x.SetAsync(
            "Authentication:Ping:GroupRoleMappings", It.Is<string>(j => j.Contains("group-1") && j.Contains("Admin")),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _secretWriter.Verify(x => x.WriteSecretAsync(
            It.Is<SecretReference>(r => r.KeyVaultName == "app" && r.SecretName == "ping-worker-client-secret"),
            "worker-secret", It.IsAny<CancellationToken>()), Times.Once);

        // The secret value must never land in a settings row.
        _systemSettings.Verify(x => x.SetAsync(
            It.IsAny<string>(), It.Is<string>(v => v.Contains("worker-secret")), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Blank_secret_keeps_the_stored_one()
    {
        await Service().UpdateAsync(Request(Ping(secret: "")), CancellationToken.None);

        _secretWriter.Verify(x => x.WriteSecretAsync(
            It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("api.evil.example")]
    [InlineData("api.pingone.sg/../x")]
    public async Task Rejects_an_api_domain_that_is_not_a_bare_pingone_host(string apiDomain)
    {
        var act = () => Service().UpdateAsync(Request(Ping(apiDomain: apiDomain)), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        _secretWriter.Verify(x => x.WriteSecretAsync(
            It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rejects_a_non_guid_environment_id_when_enabled()
    {
        var act = () => Service().UpdateAsync(Request(Ping(environmentId: "not-a-guid")), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Rejects_a_mapping_with_no_role()
    {
        var act = () => Service().UpdateAsync(
            Request(Ping(mappings: new PingGroupRoleMappingDto("group-1", ""))), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Get_returns_saved_mappings_and_never_the_secret()
    {
        _settingsCache.Setup(x => x.GetStringAsync(
                "Authentication:Ping:GroupRoleMappings", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"g1":["Admin","Audit"]}""");
        _settingsCache.Setup(x => x.GetBoolAsync(
                "Authentication:Ping:WorkerSecretConfigured", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = Service();
        // Service() registered the pass-through defaults last; re-apply the specific ones on top.
        _settingsCache.Setup(x => x.GetStringAsync(
                "Authentication:Ping:GroupRoleMappings", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"g1":["Admin","Audit"]}""");
        _settingsCache.Setup(x => x.GetBoolAsync(
                "Authentication:Ping:WorkerSecretConfigured", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var dto = await service.GetAsync(CancellationToken.None);

        dto.Ping!.WorkerSecretConfigured.Should().BeTrue();
        // A value saved by an earlier version (group to role array) is still read, taking the first role.
        dto.Ping.GroupRoleMappings.Should().ContainSingle()
            .Which.Role.Should().Be("Admin");
    }

    [Fact]
    public async Task Saving_keeps_the_mapping_order_because_the_order_is_the_priority()
    {
        await Service().UpdateAsync(
            Request(Ping(mappings:
            [
                new PingGroupRoleMappingDto("zeta", "Admin"),
                new PingGroupRoleMappingDto("alpha", "Audit")
            ])),
            CancellationToken.None);

        _systemSettings.Verify(x => x.SetAsync(
            "Authentication:Ping:GroupRoleMappings",
            It.Is<string>(j => j.IndexOf("zeta", StringComparison.Ordinal) < j.IndexOf("alpha", StringComparison.Ordinal)),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Rejects_the_same_ping_group_mapped_twice()
    {
        var act = () => Service().UpdateAsync(
            Request(Ping(mappings:
            [
                new PingGroupRoleMappingDto("group-1", "Admin"),
                new PingGroupRoleMappingDto("GROUP-1", "Audit")
            ])),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Saves_the_role_sync_and_conflict_modes()
    {
        await Service().UpdateAsync(
            Request(Ping(roleSyncMode: "RestrictMismatch", conflictMode: "UseMappingOrder")), CancellationToken.None);

        _systemSettings.Verify(x => x.SetAsync(
            "Authentication:Ping:RoleSyncMode", "RestrictMismatch", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _systemSettings.Verify(x => x.SetAsync(
            "Authentication:Ping:ConflictMode", "UseMappingOrder", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Leaves_the_modes_unchanged_when_the_request_does_not_send_them()
    {
        await Service().UpdateAsync(Request(Ping()), CancellationToken.None);

        _systemSettings.Verify(x => x.SetAsync(
            "Authentication:Ping:RoleSyncMode", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _systemSettings.Verify(x => x.SetAsync(
            "Authentication:Ping:ConflictMode", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Sometimes", null)]
    [InlineData(null, "Maybe")]
    public async Task Rejects_an_unknown_mode(string? roleSyncMode, string? conflictMode)
    {
        var act = () => Service().UpdateAsync(
            Request(Ping(roleSyncMode: roleSyncMode, conflictMode: conflictMode)), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Saves_the_no_mapped_group_mode()
    {
        await Service().UpdateAsync(Request(Ping(noMappedGroupMode: "AllowSegueRole")), CancellationToken.None);

        _systemSettings.Verify(x => x.SetAsync(
            "Authentication:Ping:NoMappedGroupMode", "AllowSegueRole", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Rejects_an_unknown_no_mapped_group_mode()
    {
        var act = () => Service().UpdateAsync(Request(Ping(noMappedGroupMode: "Whatever")), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
