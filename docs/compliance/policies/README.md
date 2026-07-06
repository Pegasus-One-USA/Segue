# FHIRBridge HIPAA Security & Privacy Policy Manual

**Policy ID:** POL-000
**Document Type:** Policy Manual Index & Governance Overview
**Status:** ADOPTED
**Owner:** [ROLE — Security Official]
**Applies to:** FHIRBridge platform, its workforce, and all systems that create, receive, maintain, or transmit electronic Protected Health Information (ePHI).

---

## 1. Purpose

This manual is the authoritative written set of administrative, physical, and technical safeguard policies adopted by [ORGANIZATION TO COMPLETE] ("the Organization") to comply with the HIPAA Security Rule (45 CFR §§164.302–164.318), the HIPAA Privacy Rule as applicable to a Business Associate, and the HITECH Breach Notification Rule (45 CFR §§164.400–164.414).

The Organization operates **FHIRBridge**, a .NET 9 healthcare data-integration platform that ingests, transforms, and routes FHIR/HL7 data (including ePHI) between electronic health record (EHR) systems and downstream destinations. Depending on the customer relationship, the Organization acts as a **Business Associate** and, in some deployments, a **Covered Entity**. These policies are written to satisfy the higher of the applicable obligations.

This folder (`docs/compliance/policies/`) is the authoritative policy set. It replaced an earlier fill-in-the-blank draft (`security-policies-templates.md`), which has been removed.

## 2. Scope

These policies apply to:

- All workforce members — employees, contractors, interns, and temporary staff — regardless of location.
- All FHIRBridge environments (development, staging, production) and supporting infrastructure (Azure cloud services, databases, key stores, CI/CD).
- All service/machine identities and integrations that touch ePHI.
- All media and devices used to access, store, or transmit ePHI.

## 3. Policy Index

| ID | Policy | HIPAA Citation | Safeguard Class |
|----|--------|----------------|-----------------|
| POL-001 | [Security Management Process](01-security-management-process.md) | §164.308(a)(1) | Administrative |
| POL-002 | [Assigned Security Responsibility](02-assigned-security-responsibility.md) | §164.308(a)(2) | Administrative |
| POL-003 | [Workforce Security](03-workforce-security.md) | §164.308(a)(3) | Administrative |
| POL-004 | [Information Access Management](04-information-access-management.md) | §164.308(a)(4) | Administrative |
| POL-005 | [Security Awareness & Training](05-security-awareness-training.md) | §164.308(a)(5) | Administrative |
| POL-006 | [Security Incident Procedures](06-security-incident-procedures.md) | §164.308(a)(6) | Administrative |
| POL-007 | [Contingency Plan](07-contingency-plan.md) | §164.308(a)(7) | Administrative |
| POL-008 | [Evaluation](08-evaluation.md) | §164.308(a)(8) | Administrative |
| POL-009 | [Access Control & Authentication](09-access-control-and-authentication.md) | §164.312(a),(d) | Technical |
| POL-010 | [Audit Controls](10-audit-controls.md) | §164.312(b) | Technical |
| POL-011 | [Integrity & Transmission Security](11-integrity-and-transmission-security.md) | §164.312(c),(e) | Technical |
| POL-012 | [Facility & Device Security](12-facility-and-device-security.md) | §164.310 | Physical |
| POL-013 | [Data Retention & Disposal](13-data-retention-and-disposal.md) | §164.310(d), §164.316(b)(2) | Physical/Admin |
| POL-014 | [Breach Notification Procedure](14-breach-notification.md) | §164.400–414, §164.410 | Administrative |

Related supporting documents (referenced, not superseded):

- `docs/compliance/backup-disaster-recovery.md` — operational backup/DR runbook (referenced by POL-007).
- `docs/compliance/baa/` — Business Associate Agreement template + subprocessor register (referenced by POL-014 and workforce/vendor policies).
- `docs/compliance/risk-assessment/` — the §164.308(a)(1) risk analysis that POL-001 operates.

## 4. Policy Governance

### 4.1 Ownership
The **Security Official** (see POL-002) owns this manual and is accountable for its currency, accuracy, and enforcement. The **Privacy Official** owns privacy-specific content.

### 4.2 Approval
Each policy is formally adopted only after review and sign-off by the approval authorities named in its Approval block. No policy is considered "adopted" until the Approval block is completed by [ORGANIZATION TO COMPLETE] and dated.

### 4.3 Review Cadence
- Every policy is reviewed **at least annually**, and additionally upon any of the following triggers: a material change to FHIRBridge architecture or hosting, a security incident or breach, a change in law/regulation, or findings from an evaluation (POL-008).
- The Security Official maintains a review calendar and records each review (even a no-change review) in the policy's Revision History table.

### 4.4 Revision Control
- Every policy carries a monotonically increasing version number and a Revision History table.
- Material changes require re-approval; editorial changes are logged but may be approved by the Security Official alone.
- Prior versions are retained for **six (6) years** from the date they were last in effect, per §164.316(b)(2)(i).

### 4.5 Distribution & Acknowledgement
Policies are made available to all workforce members. New hires acknowledge the manual during onboarding, and all workforce members re-acknowledge upon material change and at least annually (see POL-005).

### 4.6 Enforcement
Violations are subject to the sanction policy in POL-001 §4.3. Enforcement authority rests with the Security Official in coordination with [ROLE — HR / People Operations] and management.

## 5. Documentation Retention (§164.316(b)(2))

All policies, procedures, and the evidence generated in operating them (training records, access reviews, risk analyses, incident records, evaluation reports) are retained for **six (6) years** from the later of the date of creation or the date last in effect. Note: ePHI itself and the FHIRBridge audit trail are retained on a **7-year** schedule (see POL-010, POL-013), which meets or exceeds the documentation requirement.

## 6. Revision History

| Version | Date | Author | Summary |
|---------|------|--------|---------|
| 1.0 | [ORGANIZATION TO COMPLETE] | [ROLE — Security Official] | Initial adopted manual superseding the template draft. |

## 7. Approval

| Role | Name | Signature | Date |
|------|------|-----------|------|
| Security Official | [ORGANIZATION TO COMPLETE] | | |
| Privacy Official | [ORGANIZATION TO COMPLETE] | | |
| Executive Sponsor | [ORGANIZATION TO COMPLETE] | | |
