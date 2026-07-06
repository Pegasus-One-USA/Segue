# Data Retention & Disposal Policy

**Policy ID:** POL-013
**HIPAA Citation:** §164.310(d)(2)(i) — Media Disposal; §164.316(b)(2) — Documentation Retention; supports §164.312(b) Audit Controls
**Status:** ADOPTED
**Owner:** [ROLE — Security Official]
**Version:** 1.0

---

## 1. Purpose

This policy defines how long FHIRBridge data is retained and how it is securely disposed of at end-of-life, balancing regulatory, contractual, and operational requirements with the obligation to minimize retained ePHI. It supports HIPAA documentation-retention and media-disposal requirements.

## 2. Scope

This policy applies to all FHIRBridge data: operational ePHI, the hash-chained immutable audit trail, backups, de-identified datasets, secrets/key material, and PHI-bearing outputs delivered to destinations, as well as the policy/compliance documentation of this program.

## 3. Policy Statements

### 3.1 Retention Schedule
| Data Category | Retention Period | Enforced By |
|---------------|------------------|-------------|
| Operational ePHI | **7 years** (unless a shorter/longer contractual term applies) | FHIRBridge 7-year retention engine |
| Audit trail (`UserActivityAuditLog`) | **7 years**, immutable | Append-only, hash-chained store |
| Backups | Consistent with 7-year requirement | Backup schedule (POL-007) |
| Security/compliance documentation, training, incident, risk, access-review records | **6 years** from creation or last-in-effect (§164.316(b)(2)) | Records management |
| De-identified data | Per business need (not PHI) | [ORGANIZATION TO COMPLETE] |

- The **7-year retention engine** enforces lifecycle for ePHI. Where a Business Associate Agreement or law specifies a different period, the longer period governs; [ORGANIZATION TO COMPLETE — confirm any customer-specific terms].
- Retention periods start from the applicable trigger date (creation, last activity, or contract end) as configured.

### 3.2 Minimum Necessary / Data Minimization
- ePHI is retained only as long as required. Data that is no longer needed and past its retention trigger is disposed of on schedule.

### 3.3 Immutable Audit Exception
- The audit trail is **append-only and immutable** and is **exempt from routine deletion**. Audit records are not deleted before the end of their 7-year retention, even when the ePHI they reference is disposed of. This exception preserves the tamper-evident record required by POL-010 and is the authoritative history of access and changes.
- Disposal of operational ePHI is itself recorded in the audit trail.

### 3.4 Secure Disposal
- At end-of-retention, ePHI is disposed of through a **documented, approved, and logged** process:
  - Database/storage records are securely deleted; media is sanitized or destroyed per [ORGANIZATION TO COMPLETE — e.g., NIST SP 800-88].
  - Cloud-stored data relies on the provider's certified deletion/crypto-erase processes; encryption keys tied to disposed data may be destroyed to render residual copies unrecoverable.
  - Backups containing disposed ePHI are aged out per the backup rotation so retained copies do not persist beyond schedule.
- Physical media disposal follows POL-012 §3.4. Each disposal action records what was disposed, when, by whom, and the method, and is retained for six (6) years.

### 3.5 Legal Holds
- When litigation, investigation, or regulatory inquiry is reasonably anticipated, a **legal hold** suspends scheduled disposal for the affected data until released. Legal holds are managed by [ROLE — Legal / Compliance] and override the retention/disposal schedule while in effect.

### 3.6 Return or Destruction at BAA Termination
- Upon termination of a Business Associate relationship, ePHI is returned or destroyed as required by the applicable BAA. Where return/destruction is infeasible, protections continue for as long as the ePHI is retained.

## 4. Roles & Responsibilities

| Role | Responsibility |
|------|----------------|
| [ROLE — Security Official] | Owns retention/disposal policy; approves disposal; ensures immutable-audit integrity. |
| [ROLE — IT Administrator / DevOps] | Operates the retention engine, executes disposal, ages out backups. |
| [ROLE — Legal / Compliance] | Manages legal holds and BAA return/destruction obligations. |
| All workforce members | Do not retain ePHI outside approved systems; follow disposal rules. |

## 5. Enforcement / Sanctions

Retaining ePHI beyond schedule, deleting audit records, or disposing of data insecurely is subject to the sanction policy in POL-001 §3.3.

## 6. Review Cadence

Reviewed at least **annually** and upon changes to retention obligations or disposal methods.

## 7. Revision History

| Version | Date | Author | Summary |
|---------|------|--------|---------|
| 1.0 | [ORGANIZATION TO COMPLETE] | [ROLE — Security Official] | Initial adopted policy. |

## 8. Approval

| Role | Name | Signature | Date |
|------|------|-----------|------|
| Security Official | [ORGANIZATION TO COMPLETE] | | |
| Legal / Compliance | [ORGANIZATION TO COMPLETE] | | |
