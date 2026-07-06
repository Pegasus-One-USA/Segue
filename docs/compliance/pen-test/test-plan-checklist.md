# Penetration Test Plan & Checklist — FHIRBridge

A concrete, app-specific test checklist for the FHIRBridge platform, mapped to **OWASP ASVS v4/5**, the **OWASP Top 10 (2021)**, and the **OWASP API Security Top 10 (2023)**. This is the agreed **minimum coverage** for any engagement; the Tester should exceed it where warranted.

**How to use:** the Tester records for each item a result of **Pass / Fail / N/A / Not-tested**, evidence (request/response, screenshot), and a finding ID for anything that Fails. Fails flow into [`remediation-tracker.md`](./remediation-tracker.md).

**System under test recap:** .NET 9 ASP.NET Core API (`/api/v1`) + background Worker · Angular portal · SQL Server · Azure Key Vault · docker-compose · ePHI over FHIR/HL7. Auth: JWT + Entra SSO, RBAC, TOTP MFA, account lockout, rate limiting. Public/anonymous surface: `/auth/internal/login`, `/auth/refresh`, forgot/reset-password, SSO login, webhook ingestion (HMAC), JWKS, OAuth SMART launch.

---

## 1. Authentication (ASVS V2 · API2:2023 Broken Authentication · A07:2021)

| # | Test | Expected control | Result |
|---|---|---|---|
| 1.1 | Brute-force `/auth/internal/login` with wrong passwords | **Account lockout** (5 attempts / 15 min per `LocalAuth:Lockout`) engages; further attempts rejected even with correct password until window elapses. | |
| 1.2 | Distributed / username-spray brute force | **Rate limiting** on auth endpoints throttles; lockout is per-account not bypassable via casing/whitespace of username. | |
| 1.3 | Lockout does not leak account existence | Same response/timing for locked, wrong-password, and non-existent user (no user enumeration). | |
| 1.4 | Password policy & hashing | Weak passwords rejected on set/reset; hashing is PBKDF2-SHA256 (350k) — verify no weaker legacy path. | |
| 1.5 | **JWT tampering** — modify claims (userId, role, tenant, permissions) and re-submit | Signature validation rejects tampered tokens. | |
| 1.6 | **JWT `alg=none`** and **alg confusion** (RS256→HS256 using public key as HMAC secret) | Server enforces expected algorithm(s); `none` and downgraded algs rejected. | |
| 1.7 | JWT signature verified against correct **JWKS**; expired/`nbf`/`iss`/`aud` validated | Tokens with bad/absent `exp`, wrong `iss`/`aud`, or unknown `kid` rejected. | |
| 1.8 | **Refresh-token abuse** — replay a used refresh token; use after logout/expiry; use across sessions | Refresh tokens are single-use/rotated, revoked on logout, bound as intended; replay rejected. | |
| 1.9 | Refresh-token theft simulation / rotation-family reuse detection | Reuse of a rotated token invalidates the family (breach detection). | |
| 1.10 | **MFA (TOTP) bypass** — skip the MFA step, replay TOTP code, brute-force 6-digit code, accept pre-MFA "half" token on protected routes | MFA is enforced server-side; TOTP codes are single-use, rate-limited, time-window bounded; pre-MFA token cannot access protected resources. | |
| 1.11 | **SSO (Entra)** — tamper with SSO callback, replay authorization code, manipulate `state`/`nonce`, IdP-confusion / token audience | `state`/`nonce` validated (CSRF/replay), code single-use, token audience/issuer pinned to the expected Entra tenant/app. | |
| 1.12 | **Forgot/reset-password** — token predictability, reuse, expiry, cross-account use; host-header poisoning of reset link | Reset tokens are high-entropy, single-use, short-lived, bound to the account; no user enumeration; reset link host not attacker-controllable. | |

---

## 2. Authorization & multi-tenant isolation (ASVS V4 · API1 BOLA · API3 BOPLA · API5 · A01:2021)

