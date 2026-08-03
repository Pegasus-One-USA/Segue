# HIPAA / SOC 2 Control Matrix — FHIRBridge

This matrix maps HIPAA Security Rule §164.312 technical safeguards and SOC 2 Trust Services Criteria (TSC) to concrete, implemented features of the FHIRBridge platform (.NET 9, SQL Server, Azure Key Vault, docker-compose).

**Status legend**
- **Implemented** — control is realized in application code / platform and active by default.
- **Config-required** — control is available in code but depends on correct deployment configuration or infrastructure settings (e.g., Key Vault, HTTPS certs, TDE, environment secrets).
- **Organizational** — control requires an organizational process, contract, or attestation that lives outside the codebase.

---

## Table 1 — HIPAA Security Rule Technical Safeguards (45 CFR §164.312)

| Requirement | How FHIRBridge addresses it | Status |
|---|---|---|
| **§164.312(a)(1) Access Control** | Role-based access control (RBAC) enforced via permission policies on API endpoints; users are granted scoped permissions rather than blanket access. All PHI-bearing and administrative endpoints require an authenticated, authorized principal. | Implemented |
| **§164.312(a)(2)(i) Unique User Identification** | Every principal authenticates as a unique user identity — local accounts (PBKDF2-SHA256, 350,000 iterations) or federated SSO identity (Entra/Google). User identity is carried on the JWT and stamped into the hash-chained `AuditLog`, giving per-user attribution of actions; every login/logout/lockout/MFA/SSO event is separately recorded in `AuthenticationLog`. | Implemented |
| **§164.312(a)(2)(iii) Automatic Logoff** | Session bounding is enforced through short-lived JWT bearer tokens (expiry-based termination of authenticated sessions); expired tokens are rejected. Absolute token lifetime is set in configuration. | Config-required |
| **§164.312(a)(2)(iv) Encryption and Decryption** | Data at rest: SQL Server **Transparent Data Encryption (TDE)** at the database layer, with a **startup TDE health check** that fails/warns if TDE is not enabled. Secrets encrypted in **Azure Key Vault** (secret provider). Data-Protection key ring encrypts OAuth/launch tokens. **Safe Harbor + k-anonymity de-identification** provides an additional PHI-minimization control. | Config-required |
| **§164.312(b) Audit Controls** | **Hash-chained, append-only `AuditLog`** (every config/entity change captured automatically via `AuditingSaveChangesInterceptor`, zero call-site changes needed) with an `IAppendOnlyEntity` guard preventing update/delete of audit rows. Companion tables: `AuthenticationLog` (local + SSO login/logout/lockout/MFA), `SmartLaunchLog` (SMART on FHIR launches), `DataAccessLog` (every patient/resource governance decision, PHI-free), `SecurityEvent`. Downloadable evidence: `GET /api/v1/governance/reports/hipaa-audit` (PDF, includes full-chain hash verification). **Serilog** structured logging with a **`PhiMaskingEnricher`** masks PHI before logs are written. Full inventory and status: `docs/backend/08-governance-logging-status.md`. | Implemented |
| **§164.312(c)(1) Integrity** | Audit log integrity via hash chaining + append-only guard (any tampering breaks the chain and is detectable; verifiable via the compliance report above). Application-layer input validation and typed FHIR/HL7 processing preserve message integrity. TLS protects data in transit from modification (see (e)(1)). Retention/purge enforcement for these tables is **not yet implemented** — see the status doc referenced above. | Implemented |
| **§164.312(d) Person or Entity Authentication** | Dual authentication schemes: **JWT bearer** (local accounts) and **Microsoft Entra SSO**. Local auth hardened with **PBKDF2-SHA256 (350k iterations)** password hashing, **account lockout (5 attempts / 15 min, configurable under `LocalAuth:Lockout`)**, **rate limiting on auth endpoints**, and **TOTP-based MFA**. Machine-to-machine inbound webhooks authenticated via **HMAC-SHA256 signature validation**. | Implemented |
| **§164.312(e)(1) Transmission Security** | HTTPS enforced end-to-end: `RequireHttpsMetadata`, `UseHttpsRedirection`, and **HSTS** (non-dev environments). **Security-headers middleware** hardens responses. Outbound source connections validated for **HTTPS `BaseUrl`** (plaintext endpoints rejected). Inbound webhook payload authenticity via **HMAC-SHA256**. **Rate limiting** on webhook endpoints mitigates abuse. | Config-required |

