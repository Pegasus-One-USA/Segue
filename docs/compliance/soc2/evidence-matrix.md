# SOC 2 Evidence Matrix (Auditor PBC List) — FHIRBridge

**Purpose.** This is the **PBC ("Provided By Client") list** — the evidence an independent SOC 2 auditor will request, mapped to the control it substantiates and the Trust Services Criteria (TSC) reference. Use it as the running tracker during fieldwork.

**How to read it**
- **Evidence artifact** — the concrete thing the auditor inspects (an export, a config, a signed doc, a screenshot, a log sample).
- **Where it lives / [TO PRODUCE]** — repo path, system, or `[TO PRODUCE]` if it must be created/exported.
- **Owner [TO ASSIGN]** — the accountable person; fill in during kickoff.
- For **Type II**, most artifacts must be sampled **across the observation window**, not a single snapshot (note the "period sampling" column).

**Status:** [TO COMPLETE at kickoff] · **Observation window:** [DEFINE] · **Report type:** [Type I | Type II]

---

## A. Governance, policy & organization (CC1, CC2, CC3)

| # | Control | TSC ref | Evidence artifact | Where it lives / [TO PRODUCE] | Period sampling (Type II) | Owner [TO ASSIGN] |
|---|---|---|---|---|---|---|
| A1 | Adopted security policy manual | CC1.1, CC5.3 | Signed/dated policies POL-000…POL-013 | `../policies/` (Approval blocks `[TO PRODUCE]` — signatures/dates) | Version in effect during window | |
| A2 | Assigned security responsibility | CC1.2, CC1.3 | POL-002 + org chart + job descriptions | `../policies/02-...` + org chart `[TO PRODUCE]` | — | |
| A3 | Management/board security oversight | CC1.2, CC4.1 | Meeting minutes of recurring security review | `[TO PRODUCE]` — quarterly minutes | Each period meeting | |
| A4 | Code of conduct / ethics | CC1.1 | Signed acknowledgements | `[TO PRODUCE]` | New hires in window | |
| A5 | Policy distribution & acknowledgement | CC2.2, CC1.4 | Acknowledgement log | `[TO PRODUCE]` (per POL-000 §4.5) | New/updated in window | |
| A6 | System description ("Section III") | CC2.1 | System description narrative | `[TO PRODUCE]` | — | |
| A7 | External security communication / intake | CC2.3 | Trust page + security contact/intake channel | `[TO PRODUCE]` | — | |
| A8 | **Formal risk assessment** | CC3.1–CC3.4 | Risk-assessment report + **risk register** (methodology, scoring, treatment, owners) | `../risk-assessment/` (register `[TO PRODUCE]`) | Annual refresh in window | |
| A9 | Asset inventory | CC3.2, CC6.1 | Asset inventory | `../risk-assessment/asset-inventory.md` | Current at test | |

---

## B. Logical access & authentication (CC6, C1)

| # | Control | TSC ref | Evidence artifact | Where it lives / [TO PRODUCE] | Period sampling (Type II) | Owner [TO ASSIGN] |
|---|---|---|---|---|---|---|
| B1 | Password hashing | CC6.1 | Code/config: PBKDF2-SHA256, 350k iterations | Codebase (auth module) + control matrix | — (config stable) | |
| B2 | Account lockout | CC6.1, CC6.6 | Config `LocalAuth:Lockout` (5/15min) + lockout log events | Config + log sample | Lockout events in window | |
| B3 | Multi-factor authentication | CC6.1 | TOTP MFA config + enrollment screenshot | App config + `[TO PRODUCE]` screenshot | Enrollment state in window | |
| B4 | SSO / federated identity | CC6.1, CC6.2 | Entra SSO configuration | Azure Entra tenant config `[TO PRODUCE]` export | Config during window | |
| B5 | RBAC authorization | CC6.1, CC6.3 | Permission-policy definitions + endpoint enforcement | Codebase (permission policies) | — | |
| B6 | User provisioning (JML — joiner) | CC6.2 | Invite/onboarding records tied to HR event | `[TO PRODUCE]` — ticket + timestamps | Joiners in window | |
| B7 | User deprovisioning (leaver) | CC6.3 | Account-disable records tied to termination | `[TO PRODUCE]` | Leavers in window | |
| B8 | **Periodic user access review** | CC6.2, CC6.3 | Quarterly access-review export with sign-off | `[TO PRODUCE]` — access-review records | Each quarterly review | |
| B9 | Privileged-access control | CC6.1, CC6.3 | Admin/prod/key-vault access list + approval | `[TO PRODUCE]` | Reviews in window | |
| B10 | Machine-to-machine auth | CC6.1, CC6.6 | HMAC-SHA256 webhook signature validation | Codebase (webhook handler) | — | |

