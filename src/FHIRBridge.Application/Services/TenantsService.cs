using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Tenancy;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

public sealed partial class TenantsService : ITenantsService
{
    private readonly ITenantRepository _repository;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;

    public TenantsService(ITenantRepository repository, IUserDisplayNameResolver userDisplayNameResolver)
    {
        _repository = repository;
        _userDisplayNameResolver = userDisplayNameResolver;
    }

    public async Task<IReadOnlyList<TenantDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var tenants = await _repository.GetAllAsync(cancellationToken);
        var dtos = tenants.Select(ToDto).ToArray();

        var names = await _userDisplayNameResolver.ResolveAsync(
            dtos.Select(dto => dto.CreatedBy), cancellationToken);

        return dtos.Select(dto => dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
        }).ToArray();
    }

    public async Task<TenantDto> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenant = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), id);

        return ToDto(tenant);
    }

    public async Task<TenantDto> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        var (name, code) = ValidateAndNormalize(request.Name, request.Code);

        if (await _repository.CodeExistsAsync(code, excludingId: null, cancellationToken))
        {
            throw new InvalidOperationException($"A tenant with code '{code}' already exists.");
        }

        var tenant = new Tenant(Guid.NewGuid(), name, code);
        await _repository.AddAsync(tenant, cancellationToken);

        return ToDto(tenant);
    }

    public async Task<TenantDto> UpdateAsync(Guid id, UpdateTenantRequest request, CancellationToken cancellationToken)
    {
        var tenant = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), id);

        var (name, code) = ValidateAndNormalize(request.Name, request.Code);

        if (await _repository.CodeExistsAsync(code, excludingId: id, cancellationToken))
        {
            throw new InvalidOperationException($"A tenant with code '{code}' already exists.");
        }

        tenant.Update(name, code);
        tenant.SetActive(request.IsActive);
        await _repository.UpdateAsync(tenant, cancellationToken);

        return ToDto(tenant);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenant = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), id);

        // The FK is RESTRICT (see UserConfiguration), so this would fail at the DB level anyway — checked
        // here first for a clean, actionable error instead of a raw constraint-violation exception.
        if (await _repository.HasUsersAsync(id, cancellationToken))
        {
            throw new InvalidOperationException(
                "This tenant still has users assigned to it and cannot be deleted. Reassign or remove its users first.");
        }

        await _repository.DeleteAsync(tenant, cancellationToken);
    }

    // Mirrors the portal's own client-side rules (tenant-dialog.component.ts / tenant-tab.component.ts):
    // name required (max 120), code required (max 20, URL-safe). The server is authoritative; the client
    // copy only rejects obviously-bad input before a round trip.
    private (string Name, string Code) ValidateAndNormalize(string name, string code)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new InvalidOperationException("Tenant name is required.");
        }

        if (trimmedName.Length > 120)
        {
            throw new InvalidOperationException("Tenant name must not exceed 120 characters.");
        }

        var trimmedCode = code?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedCode))
        {
            throw new InvalidOperationException("Tenant code is required.");
        }

        if (trimmedCode.Length > 20)
        {
            throw new InvalidOperationException("Tenant code must not exceed 20 characters.");
        }

        if (!CodePattern().IsMatch(trimmedCode))
        {
            throw new InvalidOperationException("Tenant code may only contain letters, numbers, hyphens, and underscores.");
        }

        return (trimmedName, trimmedCode);
    }

    private static TenantDto ToDto(Tenant tenant) => new(
        tenant.Id, tenant.Name, tenant.Code, tenant.IsActive, tenant.CreatedOnUtc, tenant.CreatedBy);

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex CodePattern();
}