| # | Test | Expected control | Result |
|---|---|---|---|
| 2.1 | **IDOR on tenant IDs** — as a user in tenant A, request `/api/v1/tenants/{B}/...` | Access denied; tenant scoping enforced server-side, not by client-supplied tenant only. | |
| 2.2 | **IDOR on resource IDs** — enumerate/guess Patient, mapping, source, destination, pipeline-run, audit IDs across tenants | Every object access is authorized against the caller's tenant + permissions (BOLA). | |
| 2.3 | **Patient aggregation endpoint** `GET /api/v1/tenants/{tid}/fhirbridge/Patient/{id}` | Cannot read a Patient in another tenant or via a `source` the caller isn't entitled to; fan-out respects authorization. | |
| 2.4 | **Vertical privilege escalation** — low-permission user invokes admin/UnifiedAdmin-only endpoints | `HasPermission` / admin policies enforced on every sensitive endpoint. | |
| 2.5 | **Mass assignment / BOPLA** — inject extra fields (role, permissions, tenantId, isEnabled) in create/update bodies | Server ignores/rejects non-authorized properties; no privilege gain. | |
| 2.6 | **Function-level authz (API5)** — enumerate all routes; verify each requires the right permission | No unauthenticated or under-permissioned sensitive route; no hidden/debug endpoints. | |
| 2.7 | User-management actions (invite, resend-invite, disable, role change) authorization | Only authorized roles can invoke; cannot self-escalate or act cross-tenant. | |
| 2.8 | Soft-delete / disabled account still denied access | Disabled/soft-deleted principals cannot authenticate or use existing tokens. | |

---

## 3. Input validation & injection (ASVS V5 · API8 · A03:2021)

| # | Test | Expected control | Result |
|---|---|---|---|
| 3.1 | **SQL injection** across API params, query strings, FHIR search params, HL7 fields | Parameterized queries / EF Core; no injection. | |
| 3.2 | **XSS** (stored/reflected/DOM) in portal, esp. fields rendered from FHIR/HL7 content | Output encoding + CSP; no script execution. | |
| 3.3 | Command / OS injection where inputs reach process/file operations | No injection; safe APIs used. | |
| 3.4 | XML/JSON parsing abuse — **XXE**, JSON depth/entity bombs on FHIR/HL7 ingestion | External entities disabled; parser limits enforced; no DoS via nesting. | |
| 3.5 | HL7v2 / FHIR malformed & oversized message handling; malformed segments, encoding tricks | Robust validation; failures handled without crash or injection; size limits. | |
| 3.6 | Header/CRLF injection, HTTP request smuggling | Rejected; no response splitting. | |
| 3.7 | Mapping/transformation logic abuse (path/array `N-level` handling) with hostile mappings | No traversal into unexpected data; no code execution via mapping config. | |
| 3.8 | Mass/oversized payloads (within non-DoS limits) | Request size limits enforced. | |

---

## 4. Webhook HMAC verification (ASVS V2/V13 · API2/API8)

| # | Test | Expected control | Result |
|---|---|---|---|
| 4.1 | Submit webhook with **no signature** | Rejected. | |
| 4.2 | Submit with **invalid / wrong-key** signature | Rejected. | |
| 4.3 | **Signature stripping / algorithm downgrade** | Rejected; server enforces HMAC-SHA256. | |
| 4.4 | **Replay** a validly-signed payload | Rejected/idempotent (timestamp/nonce window enforced). | |
| 4.5 | **Timing side-channel** on signature compare | Constant-time comparison used. | |
| 4.6 | Empty/weak/default HMAC secret; secret shared across tenants | Strong per-source secret required; not guessable; not reused where it shouldn't be. | |
| 4.7 | Body-vs-signature mismatch (sign one body, send another) | Rejected; signature covers the exact body. | |

---

## 5. SSRF via EHR source `BaseUrl` (ASVS V12 · API7 · A10:2021)

| # | Test | Expected control | Result |
|---|---|---|---|
| 5.1 | Configure a source `BaseUrl` pointing at **internal/link-local** hosts (`169.254.169.254`, `localhost`, RFC1918, container-network names) | Blocked; server-side fetch does not reach internal targets. | |
| 5.2 | **Cloud metadata** endpoint access via source connector or any server fetch | Blocked; no credential/metadata leakage. | |
| 5.3 | **Plaintext / non-HTTPS `BaseUrl`** | Rejected (HTTPS-only validation). | |
| 5.4 | **DNS rebinding / redirect** to internal host after validation (TOCTOU) | Resolved-IP validated at fetch time; redirects to internal ranges blocked. | |
| 5.5 | JWKS URL / SMART launch URLs / any other server-controlled outbound fetch | Same SSRF protections applied to all outbound fetches, not just source `BaseUrl`. | |
| 5.6 | SSRF via file/gopher/other schemes | Only `https` allowed. | |

---

## 6. Secrets exposure (ASVS V6/V14 · API8 · A05:2021)

| # | Test | Expected control | Result |
|---|---|---|---|
| 6.1 | Inspect API responses, error messages, stack traces for secrets/connection strings/tokens | No secrets leaked; generic errors in non-dev. | |
| 6.2 | JWKS and discovery endpoints expose **public** keys only | No private key material exposed. | |
| 6.3 | Portal bundle / source maps / config for embedded secrets | No secrets in client bundle. | |
| 6.4 | Verify secrets are sourced from **Key Vault**, not hardcoded/env-in-image | No secrets in container image, git, or logs (corroborates gitleaks CI). | |
| 6.5 | **PHI/secret masking in logs** (`PhiMaskingEnricher`) — trigger logged events and inspect | PHI and secrets masked in logs. | |
| 6.6 | Verbose/debug endpoints (Swagger, health, actuator-style) in non-dev | Not exposed / not leaking sensitive detail in production-like config. | |

