using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Services.Cds;
using FHIRBridge.Infrastructure.Cds.Handlers;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// Handler types are DI-collected and found by key through the registry (never a switch). Also checks the five
/// built-in types as a set: unique keys, supported hooks, well-formed settings descriptors and prefetch templates.
/// </summary>
public sealed class CdsServiceHandlerRegistryTests
{
    private static ICdsServiceHandler[] BuiltInHandlers() =>
    [
        new DrugAllergyCdsHandler(),
        new DrugInteractionCdsHandler(),
        new RenalDoseCdsHandler(),
        new CareGapCdsHandler(),
        new PriorAuthCdsHandler(),
    ];

    [Fact]
    public void Every_built_in_handler_is_found_by_its_key()
    {
        var registry = new CdsServiceHandlerRegistry(BuiltInHandlers());

        registry.All.Should().HaveCount(5);
        foreach (var key in new[]
                 {
                     CdsHandlerKeys.DrugAllergy, CdsHandlerKeys.DrugInteraction, CdsHandlerKeys.RenalDose,
                     CdsHandlerKeys.CareGap, CdsHandlerKeys.PriorAuth,
                 })
        {
            registry.Find(key)!.Key.Should().Be(key);
        }
    }

    [Fact]
    public void The_lookup_ignores_case()
    {
        var registry = new CdsServiceHandlerRegistry(BuiltInHandlers());

        registry.Find("DRUG-ALLERGY").Should().BeOfType<DrugAllergyCdsHandler>();
    }

    [Theory]
    [InlineData("no-such-handler")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_key_finds_nothing(string? key)
    {
        var registry = new CdsServiceHandlerRegistry(BuiltInHandlers());

        registry.Find(key).Should().BeNull();
    }

    [Fact]
    public void Two_handlers_with_the_same_key_are_rejected()
    {
        var act = () => new CdsServiceHandlerRegistry([new DrugAllergyCdsHandler(), new DrugAllergyCdsHandler()]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*drug-allergy*");
    }

    [Fact]
    public void Handlers_are_listed_by_display_name()
    {
        var registry = new CdsServiceHandlerRegistry(BuiltInHandlers());

        registry.All.Select(h => h.DisplayName).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Built_in_handlers_declare_the_approved_hooks()
    {
        var registry = new CdsServiceHandlerRegistry(BuiltInHandlers());

        registry.Find(CdsHandlerKeys.DrugAllergy)!.SupportedHooks.Should().BeEquivalentTo([CdsHookNames.OrderSelect, CdsHookNames.OrderSign]);
        registry.Find(CdsHandlerKeys.DrugInteraction)!.SupportedHooks.Should().BeEquivalentTo([CdsHookNames.OrderSelect, CdsHookNames.OrderSign]);
        registry.Find(CdsHandlerKeys.RenalDose)!.SupportedHooks.Should().BeEquivalentTo([CdsHookNames.OrderSelect, CdsHookNames.OrderSign]);
        registry.Find(CdsHandlerKeys.CareGap)!.SupportedHooks.Should().BeEquivalentTo([CdsHookNames.PatientView]);
        registry.Find(CdsHandlerKeys.PriorAuth)!.SupportedHooks.Should().BeEquivalentTo([CdsHookNames.OrderSign]);
    }

    [Fact]
    public void Every_settings_descriptor_is_well_formed_and_its_defaults_pass_validation()
    {
        foreach (var handler in BuiltInHandlers())
        {
            handler.Settings.Select(s => s.Key).Should().OnlyHaveUniqueItems(handler.Key);
            foreach (var setting in handler.Settings)
            {
                CdsSettingTypes.All.Should().Contain(setting.Type, $"{handler.Key}.{setting.Key}");
                if (setting.Type == CdsSettingTypes.Select)
                {
                    setting.Options.Should().NotBeNullOrEmpty($"{handler.Key}.{setting.Key} is a select");
                }
            }

            var defaults = new System.Text.Json.Nodes.JsonObject();
            foreach (var setting in handler.Settings.Where(s => s.DefaultValue is not null))
            {
                defaults[setting.Key] = setting.DefaultValue!.DeepClone();
            }

            var act = () => CdsSettingsValues.Validate(handler.Settings, defaults);
            act.Should().NotThrow($"the defaults of {handler.Key} must be valid settings");
        }
    }

    [Fact]
    public void Prefetch_templates_use_the_context_patient_id_token()
    {
        foreach (var handler in BuiltInHandlers())
        {
            foreach (var hook in handler.SupportedHooks)
            {
                handler.GetPrefetchTemplates(hook).Values.Should().AllSatisfy(template =>
                    template.Should().Contain("{{context.patientId}}"));
            }
        }
    }
}