---

## C. Encryption & confidentiality (CC6.7, C1)

| # | Control | TSC ref | Evidence artifact | Where it lives / [TO PRODUCE] | Period sampling (Type II) | Owner [TO ASSIGN] |
|---|---|---|---|---|---|---|
| C1 | Encryption at rest (TDE) | CC6.1, C1.1 | SQL TDE enabled proof + **startup TDE health-check** log | `[TO PRODUCE]` DB config export + app log | Health-check passes in window | |
| C2 | Secrets management | CC6.1 | **Azure Key Vault** config (access policies/RBAC, secret list metadata) | Azure `[TO PRODUCE]` export | Access changes in window | |
| C3 | Token encryption | CC6.1 | Data-Protection key-ring config | App config `[TO PRODUCE]` | — | |
| C4 | Encryption in transit | CC6.7 | HTTPS/HSTS/security-headers config; TLS cert; outbound `BaseUrl` HTTPS validation | Codebase + `[TO PRODUCE]` cert/scan | Cert validity in window | |
| C5 | De-identification | C1.1, CC6.7 | Safe Harbor + k-anonymity implementation + test evidence | Codebase + test output | — | |
| C6 | PHI log masking | C1.1, CC6.7 | `PhiMaskingEnricher` code + masked-log sample | Codebase + log sample | Log samples across window | |
| C7 | Data classification | C1.1 | Data-classification policy | `[TO PRODUCE]` | — | |
| C8 | Retention & disposal | C1.2, CC6.5 | 7-year retention engine + POL-013 + **disposal records** | Codebase + `../policies/13-...` + disposal log `[TO PRODUCE]` | Disposals in window | |

---

## D. Audit logging & monitoring (CC4, CC7)

| # | Control | TSC ref | Evidence artifact | Where it lives / [TO PRODUCE] | Period sampling (Type II) | Owner [TO ASSIGN] |
|---|---|---|---|---|---|---|
| D1 | Tamper-evident audit trail | CC7.2, CC7.3 | **Hash-chained `UserActivityAuditLog`** export + `GuardAppendOnlyLogs` code | `[TO PRODUCE]` audit-log export + codebase | Log samples across window | |
| D2 | Audit-chain integrity verification | CC4.1, CC7.2 | Scheduled chain-verification job + results | `[TO PRODUCE]` job + run logs | Each run in window | |
| D3 | Observability | CC7.1, A1.1 | OpenTelemetry config + health-check endpoints | Codebase + `[TO PRODUCE]` dashboard screenshot | Uptime data across window | |
| D4 | Structured logging | CC7.2 | Serilog config + representative logs | Codebase + log sample | Samples across window | |
| D5 | Alerting / on-call | CC7.1, CC7.2 | Alert rules + on-call rotation + sample alert | `[TO PRODUCE]` | Alerts in window | |
| D6 | Deficiency / findings register | CC4.2, CC7.4 | Findings tracker with remediation dates | `[TO PRODUCE]` | Entries across window | |
| D7 | Periodic control self-evaluation | CC4.1 | POL-008 evaluation reports | `[TO PRODUCE]` | Each evaluation in window | |

---

## E. System operations & incident response (CC7)

| # | Control | TSC ref | Evidence artifact | Where it lives / [TO PRODUCE] | Period sampling (Type II) | Owner [TO ASSIGN] |
|---|---|---|---|---|---|---|
| E1 | Incident-response plan | CC7.3, CC7.4 | POL-006 + severity/SLA definitions | `../policies/06-...` | — | |
| E2 | Incident register | CC7.3, CC7.4, CC7.5 | Incident log with triage/resolution/post-mortem | `[TO PRODUCE]` | Incidents in window (or attestation of none) | |
| E3 | IR tabletop test | CC7.3 | Tabletop-exercise report | `[TO PRODUCE]` | At least one in window | |
| E4 | Breach-notification procedure | CC7.4 | §164.410 procedure | `[TO PRODUCE]` (per POL-006) | — | |
| E5 | Rate limiting | CC6.6, CC7.1 | Rate-limit config + rejection log sample | Codebase + log sample | Samples in window | |