---

## 7. Security headers & TLS (ASVS V9/V14 · A05:2021)

| # | Test | Expected control | Result |
|---|---|---|---|
| 7.1 | TLS config — protocol versions, cipher suites, cert validity/chain | TLS 1.2+; no weak ciphers; valid cert. | |
| 7.2 | **HSTS** present (non-dev) with sane max-age | Present. | |
| 7.3 | HTTP→HTTPS redirect (`UseHttpsRedirection`); no sensitive endpoint over HTTP | Enforced. | |
| 7.4 | Security-headers middleware — CSP, X-Content-Type-Options, X-Frame-Options/frame-ancestors, Referrer-Policy, Permissions-Policy | Present and effective (esp. CSP for the portal). | |
| 7.5 | CORS policy — origins, credentials, methods | Not overly permissive; no `*` with credentials. | |
| 7.6 | Cookie flags (if any auth cookies) — Secure, HttpOnly, SameSite | Set appropriately. | |

---

## 8. Session & token management (ASVS V3 · API2)

| # | Test | Expected control | Result |
|---|---|---|---|
| 8.1 | Token lifetime — access token short-lived; expiry enforced (automatic logoff) | Expired tokens rejected. | |
| 8.2 | Logout invalidates refresh/session server-side | Post-logout tokens unusable. | |
| 8.3 | Concurrent sessions / token binding behavior as designed | No unexpected session fixation or sharing. | |
| 8.4 | No sensitive tokens in URL/query, browser history, or logs | Tokens only in headers/secure storage. | |

---

## 9. Rate-limiting effectiveness (ASVS V11 · API4 Unrestricted Resource Consumption)

| # | Test | Expected control | Result |
|---|---|---|---|
| 9.1 | Auth endpoints throttle rapid requests | Rate limit engages; 429 returned. | |
| 9.2 | Webhook endpoints throttle abuse | Rate limit engages. | |
| 9.3 | Expensive endpoints (Patient aggregation fan-out, pipeline triggers) resource-bounded | No unbounded amplification; limits/timeouts present. | |
| 9.4 | Rate-limit bypass attempts (header spoofing `X-Forwarded-For`, casing, path tricks) | Limit keyed on a trustworthy identifier; not trivially bypassable. | |
| 9.5 | Pagination/query limits on list endpoints (`API4`) | Max page size enforced; no unbounded result sets. | |

---

## 10. Dependency & known-CVE review (ASVS V14 · A06:2021 · API8)

| # | Test | Expected control | Result |
|---|---|---|---|
| 10.1 | Enumerate .NET/NuGet and Angular/npm dependencies; check for known CVEs | No exploitable known-vulnerable components (corroborates CI SCA). | |
| 10.2 | Framework/runtime versions (.NET 9, ASP.NET Core, SQL client, Angular) patched | On supported, patched versions. | |
| 10.3 | Container base images for known CVEs | Base images patched. | |
| 10.4 | Exposed component versions via banners/headers/errors | Minimal version disclosure. | |

---

## 11. Business logic & pipeline-specific abuse (ASVS V1 · API6 Sensitive Business Flows)

| # | Test | Expected control | Result |
|---|---|---|---|
| 11.1 | Abuse ingestion→mapping→destination pipeline to route data to an attacker-controlled destination | Destination configuration authorized; HTTPS-only; no cross-tenant routing. | |
| 11.2 | De-identification (Safe Harbor / k-anonymity) bypass — get identifiable data out of a de-id path | De-id enforced where required; no leakage. | |
| 11.3 | Audit-log tamper attempt (append-only, hash-chained `UserActivityAuditLog`) | Update/delete blocked; chain break detectable. | |
| 11.4 | Workflow/ranked-engine manipulation to skip authz or replay steps | Steps re-authorized; no replay bypass. | |

---

## Coverage sign-off

| Section | Covered? | Tester initials |
|---|---|---|
| 1 Authentication | | |
| 2 Authorization / multi-tenant | | |
| 3 Input validation / injection | | |
| 4 Webhook HMAC | | |
| 5 SSRF | | |
| 6 Secrets | | |
| 7 Headers / TLS | | |
| 8 Session / tokens | | |
| 9 Rate limiting | | |
| 10 Dependencies / CVEs | | |
| 11 Business logic / pipeline | | |

_Any item marked Fail must have a corresponding entry in [`remediation-tracker.md`](./remediation-tracker.md)._
