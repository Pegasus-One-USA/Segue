using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Services;

public sealed class NotificationSettingsServiceTests
{
    private readonly Mock<INotificationSettingsRepository> _repository = new();
    private readonly Mock<ISecretWriter> _secretWriter = new();

    private NotificationSettingsService Service() => new(_repository.Object, _secretWriter.Object);

    [Fact]
    public async Task GetAsync_returns_a_disabled_default_when_nothing_has_ever_been_saved()
    {
        _repository.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((NotificationSettings?)null);

        var dto = await Service().GetAsync(CancellationToken.None);

        dto.IsEnabled.Should().BeFalse();
        dto.HasPasswordConfigured.Should().BeFalse();
        dto.FromName.Should().Be("FHIRBridge");
    }

    [Fact]
    public async Task GetAsync_never_exposes_the_password_only_whether_one_is_set()
    {
        var settings = new NotificationSettings(
            true, "smtp.example.com", 587, true, "user@example.com", "noreply@example.com", "FHIRBridge",
            new SecretReference("app", "smtp-password"));
        _repository.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var dto = await Service().GetAsync(CancellationToken.None);

        dto.HasPasswordConfigured.Should().BeTrue();
        typeof(NotificationSettingsDto).GetProperties().Select(p => p.Name)
            .Should().NotContain("Password");
    }

    [Fact]
    public async Task UpdateAsync_with_no_password_creates_settings_with_no_password_reference_on_first_save()
    {
        _repository.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((NotificationSettings?)null);
        NotificationSettings? saved = null;
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<NotificationSettings>(), It.IsAny<CancellationToken>()))
            .Callback<NotificationSettings, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);

        var request = new UpdateNotificationSettingsRequest(
            true, "smtp.example.com", 587, true, "user@example.com", "noreply@example.com", "FHIRBridge", Password: null);

        var dto = await Service().UpdateAsync(request, CancellationToken.None);

        dto.HasPasswordConfigured.Should().BeFalse();
        saved.Should().NotBeNull();
        saved!.PasswordSecretReference.Should().BeNull();
        _secretWriter.Verify(
            x => x.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_with_a_new_password_writes_it_to_the_secret_store_and_keeps_it_out_of_the_dto()
    {
        _repository.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((NotificationSettings?)null);
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<NotificationSettings>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var request = new UpdateNotificationSettingsRequest(
            true, "smtp.example.com", 587, true, "user@example.com", "noreply@example.com", "FHIRBridge",
            Password: "hunter2");

        var dto = await Service().UpdateAsync(request, CancellationToken.None);

        dto.HasPasswordConfigured.Should().BeTrue();
        _secretWriter.Verify(
            x => x.WriteSecretAsync(It.IsAny<SecretReference>(), "hunter2", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_with_a_blank_password_preserves_the_previously_saved_one()
    {
        var existing = new NotificationSettings(
            true, "smtp.example.com", 587, true, "user@example.com", "noreply@example.com", "FHIRBridge",
            new SecretReference("app", "smtp-password"));
        _repository.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _repository
            .Setup(x => x.SaveAsync(It.IsAny<NotificationSettings>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var request = new UpdateNotificationSettingsRequest(
            true, "smtp2.example.com", 25, false, "user2@example.com", "noreply2@example.com", "FHIRBridge",
            Password: null);

        var dto = await Service().UpdateAsync(request, CancellationToken.None);

        dto.HasPasswordConfigured.Should().BeTrue();
        dto.Host.Should().Be("smtp2.example.com");
        _secretWriter.Verify(
            x => x.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
