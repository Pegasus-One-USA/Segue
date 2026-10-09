using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Infrastructure.Caching;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>
/// EhrWriteBack:DryRunEnabled decides only whether the destination form offers Dry run. It is on by default, so
/// nothing changes until an admin turns it off, and nothing on the run side reads it.
/// </summary>
public sealed class EhrWriteBackDryRunSettingTests
{
    [Fact]
    public void The_setting_is_named_and_off_by_default_like_clone_mode()
    {
        EhrWriteBackSettings.DryRunEnabledKey.Should().Be("EhrWriteBack:DryRunEnabled");
        EhrWriteBackSettings.DryRunEnabledDefault.Should().BeFalse();
    }

    [Fact]
    public async Task System_Settings_lists_it_off_with_a_plain_description()
    {
        var repository = new InMemorySystemSettingRepository();

        await new SystemSettingsSeeder(repository, new ConfigurationBuilder().Build()).EnsureSeededAsync(CancellationToken.None);

        var setting = await repository.GetByKeyAsync(EhrWriteBackSettings.DryRunEnabledKey, CancellationToken.None);
        setting.Should().NotBeNull();
        bool.Parse(setting!.Value).Should().BeFalse();
        setting.Description.Should().Contain("Dry run").And.Contain("keeps running as a dry run");
    }

    [Fact]
    public async Task An_appsettings_override_is_what_gets_seeded()
    {
        var repository = new InMemorySystemSettingRepository();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [EhrWriteBackSettings.DryRunEnabledKey] = "true" })
            .Build();

        await new SystemSettingsSeeder(repository, configuration).EnsureSeededAsync(CancellationToken.None);

        var setting = await repository.GetByKeyAsync(EhrWriteBackSettings.DryRunEnabledKey, CancellationToken.None);
        bool.Parse(setting!.Value).Should().BeTrue();
    }

    [Fact]
    public async Task Seeding_never_overwrites_the_value_an_admin_chose()
    {
        var repository = new InMemorySystemSettingRepository();
        await repository.UpsertAsync(EhrWriteBackSettings.DryRunEnabledKey, "true", "chosen", CancellationToken.None);

        await new SystemSettingsSeeder(repository, new ConfigurationBuilder().Build()).EnsureSeededAsync(CancellationToken.None);

        (await repository.GetByKeyAsync(EhrWriteBackSettings.DryRunEnabledKey, CancellationToken.None))!.Value.Should().Be("true");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("off", false)]
    [InlineData("", false)]
    public async Task The_stored_value_is_read_as_a_bool_and_anything_else_keeps_dry_run_hidden(string? stored, bool expected)
    {
        var repository = new InMemorySystemSettingRepository();
        if (stored is not null)
        {
            await repository.UpsertAsync(EhrWriteBackSettings.DryRunEnabledKey, stored, null, CancellationToken.None);
        }

        var services = new ServiceCollection().AddSingleton<ISystemSettingRepository>(repository).BuildServiceProvider();
        var cache = new InProcessSystemSettingsCache(services.GetRequiredService<IServiceScopeFactory>());

        var enabled = await cache.GetBoolAsync(
            EhrWriteBackSettings.DryRunEnabledKey, EhrWriteBackSettings.DryRunEnabledDefault, CancellationToken.None);

        enabled.Should().Be(expected);
    }

    [Fact]
    public void The_capabilities_dto_carries_the_setting_next_to_clone_mode()
    {
        var dto = new EhrWriteCapabilitiesDto("Epic", true, CloneModeEnabled: false, DryRunEnabled: false, []);

        dto.DryRunEnabled.Should().BeFalse();
        dto.CloneModeEnabled.Should().BeFalse();
        (dto with { DryRunEnabled = true }).DryRunEnabled.Should().BeTrue();
    }

    [Fact]
    public void The_write_back_writer_does_not_read_system_settings()
    {
        // A saved dry run (dest_dryRun true) must stay a dry run when the setting is off: the writer cannot see it.
        typeof(MappedEhrWriteBackDestinationWriter).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .Should().NotContain(typeof(FHIRBridge.Application.Abstractions.Caching.ISystemSettingsCache))
            .And.NotContain(typeof(ISystemSettingRepository));
    }
}
