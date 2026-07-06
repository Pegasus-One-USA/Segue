# Risk Treatment Plan — FHIRBridge Security Risk Analysis

**Supports:** HIPAA §164.308(a)(1)(ii)(B) Risk Management (implement measures to reduce risks to a reasonable and appropriate level); follows NIST SP 800-30 risk-response guidance.
**Parent document:** `README.md`. **Risk IDs (R##)** reference `threat-vulnerability-register.md`.

## Scope of this plan

Per methodology, this plan tracks a treatment action for **every residual risk rated Medium or higher**. Residual **Low** risks (R01, R03, R11, R12, R15, R17, R21, R22, R30) are considered acceptable and are monitored through the normal review cadence; they do not appear here.

## Status legend

- **Mitigated** — the technical control is implemented in the FHIRBridge codebase/platform and active; treatment is to keep it configured/verified.
- **Config-required** — the control exists in code but must be correctly set per deployment; action is to verify and lock it in a deployment checklist.
- **Open** — a gap requiring net-new work (code, infrastructure, or organizational process).
- **In progress** — work started.

Owners are marked `[TO ASSIGN]` and target dates `[TO SET]` for the organization to complete.

---

## A. High residual risk — prioritized

| Risk | Action | Owner | Target date | Status |
|---|---|---|---|---|
| **R07 — Ransomware vs. data & backups** | Implement immutable/offline and geo-redundant backups; segment/limit backup credentials; enable server-side malware protection; validate via tested restore. Follow `../backup-disaster-recovery.md`. | [TO ASSIGN — Infra/Ops] | [TO SET] | **Open** |
| **R10 — Data-Protection key ring not persisted across instances** | Configure a persisted, Key Vault-backed Data-Protection key ring (shared store) before any multi-instance/scale-out deployment; document key rotation. | [TO ASSIGN — Platform Eng] | [TO SET] | **Open** |
| **R14 — Backup failure / unrecoverable backups** | Implement automated SQL + DP key-ring backups with integrity verification and scheduled restore tests; align retention to the 7-year requirement (retention engine already present). | [TO ASSIGN — Infra/Ops] | [TO SET] | **Open** |
| **R20 — Availability loss / no DR** | Define RTO/RPO; implement DR failover (multi-AZ/region on target platform), and conduct a documented DR test on a defined cadence. See `../backup-disaster-recovery.md`. | [TO ASSIGN — Infra/Ops] | [TO SET] | **Open** |

---

## B. Medium residual risk

| Risk | Action | Owner | Target date | Status |
|---|---|---|---|---|
| **R02 — Broken/over-broad authorization** | Add authorization test coverage for patient-read/IDOR paths; institute periodic access review of RBAC role definitions. | [TO ASSIGN — Eng + Security] | [TO SET] | **Open** (RBAC enforcement Mitigated; review process pending) |
| **R04 — MFA not enforceable for all local users** | Ship the pending **portal MFA-enrollment UI**; enforce MFA org-wide (local + Entra conditional access). | [TO ASSIGN — Eng] | [TO SET] | **Open** (TOTP MFA backend Mitigated; enrollment UI pending) |
| **R05 — Malicious insider** | Wire audit-log monitoring/alerting for anomalous access; ratify and enforce a workforce sanction policy. | [TO ASSIGN — Security + HR] | [TO SET] | **Open** (audit trail Mitigated; monitoring + policy pending) |
| **R06 — Lost/stolen device** | Establish endpoint policy: full-disk encryption, MDM enrollment, auto-lock/session timeout for portal access. | [TO ASSIGN — IT] | [TO SET] | **Open** (short-lived sessions Mitigated; endpoint policy organizational) |
| **R08 — `AllowedHosts` wildcard** | Set explicit `AllowedHosts` to production hostnames; add to deployment verification checklist. | [TO ASSIGN — Platform Eng] | [TO SET] | **Config-required** |
| **R09 — Permissive CORS** | Restrict CORS to known portal origin(s); prohibit wildcard origins in production; add to deployment checklist. | [TO ASSIGN — Platform Eng] | [TO SET] | **Config-required** |
| **R13 — Third-party / subprocessor breach** | Execute signed BAAs with Microsoft Azure and every downstream ePHI recipient; perform subprocessor due diligence. See BAA checklist in `../security-policies-templates.md`. | [TO ASSIGN — Legal/Compliance] | [TO SET] | **Open** |
| **R16 — Denial of service** | Front the API with a WAF/rate-limited gateway; enable autoscale on the target platform. (App-layer rate limiting already Mitigated.) | [TO ASSIGN — Infra/Ops] | [TO SET] | **Open** |
| **R18 — Vulnerable dependencies** | Add automated SCA + container-image scanning to CI; define a patch SLA for critical/high CVEs. | [TO ASSIGN — DevSecOps] | [TO SET] | **Open** |
| **R19 — Physical / facility compromise** | Host production in a BAA-covered cloud facility; document inherited physical controls; confirm docker-compose host physical security. | [TO ASSIGN — Infra/Ops] | [TO SET] | **Open** ([TO CONFIRM] current host) |
| **R23 — Compromised/spoofed EHR source** | Maintain source allow-listing; execute BAA/interconnection agreements per source; evaluate certificate pinning. | [TO ASSIGN — Integrations] | [TO SET] | **Open** (HTTPS validation + token custody Mitigated) |
| **R24 — Improper de-identification** | Validate de-id configuration per destination; obtain expert-determination review where Safe Harbor is insufficient. | [TO ASSIGN — Data/Privacy] | [TO SET] | **Open** (Safe Harbor + k-anonymity Mitigated where configured) |
| **R25 — Session/token theft (XSS)** | Harden Content-Security-Policy; review portal token-storage strategy for XSS resistance. | [TO ASSIGN — Eng] | [TO SET] | **Open** (security headers + short-lived JWT Mitigated) |
| **R26 — Insufficient monitoring/alerting** | Forward hash-chained audit + Serilog security events to a SIEM with alerting on lockouts, rate-limit rejections, and anomalous access. | [TO ASSIGN — Security/Ops] | [TO SET] | **Open** (logging/audit Mitigated; SIEM/alerting pending) |
| **R27 — Orphaned / stale accounts** | Define provisioning/deprovisioning SLA; implement periodic access recertification; inventory service accounts. | [TO ASSIGN — IT/Security] | [TO SET] | **Open** (account disable + invite flow Mitigated) |
| **R28 — Insecure default/debug config in prod** | Produce and enforce a production hardening checklist (HSTS on, dev pages off, verbose errors off, secrets from Key Vault). | [TO ASSIGN — Platform Eng] | [TO SET] | **Config-required** |
| **R29 — Human error / misrouting** | Institute change control on pipeline/destination configuration; require review before enabling a new destination. | [TO ASSIGN — Eng + Compliance] | [TO SET] | **Open** (RBAC + audit Mitigated; change control organizational) |

---

## C. Cross-cutting organizational actions (required for §164.308 / SOC 2 beyond code)

| Item | Action | Owner | Target date | Status |
|---|---|---|---|---|
| **Business Associate Agreements** | Execute BAAs with Azure and all downstream ePHI recipients/subprocessors. | [TO ASSIGN — Legal/Compliance] | [TO SET] | **Open** |
| **Penetration test** | Engage an independent firm for application + infrastructure pen testing; track remediation to closure. | [TO ASSIGN — Security] | [TO SET] | **Open** |
| **Incident response & breach notification** | Ratify and drill IR procedures; ensure breach notification aligns to HIPAA §164.410 (≤60 days from discovery). | [TO ASSIGN — Security + Legal] | [TO SET] | **Open** |
| **Workforce security & training** | Recurring HIPAA/security awareness training with retained completion records; role-based access authorization. | [TO ASSIGN — HR/Security] | [TO SET] | **Open** |
| **SOC 2 auditor engagement** | Engage a CPA firm for Type I then Type II examination. | [TO ASSIGN — Compliance] | [TO SET] | **Open** |

---

## D. Already-remediated technical safeguards (evidence — kept configured/verified)

These controls addressing residual-Low risks are **Mitigated** in code and are recorded here as evidence of the risk-management measures already in place. They are maintained and verified on the review cadence.

| Control | Addresses | Status |
|---|---|---|
| JWT + Entra SSO authentication; RBAC on all PHI/admin endpoints | R01, R02 | **Mitigated** |
| PBKDF2-SHA256 (350k) + account lockout + auth rate limiting | R03 | **Mitigated** |
| HTTPS/HSTS enforcement; source `BaseUrl` HTTPS validation | R11 | **Mitigated** |
| Serilog `PhiMaskingEnricher`; PHI-free data-access audit | R12 | **Mitigated** |
| Hash-chained, append-only audit log (`GuardAppendOnlyLogs`) | R15 | **Mitigated** |
| EF Core parameterization; typed FHIR/HL7 + input validation | R17, R30 | **Mitigated** |
| Azure Key Vault secret custody; secrets excluded from logs | R21 | **Mitigated** |
| HMAC-SHA256 webhook signature validation + rate limiting | R22 | **Mitigated** |
| 7-year retention engine | supports R14 retention requirement | **Mitigated** |

> **Column-level encryption note (from control matrix):** confidentiality at rest currently relies on **SQL Server TDE** at the database layer plus a startup TDE health check. Column/field-level encryption for the most sensitive elements is a recommended future enhancement and should be tracked as an organizational decision — **[ORGANIZATION TO COMPLETE — accept TDE-only or fund column-level encryption]**.
