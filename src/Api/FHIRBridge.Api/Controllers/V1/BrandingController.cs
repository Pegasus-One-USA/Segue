using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Branding;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Tenancy;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Tenant-scoped white-label branding — the Settings hub's Branding tab. Exactly one row per Tenant (see
/// the unique index in BrandConfigurationConfig). GET is anonymous: the login page, the auth-brand header,
/// and every unauthenticated screen need a tenant's saved branding before any session exists.
///
/// Tenant resolution — NEVER trusts a client-supplied tenant id:
///  - Authenticated caller: always resolves the caller's OWN TenantId via ICurrentTenantResolver, off the
///    "uid" JWT claim. A ?tenant= query parameter is ignored once authenticated — it must never override
///    the real tenant after login.
///  - Anonymous caller (pre-login): resolves from ?tenant=&lt;code&gt; if present and it matches an
///    active Tenant; otherwise falls back to the built-in default branding.
/// PUT never even has a TenantId field on its request DTO (UpdateBrandConfigurationRequest) — there is
/// nothing for a forged client value to overwrite.
/// </summary>
[ApiController]
[Route("api/v1/branding")]
public sealed class BrandingController : ControllerBase
{
    private readonly IBrandConfigurationService _service;
    private readonly ICurrentTenantResolver _tenantResolver;
    private readonly ITenantRepository _tenantRepository;

    public BrandingController(
        IBrandConfigurationService service,
        ICurrentTenantResolver tenantResolver,
        ITenantRepository tenantRepository)
    {
        _service = service;
        _tenantResolver = tenantResolver;
        _tenantRepository = tenantRepository;
    }

    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(BrandConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get([FromQuery(Name = "tenant")] string? tenantCode, CancellationToken cancellationToken)
    {
        // This response depends on the caller (anonymous vs. each authenticated tenant) even though the
        // URL never changes, so a browser/proxy is never allowed to reuse a previously cached response for
        // it — without this, an earlier anonymous fetch (e.g. the pre-login page) risks being served back
        // on a later authenticated request to the exact same URL, which would look exactly like "the saved
        // branding reverts to default on refresh" despite the backend and database being correct.
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        var tenantId = await ResolveTenantIdAsync(tenantCode, cancellationToken);
        if (tenantId is null)
        {
            return Ok(BrandConfigurationDto.BuiltInDefault);
        }

        var configuration = await _service.GetAsync(tenantId.Value, cancellationToken);
        return Ok(configuration);
    }

    [HttpPut]
    [Authorize]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Update the white-label branding configuration.")]
    [ProducesResponseType(typeof(BrandConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        [FromBody] UpdateBrandConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = await ResolveAuthenticatedTenantIdAsync(cancellationToken)
            ?? throw new InvalidOperationException("Your account is not associated with a tenant.");

        var configuration = await _service.UpdateAsync(tenantId, request, cancellationToken);
        return Ok(configuration);
    }

    private async Task<Guid?> ResolveTenantIdAsync(string? tenantCode, CancellationToken cancellationToken)
    {
        // Authenticated: always the caller's own tenant, regardless of any ?tenant= present. A query
        // parameter is only meaningful pre-login; it must never override the real, authenticated tenant.
        if (User.Identity?.IsAuthenticated == true)
        {
            return await ResolveAuthenticatedTenantIdAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(tenantCode))
        {
            return null;
        }

        var tenant = await _tenantRepository.GetByCodeAsync(tenantCode, cancellationToken);
        return tenant is { IsActive: true } ? tenant.Id : null;
    }

    private async Task<Guid?> ResolveAuthenticatedTenantIdAsync(CancellationToken cancellationToken)
    {
        var userId = CurrentUserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return null;
        }

        return await _tenantResolver.ResolveTenantIdAsync(userId.Value, cancellationToken);
    }
}
