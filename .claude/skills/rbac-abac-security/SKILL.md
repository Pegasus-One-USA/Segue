---
name: rbac-abac-security
description: >
  Expert skill for designing and implementing Role-Based Access Control (RBAC) and Attribute-Based Access Control (ABAC) — including hybrid RBAC+ABAC models. Covers policy modeling, claims/attribute design, policy decision points (PDP) vs enforcement points (PEP), .NET (IAuthorizationHandler, policy-based authorization), Angular route guards, Node/Express middleware, OPA/Rego, AWS/Azure IAM condition-based policies, and multi-tenant permission systems. Trigger whenever the user mentions RBAC, ABAC, access control, permissions, authorization (not authentication), policy-based security, claims-based auth, fine-grained access control, or "who can access what" — even casually, e.g. "add roles to my app" or "restrict this endpoint by attribute."
---

# RBAC / ABAC Authorization Architect

You are a senior application security architect specializing in authorization models. Always distinguish **authentication** (who is this?) from **authorization** (what can they do?) and default to attribute-based reasoning even inside an RBAC system, since pure role checks age poorly.

## Core Principles

- **Roles are a coarse attribute, not a separate paradigm.** Model RBAC as ABAC with one attribute (`role`) when possible — this makes future extension (department, tenant, resource owner, time-of-day) additive, not a rewrite.
- **Decouple PDP from PEP.** The Policy Decision Point (evaluates rules) should be separable from the Policy Enforcement Point (middleware/guard that calls it) — enables central policy testing and reuse across API + UI.
- **Deny by default.** Every resource is inaccessible unless a policy explicitly grants it.
- **Never trust client-side checks alone.** UI-level role/attribute checks (hiding buttons) are UX only; the API/backend must re-enforce every check.
- **Least privilege + explicit resource scoping.** Prefer "can edit *this* invoice because attribute `ownerId == user.id`" over "has role Editor" wherever resource-level ownership exists.

---

## Modeling: RBAC vs ABAC vs Hybrid

| Model | Grants access based on | Best for |
|---|---|---|
| RBAC | Assigned role(s) | Stable org structures, admin/coarse permissions |
| ABAC | Attributes of user, resource, action, environment | Dynamic, contextual, resource-owner, multi-tenant rules |
| Hybrid (recommended default) | Role as one attribute among many | Most real-world apps — start RBAC, extend with ABAC rules as needed |

**ABAC policy shape**: `Decision = f(subject attributes, resource attributes, action, environment)`
Example: "A `clinician` (subject.role) can `read` (action) a `patient record` (resource) if `subject.department == resource.department` OR `subject.id == resource.assignedProviderId` AND `environment.time` is within business hours."

---

## .NET / ASP.NET Core Implementation

### Policy-based authorization (attribute-driven)
```csharp
// Program.cs
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CanEditInvoice", policy =>
        policy.Requirements.Add(new ResourceOwnerRequirement()));

    options.AddPolicy("RequireDeptAccess", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.HasClaim("department", "Finance") ||
            ctx.User.IsInRole("Admin")));
});

builder.Services.AddScoped<IAuthorizationHandler, ResourceOwnerHandler>();
```

```csharp
public class ResourceOwnerRequirement : IAuthorizationRequirement { }

public class ResourceOwnerHandler : AuthorizationHandler<ResourceOwnerRequirement, Invoice>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ResourceOwnerRequirement req, Invoice resource)
    {
        var userId = context.User.FindFirst("sub")?.Value;
        if (resource.OwnerId == userId || context.User.IsInRole("Admin"))
            context.Succeed(req);
        return Task.CompletedTask;
    }
}
```

```csharp
// Controller usage — resource-based (attribute) check
[HttpPut("{id}")]
public async Task<IActionResult> Update(Guid id, InvoiceDto dto)
{
    var invoice = await _repo.GetAsync(id);
    var authResult = await _authorizationService.AuthorizeAsync(User, invoice, "CanEditInvoice");
    if (!authResult.Succeeded) return Forbid();
    // ...
}

// Controller usage — role/claim-based
[Authorize(Policy = "RequireDeptAccess")]
[HttpGet("finance-reports")]
public IActionResult FinanceReports() => Ok();
```

### Claims design for JWT
```json
{
  "sub": "user-123",
  "roles": ["Clinician", "Reviewer"],
  "department": "Cardiology",
  "tenant_id": "hosp-42",
  "attributes": { "clearanceLevel": 3 }
}
```
Keep the token lean — put volatile/large attribute sets (e.g., per-resource ACLs) in a lookup service, not the JWT, to avoid stale-token privilege drift.

---

## Angular Implementation

### Route guard (attribute-aware)
```typescript
export const authGuard: CanActivateFn = (route) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const requiredRoles = route.data['roles'] as string[] | undefined;
  const requiredAttr = route.data['attr'] as ((user: User) => boolean) | undefined;

  const user = auth.currentUser();
  const roleOk = !requiredRoles || requiredRoles.some(r => user.roles.includes(r));
  const attrOk = !requiredAttr || requiredAttr(user);

  if (roleOk && attrOk) return true;
  router.navigate(['/forbidden']);
  return false;
};

// Route config
{ path: 'finance', canActivate: [authGuard], data: { roles: ['Admin', 'Finance'] } }
```

### Structural directive for attribute-based UI hiding (UX-only, not security boundary)
```typescript
@Directive({ selector: '[hasPermission]', standalone: true })
export class HasPermissionDirective {
  @Input() set hasPermission(check: string) {
    this.vcr.clear();
    if (this.permissionService.can(check)) {
      this.vcr.createEmbeddedView(this.tpl);
    }
  }
  constructor(private tpl: TemplateRef<unknown>, private vcr: ViewContainerRef,
              private permissionService: PermissionService) {}
}
// <button *hasPermission="'invoice:edit'">Edit</button>
```

---

## Open Policy Agent (OPA) / Rego — externalized policy

Use when policies must be centrally auditable, versioned, and shared across multiple services/languages.

```rego
package authz

default allow = false

allow {
  input.action == "read"
  input.resource.type == "patient_record"
  input.subject.department == input.resource.department
}

allow {
  input.subject.roles[_] == "admin"
}
```
Query via sidecar (`http://localhost:8181/v1/data/authz/allow`) from any PEP — API gateway, backend service, or CI policy check.

---

## Cloud IAM Condition-Based (ABAC) Patterns

**AWS IAM condition example** (tag-based ABAC):
```json
{
  "Effect": "Allow",
  "Action": "s3:GetObject",
  "Resource": "arn:aws:s3:::bucket/*",
  "Condition": { "StringEquals": { "s3:ExistingObjectTag/department": "${aws:PrincipalTag/department}" } }
}
```

**Azure RBAC + ABAC condition** (storage blob, resource attribute match):
```
@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:Tags['department']] StringEquals @Principal[department]
```

---

## Multi-Tenant Considerations

- Always include `tenant_id` as a mandatory attribute in every authorization check — cross-tenant leakage is the most common ABAC bug in SaaS systems.
- Prefer row-level security (Postgres RLS policies keyed on `tenant_id`/`attributes`) as a defense-in-depth backstop behind application-layer checks.

## Testing Authorization Logic

- Unit test the PDP in isolation with a matrix of (subject, resource, action, environment) fixtures — include explicit "should deny" cases, not just "should allow."
- Add integration tests hitting real endpoints with tokens for each role/attribute combination to catch PEP wiring bugs (e.g., a forgotten `[Authorize]` attribute).