---

## F. Change management (CC8)

| # | Control | TSC ref | Evidence artifact | Where it lives / [TO PRODUCE] | Period sampling (Type II) | Owner [TO ASSIGN] |
|---|---|---|---|---|---|---|
| F1 | Change process definition | CC8.1 | Change-management policy/runbook | `[TO PRODUCE]` | — | |
| F2 | Code review / approval gate | CC8.1 | Branch-protection settings + PR review samples | Repo settings `[TO PRODUCE]` + PR history | PR sample across window | |
| F3 | Automated testing gate | CC8.1 | ~100+ test suite + CI pass records | Codebase + CI run logs | CI runs across window | |
| F4 | SAST / secret / dependency scanning | CC8.1, CC7.1 | **CodeQL**, **gitleaks**, dependency-scan, **Dependabot** CI run logs | CI provider `[TO PRODUCE]` run logs + config | Runs across window | |
| F5 | Schema-change control | CC8.1 | Versioned EF Core migrations | Codebase (migrations) | Migrations in window | |
| F6 | Deployment / release approval | CC8.1 | Release approval + rollback procedure | `[TO PRODUCE]` | Releases in window | |

---

## G. Vendor & risk mitigation (CC9)

| # | Control | TSC ref | Evidence artifact | Where it lives / [TO PRODUCE] | Period sampling (Type II) | Owner [TO ASSIGN] |
|---|---|---|---|---|---|---|
| G1 | Subservice organization (Azure) | CC9.2 | **Azure/Microsoft SOC 2 report** (subservice) | `[TO PRODUCE]` — obtain from Microsoft | Report covering window | |
| G2 | Signed BAAs | CC9.2, C1.1 | BAA with Azure + downstream PHI recipients | `../baa/` (executed BAAs `[TO PRODUCE]`) | In effect during window | |
| G3 | Vendor register & review | CC9.2 | Vendor inventory + risk tier + annual SOC2 review notes | `[TO PRODUCE]` | Reviews in window | |
| G4 | Penetration test | CC7.1, CC4.1 | Independent pen-test report + remediation tracker | `[TO PRODUCE]` | Test + remediation in window | |
| G5 | Cyber insurance (if applicable) | CC9.1 | Policy declaration page | `[TO PRODUCE]` | In effect during window | |

---

## H. Availability (A1)

| # | Control | TSC ref | Evidence artifact | Where it lives / [TO PRODUCE] | Period sampling (Type II) | Owner [TO ASSIGN] |
|---|---|---|---|---|---|---|
| H1 | Backup strategy | A1.2 | Backup/DR runbook | `../backup-disaster-recovery.md` | — | |
| H2 | Backups executed | A1.2 | Backup job logs (SQL + Data-Protection key ring) | `[TO PRODUCE]` | Backups across window | |
| H3 | Restore test | A1.3 | Restore-test report | `[TO PRODUCE]` | At least one in window | |
| H4 | DR test | A1.2, A1.3 | DR-exercise after-action report | `[TO PRODUCE]` | At least one in window | |
| H5 | Uptime / SLA monitoring | A1.1 | Uptime objective + monitoring dashboard | `[TO PRODUCE]` | Data across window | |
| H6 | Capacity monitoring | A1.1 | Capacity metrics + scaling procedure | `[TO PRODUCE]` | Data across window | |

---

## Producing the technical exports (quick reference)

- **Audit-log export** — query `UserActivityAuditLog` for the sampled dates; include hash-chain columns so the auditor can verify integrity.
- **CI run logs** — export CodeQL/gitleaks/dependency-scan/Dependabot job history from the CI provider for the window.
- **Access-review records** — export user↔permission mappings; capture reviewer sign-off (dated).
- **Key Vault config** — export access policies/RBAC assignments and secret metadata (never secret values).
- **TDE proof** — DB-level TDE status + the application's startup TDE health-check log line.
- **Azure SOC 2** — request from Microsoft's Service Trust Portal; treat Azure as a subservice organization (decide carve-out vs. inclusive method with the auditor).

> Anything marked `[TO PRODUCE]` is an organizational deliverable, not something the codebase emits. Assign an owner at kickoff and set a due date before the observation window opens.
