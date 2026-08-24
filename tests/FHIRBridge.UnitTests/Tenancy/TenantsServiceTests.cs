using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Tenancy;

/// <summary>Covers TenantsService — the real backend for Tenant CRUD (previously an in-memory,
/// non-persistent frontend mock, TenantRoleService).</summary>
public sealed class TenantsServiceTests
{
    private readonly Mock<ITenantRepository> _repository = new();
    private readonly Mock<IUserDisplayNameResolver> _nameResolver = new();

    public TenantsServiceTests()
    {
        _nameResolver.Setup(x => x.ResolveAsync(It.IsAny<IEnumerable<string?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
    }

    private TenantsService Service() => new(_repository.Object, _nameResolver.Object);

    [Fact]
    public async Task CreateAsync_creates_a_tenant_with_the_requested_name_and_code()
    {
        _repository.Setup(x => x.CodeExistsAsync("acme", null, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        Tenant? added = null;
        _repository.Setup(x => x.AddAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()))
            .Callback<Tenant, CancellationToken>((t, _) => added = t)
            .Returns(Task.CompletedTask);

        var dto = await Service().CreateAsync(new CreateTenantRequest("Acme Health", "acme"), CancellationToken.None);

        dto.Name.Should().Be("Acme Health");
        dto.Code.Should().Be("acme");
        dto.IsActive.Should().BeTrue();
        added.Should().NotBeNull();
        added!.Code.Should().Be("acme");
    }

    [Fact]
    public async Task CreateAsync_rejects_a_duplicate_code()
    {
        _repository.Setup(x => x.CodeExistsAsync("acme", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = async () => await Service().CreateAsync(new CreateTenantRequest("Acme Health", "acme"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repository.Verify(x => x.AddAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_rejects_a_blank_name()
    {
        var act = async () => await Service().CreateAsync(new CreateTenantRequest("   ", "acme"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("has a space")]
    [InlineData("has/slash")]
    [InlineData("")]
    public async Task CreateAsync_rejects_a_non_url_safe_code(string badCode)
    {
        var act = async () => await Service().CreateAsync(new CreateTenantRequest("Acme Health", badCode), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateAsync_updates_the_same_tenant_in_place()
    {
        var tenant = new Tenant(Guid.NewGuid(), "Old Name", "old-code");
        _repository.Setup(x => x.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        _repository.Setup(x => x.CodeExistsAsync("new-code", tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var dto = await Service().UpdateAsync(
            tenant.Id, new UpdateTenantRequest("New Name", "new-code", IsActive: false), CancellationToken.None);

        dto.Name.Should().Be("New Name");
        dto.Code.Should().Be("new-code");
        dto.IsActive.Should().BeFalse();
        tenant.Name.Should().Be("New Name");
        tenant.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_throws_NotFound_for_an_unknown_tenant()
    {
        _repository.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Tenant?)null);

        var act = async () => await Service().UpdateAsync(
            Guid.NewGuid(), new UpdateTenantRequest("Name", "code", true), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task UpdateAsync_allows_keeping_a_tenants_own_existing_code_unchanged()
    {
        var tenant = new Tenant(Guid.NewGuid(), "Acme", "acme");
        _repository.Setup(x => x.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        // CodeExistsAsync excludes this tenant's own id, so re-saving its own code must not collide.
        _repository.Setup(x => x.CodeExistsAsync("acme", tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var act = async () => await Service().UpdateAsync(
            tenant.Id, new UpdateTenantRequest("Acme Renamed", "acme", true), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_deletes_a_tenant_with_no_users()
    {
        var tenant = new Tenant(Guid.NewGuid(), "Acme", "acme");
        _repository.Setup(x => x.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        _repository.Setup(x => x.HasUsersAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Service().DeleteAsync(tenant.Id, CancellationToken.None);

        _repository.Verify(x => x.DeleteAsync(tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_refuses_to_delete_a_tenant_that_still_has_users()
    {
        var tenant = new Tenant(Guid.NewGuid(), "Acme", "acme");
        _repository.Setup(x => x.GetByIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        _repository.Setup(x => x.HasUsersAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = async () => await Service().DeleteAsync(tenant.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repository.Verify(x => x.DeleteAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