---

## Table 2 — SOC 2 Trust Services Criteria (Common Criteria)

| Criterion | How FHIRBridge addresses it | Status |
|---|---|---|
| **CC6.1 — Logical access security controls (identity, encryption, protection of information assets)** | RBAC permission policies; PBKDF2-SHA256 (350k) local password hashing; JWT + Entra SSO authentication; TDE at rest + TDE startup health check; Azure Key Vault for secrets; Data-Protection key ring for token encryption; Safe Harbor / k-anonymity de-identification. | Implemented / Config-required |
| **CC6.2 — Registration and authorization of new users; credential issuance** | Unique user identities via local accounts or Entra SSO; users are provisioned with scoped RBAC permissions; invite/resend-invite flow for onboarding controlled accounts. Deprovisioning executed by disabling accounts. (Formal provisioning/deprovisioning **process** is organizational — see policy templates.) | Implemented + Organizational |
| **CC6.6 — Protection against threats from outside system boundaries** | HTTPS enforcement (`RequireHttpsMetadata`, `UseHttpsRedirection`, HSTS); security-headers middleware; rate limiting on auth and webhook endpoints; HMAC-SHA256 webhook signature validation; source `BaseUrl` HTTPS validation. | Implemented / Config-required |
| **CC6.7 — Restriction of information transmission, movement, and removal** | TLS/HTTPS for all transmission; HTTPS-only outbound source connections; PHI masking in logs (`PhiMaskingEnricher`); de-identification (Safe Harbor + k-anonymity) before data leaves the trust boundary where applicable; Key Vault-held secrets never emitted to logs. | Implemented |
| **CC7.1 — Detection of configuration changes / vulnerabilities (monitoring)** | Startup TDE health check surfaces at-rest encryption misconfiguration; every configuration/entity change is captured automatically in the append-only `AuditLog` (old/new value diff per change, viewable and comparable in the portal's Audit Logs screen); structured Serilog logging enables monitoring pipelines. An Operations log family (`ErrorLog`, `SchedulerHistory`, `RetryHistory`, `ApiRequestLog`, `EndpointHealthCheck`) covers operational anomalies too. (Vulnerability scanning / IDS tooling, and a rule-based Alert Engine over these tables, are not yet built — organizational/planned.) | Implemented + Organizational |
| **CC7.2 — Monitoring of system components for anomalies (security events)** | Hash-chained, append-only `AuditLog` plus `SecurityEvent`/`AuthenticationLog`/`SmartLaunchLog` provide tamper-evident, per-user-attributed records of lockouts, failed logins, and failed SSO/SMART launches. (Automated alerting/SIEM integration on top of these tables — an Alert Engine and OTLP export — is not yet built; see `docs/backend/08-governance-logging-status.md`.) | Implemented + Organizational |
| **CC8.1 — Change management (authorization, testing, deployment)** | Database schema evolution via versioned EF Core migrations; deployment via reproducible docker-compose; ~100+ automated tests gate changes. (Formal change-approval workflow and segregation of duties are organizational — see policy templates.) | Implemented + Organizational |

---

## Remaining organizational controls (outside the codebase)

The technical safeguards above do not by themselves confer HIPAA compliance or SOC 2 certification. The organization must also establish, document, and operate the following:

- **Signed Business Associate Agreements (BAAs)** — with Microsoft Azure (covered cloud services) and with every downstream partner/subcontractor that receives PHI. See the BAA checklist in `security-policies-templates.md`.
- **HIPAA §164.308 Administrative Safeguards / Risk Assessment** — a documented, periodic security risk analysis and risk-management process, assigned security responsibility, and sanction policy.
- **Workforce Security & Training** — background/authorization procedures, role-based access authorization, and recurring HIPAA/security awareness training with retained completion records.
- **Contingency Plan** — data backup plan, disaster-recovery plan, and emergency-mode operations plan, tested on a defined cadence (see `backup-disaster-recovery.md`).
- **Incident Response & Breach Notification** — documented IR procedures and breach-notification process compliant with **HIPAA §164.410** (notification to covered entities without unreasonable delay, no later than 60 days from discovery).
- **Penetration Testing** — independent application and infrastructure penetration testing on a defined cadence, with tracked remediation.
- **SOC 2 Auditor Engagement** — engage a licensed CPA firm for a **Type I** examination (design of controls at a point in time), followed by a **Type II** examination (operating effectiveness over a review period, typically 6–12 months).
