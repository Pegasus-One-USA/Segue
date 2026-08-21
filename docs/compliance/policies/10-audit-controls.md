# Audit Controls Policy

**Policy ID:** POL-010
**HIPAA Citation:** §164.312(b) — Audit Controls
**Status:** ADOPTED
**Owner:** [ROLE — Security Official]
**Version:** 1.0

---

## 1. Purpose

This policy establishes the hardware, software, and procedural mechanisms that record and examine activity in FHIRBridge systems that contain or use electronic Protected Health Information (ePHI), satisfying §164.312(b).

## 2. Scope

This policy applies to all FHIRBridge audit and logging mechanisms, including the tamper-evident application audit trail (`AuditLog`, hash-chained), its companion governance tables (`AuthenticationLog`, `SmartLaunchLog`, `DataAccessLog`, `SecurityEvent`, and the Operations log tables — see `docs/backend/08-governance-logging-status.md` for the full inventory and current implementation status), structured application logs (Serilog), and authentication/access/data-access events.

> **Implementation note:** the table named in this policy as of a prior revision (`UserActivityAuditLog`) was replaced by `AuditLog` (2026-07). See the status doc above before assuming any specific table/mechanism named here still matches the code — that doc is the source of truth for what's actually implemented versus planned.

## 3. Policy Statements

### 3.1 What Is Logged
FHIRBridge records security-relevant events, including at minimum:
- Authentication events: successful and failed logins, account lockouts, MFA challenges.
- Authorization/access-control changes: RBAC permission grants, modifications, and revocations; user provisioning/deprovisioning.
- ePHI data-access and configuration events attributable to a unique identity.
- Security-control events: rate-limiting triggers, HMAC webhook validation failures, and administrative actions.

### 3.2 Tamper-Evidence and Append-Only Integrity
- The `AuditLog` table is **append-only and hash-chained**: each record is cryptographically linked to the prior record (`AuditLog.ComputeHash`/`VerifyOwnHash`) so that any insertion, modification, or deletion breaks the chain and is detectable. `AuditLog`, `AuthenticationLog`, `SmartLaunchLog`, and `DataAccessLog` are all enforced append-only at the persistence layer (`IAppendOnlyEntity` + a `SaveChanges` interceptor guard that throws on any Modify/Delete attempt). `SecurityEvent` is deliberately the one exception — its `Resolved` flag must stay mutable for triage.
- Modification or deletion of audit records is **prohibited and technically prevented**. Audit records are exempt from the standard deletion paths and are retained through end-of-retention regardless of other data lifecycle actions (see POL-013).
- Full-chain hash verification (`IAuditChainVerificationService`, walking the entire `AuditLog` table, not just a window) runs in two ways: **on demand**, as part of generating the HIPAA/SOC2 compliance report (`GET /api/v1/governance/reports/hipaa-audit`); and **on a schedule**, via `AuditChainVerificationWorker` (Worker host), enabled by default at a **24-hour** cadence (configurable via `AuditChainVerification:IntervalHours`) — satisfying this policy's verification-cadence requirement without relying on someone remembering to pull a report. A verification failure raises a `Critical` `SecurityEvent` (`EventType = "AuditChainBroken"`) and is treated as a security incident (POL-006).

### 3.3 PHI Protection in Logs
- Application logs must **not contain PHI**. PHI is masked before write by the platform's PHI-masking log enricher. Introducing PHI into logs is prohibited; any discovered exposure is handled as an incident.

### 3.4 Log Review and Monitoring
- Audit and log data are reviewed under the information system activity review (POL-001) on a defined cadence, and are the primary source for incident detection (POL-006).
- Logs are forwarded to [ORGANIZATION TO COMPLETE — SIEM/log-management solution]; alerting is configured for [ORGANIZATION TO COMPLETE — e.g., repeated lockouts, hash-chain breaks, webhook signature failures, anomalous access volume].

### 3.5 Retention and Protection
- Audit records are retained for **7 years** (see POL-013) on immutable, append-only storage.
- Access to audit logs is restricted (read-only for auditors; no modify/delete for anyone) and is itself logged.
- Time synchronization: systems generating audit records use a reliable, synchronized time source so timestamps are accurate and correlatable. [ORGANIZATION TO COMPLETE — time source, e.g., NTP/Azure time.]

## 4. Roles & Responsibilities

| Role | Responsibility |
|------|----------------|
| [ROLE — Security Official] | Owns audit-control policy; ensures reviews and hash-chain verification occur. |
| [ROLE — IT Administrator / DevOps] | Operates logging pipeline, SIEM forwarding, alerting, and time sync. |
| [ROLE — Auditor / Compliance] | Performs read-only log reviews and investigations. |
| All workforce members | Never disable logging or introduce PHI into logs. |

## 5. Enforcement / Sanctions

Tampering with logs, disabling audit controls, or introducing PHI into logs is a serious violation subject to the sanction policy in POL-001 §3.3.

## 6. Review Cadence

Reviewed at least **annually** and when logging architecture changes.

## 7. Revision History

| Version | Date | Author | Summary |
|---------|------|--------|---------|
| 1.0 | [ORGANIZATION TO COMPLETE] | [ROLE — Security Official] | Initial adopted policy. |

## 8. Approval

| Role | Name | Signature | Date |
|------|------|-----------|------|
| Security Official | [ORGANIZATION TO COMPLETE] | | |
| IT Administrator | [ORGANIZATION TO COMPLETE] | | |
