# Workforce Security Policy

**Policy ID:** POL-003
**HIPAA Citation:** §164.308(a)(3) — Workforce Security (Authorization/Supervision, Workforce Clearance, Termination Procedures)
**Status:** ADOPTED
**Owner:** [ROLE — Security Official]
**Version:** 1.0

---

## 1. Purpose

This policy ensures that only appropriately authorized and cleared workforce members have access to electronic Protected Health Information (ePHI) in FHIRBridge, that access is supervised, and that access is promptly revoked when a workforce member separates or changes role. It implements the authorization/supervision, workforce clearance, and termination-procedure specifications of §164.308(a)(3).

## 2. Scope

This policy applies to all workforce members — employees, contractors, interns, temporary staff, and any personnel who access or manage FHIRBridge or its ePHI — and to all local FHIRBridge accounts, Entra SSO identities, and service/machine identities.

## 3. Policy Statements

### 3.1 Authorization and Supervision (§164.308(a)(3)(ii)(A)) — ADDRESSABLE
- Access to FHIRBridge and ePHI is authorized only for workforce members whose role requires it, on a least-privilege, need-to-know basis (see POL-004).
- Each access grant is approved by [ROLE — Manager / Data Owner] before provisioning and recorded.
- Workforce members who access ePHI do so under the supervision of [ROLE — Manager]. Privileged/administrative activity is logged in the FHIRBridge audit trail (POL-010) and subject to the activity review in POL-001.

### 3.2 Workforce Clearance (§164.308(a)(3)(ii)(B)) — ADDRESSABLE
- Before ePHI access is granted, the Organization verifies that the level of access is appropriate to the workforce member's role.
- Pre-employment/pre-engagement screening (including background checks where legally permissible) is performed per [ORGANIZATION TO COMPLETE — screening standard]. Screening records are retained by [ROLE — HR / People Operations].
- Contractors and vendors with ePHI access are covered by an executed Business Associate Agreement or equivalent confidentiality/security obligations before access is granted.

### 3.3 Termination Procedures / Deprovisioning (§164.308(a)(3)(ii)(C)) — ADDRESSABLE
- Upon termination, role change, or end of engagement, the workforce member's access is revoked. Access must be disabled within **[ORGANIZATION TO COMPLETE — e.g., 24 hours]** of the effective separation, and immediately for involuntary or for-cause separations.
- Deprovisioning covers all of the following:
  - Disabling/removing the FHIRBridge local account and revoking RBAC permissions.
  - Removing/disabling the Entra SSO identity or group membership used to reach FHIRBridge.
  - Revoking or rotating any secrets, API keys, service credentials, JWT-issuing credentials, or HMAC webhook secrets the individual controlled.
  - Recovering Organization-issued devices and media (see POL-012).
- A deprovisioning checklist is completed and retained for each separation. [ROLE — HR / People Operations] initiates the process; [ROLE — Security Official / IT Administrator] executes the technical revocation and confirms completion.
- Access removals are recorded in the FHIRBridge audit trail and reconciled during periodic access reviews (POL-004).

### 3.4 Role Changes
- On internal transfer, access is re-evaluated and reduced to the new role's least-privilege set; access no longer needed is removed on the same timeline as termination.

## 4. Roles & Responsibilities

| Role | Responsibility |
|------|----------------|
| [ROLE — HR / People Operations] | Initiates onboarding/offboarding; owns clearance/screening records; triggers deprovisioning. |
| [ROLE — Manager / Data Owner] | Authorizes and supervises access; certifies need-to-know. |
| [ROLE — Security Official / IT Administrator] | Executes technical provisioning and deprovisioning; verifies completion. |
| All workforce members | Return assets; comply with least-privilege. |

## 5. Enforcement / Sanctions

Unauthorized access, failure to deprovision, or circumvention of authorization controls is subject to the sanction policy in POL-001 §3.3.

## 6. Review Cadence

Reviewed at least **annually** and upon change to onboarding/offboarding processes.

## 7. Revision History

| Version | Date | Author | Summary |
|---------|------|--------|---------|
| 1.0 | [ORGANIZATION TO COMPLETE] | [ROLE — Security Official] | Initial adopted policy. |

## 8. Approval

| Role | Name | Signature | Date |
|------|------|-----------|------|
| Security Official | [ORGANIZATION TO COMPLETE] | | |
| HR / People Operations | [ORGANIZATION TO COMPLETE] | | |
