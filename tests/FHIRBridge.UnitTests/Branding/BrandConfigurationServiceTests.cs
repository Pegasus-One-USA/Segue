using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Branding;

/// <summary>
/// Covers BrandConfigurationService — the backend half of the "Save -> database -> refresh -> GET from
/// backend -> same branding applied" acceptance criterion, now scoped per tenant (see BrandConfiguration's
/// TenantId + the unique index in BrandConfigurationConfig). The frontend half, and the actual tenant
/// isolation enforced at the controller layer (BrandingController never trusting a client-supplied tenant
/// id), aren't unit-testable from here; see the final report for what was checked there instead.
/// </summary>
public sealed class BrandConfigurationServiceTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private readonly Mock<IBrandConfigurationRepository> _repository = new();

    private BrandConfigurationService Service() => new(_repository.Object);

    private static UpdateBrandConfigurationRequest ValidRequest(
        string companyName = "Acme Health",
        string primaryColor = "#112233",
        string supportEmail = "support@acme.test",
        string defaultThemeMode = "dark",
        string loaderStyle = "spinner") =>
        new(
            companyName, primaryColor, "#445566", "#778899", "#AABBCC",
            "'Roboto', sans-serif", "Acme footer", supportEmail, "555-0100", "https://acme.test",
            "Acme email footer", defaultThemeMode, loaderStyle,
            new BrandAssetsDto("logo.png", "dark-logo.png", "favicon.png", "bg.png", "illustration.png", "email-logo.png"));

    private static BrandConfiguration Existing(Guid tenantId, string companyName = "Old Name") => new(
        tenantId, companyName, "#000000", "#000000", "#000000", "#000000", "", "", "", "", "", "",
        "light", "bar", "", "", "favicon.ico", "", "", "");

    [Fact]
    public async Task GetAsync_when_no_row_exists_for_this_tenant_returns_built_in_defaults_and_IsConfigured_false()
    {
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync((BrandConfiguration?)null);

        var dto = await Service().GetAsync(TenantA, CancellationToken.None);

        dto.IsConfigured.Should().BeFalse();
        dto.CompanyName.Should().Be("Segue");
        dto.PrimaryColor.Should().Be("#00A89D");
        dto.DefaultThemeMode.Should().Be("light");
    }

    [Fact]
    public async Task GetAsync_when_a_row_exists_for_this_tenant_returns_its_actual_values_and_IsConfigured_true()
    {
        var saved = new BrandConfiguration(
            TenantA, "Acme Health", "#112233", "#445566", "#778899", "#AABBCC", "'Roboto', sans-serif",
            "Acme footer", "support@acme.test", "555-0100", "https://acme.test", "Acme email footer",
            "dark", "spinner", "logo.png", "dark-logo.png", "favicon.png", "bg.png", "illustration.png", "email-logo.png");
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync(saved);

        var dto = await Service().GetAsync(TenantA, CancellationToken.None);

        dto.IsConfigured.Should().BeTrue();
        dto.CompanyName.Should().Be("Acme Health");
        dto.PrimaryColor.Should().Be("#112233");
        dto.DefaultThemeMode.Should().Be("dark");
        dto.LoaderStyle.Should().Be("spinner");
        dto.Assets.LogoUrl.Should().Be("logo.png");
    }

    // ── Tenant isolation — the actual point of this whole change ───────────────────────────────────

    [Fact]
    public async Task GetAsync_for_tenant_A_never_returns_tenant_Bs_branding()
    {
        var tenantBConfig = Existing(TenantB, "Tenant B Co");
        // TenantA has no row of its own — only TenantB does.
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync((BrandConfiguration?)null);
        _repository.Setup(x => x.GetByTenantIdAsync(TenantB, It.IsAny<CancellationToken>())).ReturnsAsync(tenantBConfig);

        var dtoForA = await Service().GetAsync(TenantA, CancellationToken.None);

        dtoForA.CompanyName.Should().NotBe("Tenant B Co");
        dtoForA.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_for_tenant_A_creates_a_row_scoped_to_tenant_A_and_never_touches_tenant_B()
    {
        var tenantBConfig = Existing(TenantB, "Tenant B Co");
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync((BrandConfiguration?)null);
        _repository.Setup(x => x.GetByTenantIdAsync(TenantB, It.IsAny<CancellationToken>())).ReturnsAsync(tenantBConfig);
        BrandConfiguration? saved = null;
        _repository.Setup(x => x.SaveAsync(It.IsAny<BrandConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<BrandConfiguration, CancellationToken>((c, _) => saved = c)
            .Returns(Task.CompletedTask);

        await Service().UpdateAsync(TenantA, ValidRequest(companyName: "Tenant A Co"), CancellationToken.None);

        saved.Should().NotBeNull();
        saved!.TenantId.Should().Be(TenantA);
        saved.CompanyName.Should().Be("Tenant A Co");
        // TenantB's own row is a completely separate object the mock never mutated.
        tenantBConfig.CompanyName.Should().Be("Tenant B Co");
    }

    [Fact]
    public async Task UpdateAsync_for_tenant_A_updates_only_tenant_As_existing_row_when_both_tenants_already_have_one()
    {
        var tenantAConfig = Existing(TenantA, "A Old Name");
        var tenantBConfig = Existing(TenantB, "B Old Name");
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync(tenantAConfig);
        _repository.Setup(x => x.GetByTenantIdAsync(TenantB, It.IsAny<CancellationToken>())).ReturnsAsync(tenantBConfig);
        _repository.Setup(x => x.SaveAsync(It.IsAny<BrandConfiguration>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var dto = await Service().UpdateAsync(TenantA, ValidRequest(companyName: "A New Name"), CancellationToken.None);

        dto.CompanyName.Should().Be("A New Name");
        tenantAConfig.CompanyName.Should().Be("A New Name");
        // Tenant B's row is untouched — same reference, unchanged value, never passed to SaveAsync.
        tenantBConfig.CompanyName.Should().Be("B Old Name");
        _repository.Verify(x => x.SaveAsync(tenantBConfig, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_when_no_row_exists_creates_one_with_the_requested_values()
    {
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync((BrandConfiguration?)null);
        BrandConfiguration? saved = null;
        _repository.Setup(x => x.SaveAsync(It.IsAny<BrandConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<BrandConfiguration, CancellationToken>((c, _) => saved = c)
            .Returns(Task.CompletedTask);

        var dto = await Service().UpdateAsync(TenantA, ValidRequest(), CancellationToken.None);

        dto.IsConfigured.Should().BeTrue();
        dto.CompanyName.Should().Be("Acme Health");
        saved.Should().NotBeNull();
        saved!.CompanyName.Should().Be("Acme Health");
        saved.PrimaryColor.Should().Be("#112233");
        _repository.Verify(x => x.SaveAsync(It.IsAny<BrandConfiguration>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_when_a_row_already_exists_updates_the_same_entity_in_place()
    {
        var existing = Existing(TenantA);
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        BrandConfiguration? saved = null;
        _repository.Setup(x => x.SaveAsync(It.IsAny<BrandConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<BrandConfiguration, CancellationToken>((c, _) => saved = c)
            .Returns(Task.CompletedTask);

        var dto = await Service().UpdateAsync(TenantA, ValidRequest(companyName: "New Name"), CancellationToken.None);

        dto.CompanyName.Should().Be("New Name");
        // Same row updated in place (not a second row created) — same Id as the original.
        saved.Should().NotBeNull();
        saved!.Id.Should().Be(existing.Id);
        saved.CompanyName.Should().Be("New Name");
    }

    [Fact]
    public async Task UpdateAsync_round_trips_every_field_the_caller_sent()
    {
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync((BrandConfiguration?)null);
        _repository.Setup(x => x.SaveAsync(It.IsAny<BrandConfiguration>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var request = ValidRequest();

        var dto = await Service().UpdateAsync(TenantA, request, CancellationToken.None);

        dto.CompanyName.Should().Be(request.CompanyName);
        dto.PrimaryColor.Should().Be(request.PrimaryColor);
        dto.SecondaryColor.Should().Be(request.SecondaryColor);
        dto.AccentColor.Should().Be(request.AccentColor);
        dto.BackgroundColor.Should().Be(request.BackgroundColor);
        dto.FontFamily.Should().Be(request.FontFamily);
        dto.FooterText.Should().Be(request.FooterText);
        dto.SupportEmail.Should().Be(request.SupportEmail);
        dto.SupportPhone.Should().Be(request.SupportPhone);
        dto.Website.Should().Be(request.Website);
        dto.EmailFooterText.Should().Be(request.EmailFooterText);
        dto.DefaultThemeMode.Should().Be(request.DefaultThemeMode);
        dto.LoaderStyle.Should().Be(request.LoaderStyle);
        dto.Assets.LogoUrl.Should().Be(request.Assets.LogoUrl);
        dto.Assets.DarkLogoUrl.Should().Be(request.Assets.DarkLogoUrl);
        dto.Assets.FaviconUrl.Should().Be(request.Assets.FaviconUrl);
        dto.Assets.LoginBackgroundUrl.Should().Be(request.Assets.LoginBackgroundUrl);
        dto.Assets.LoginIllustrationUrl.Should().Be(request.Assets.LoginIllustrationUrl);
        dto.Assets.EmailLogoUrl.Should().Be(request.Assets.EmailLogoUrl);
    }

    [Fact]
    public async Task UpdateAsync_rejects_a_blank_company_name()
    {
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync((BrandConfiguration?)null);

        var act = async () => await Service().UpdateAsync(TenantA, ValidRequest(companyName: "   "), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repository.Verify(x => x.SaveAsync(It.IsAny<BrandConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("")]
    public async Task UpdateAsync_rejects_an_invalid_hex_color(string badColor)
    {
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync((BrandConfiguration?)null);

        var act = async () => await Service().UpdateAsync(TenantA, ValidRequest(primaryColor: badColor), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repository.Verify(x => x.SaveAsync(It.IsAny<BrandConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_rejects_an_invalid_support_email()
    {
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync((BrandConfiguration?)null);

        var act = async () => await Service().UpdateAsync(TenantA, ValidRequest(supportEmail: "not-an-email"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateAsync_accepts_a_blank_support_email_since_the_field_is_optional()
    {
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync((BrandConfiguration?)null);
        _repository.Setup(x => x.SaveAsync(It.IsAny<BrandConfiguration>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var act = async () => await Service().UpdateAsync(TenantA, ValidRequest(supportEmail: ""), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpdateAsync_rejects_an_unrecognized_theme_mode()
    {
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync((BrandConfiguration?)null);

        var act = async () => await Service().UpdateAsync(TenantA, ValidRequest(defaultThemeMode: "neon"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateAsync_rejects_an_unrecognized_loader_style()
    {
        _repository.Setup(x => x.GetByTenantIdAsync(TenantA, It.IsAny<CancellationToken>())).ReturnsAsync((BrandConfiguration?)null);

        var act = async () => await Service().UpdateAsync(TenantA, ValidRequest(loaderStyle: "confetti"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
