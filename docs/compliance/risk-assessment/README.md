# Security Risk Analysis — FHIRBridge

**Regulatory basis:** HIPAA Security Rule §164.308(a)(1)(ii)(A) — Risk Analysis (Required implementation specification).
**Document set version:** 1.0 (Draft for organizational completion)
**Prepared for:** [ORGANIZATION TO COMPLETE — legal entity name] (the Covered Entity / Business Associate operating FHIRBridge)

---

## 1. Purpose

HIPAA §164.308(a)(1)(ii)(A) requires the organization to "conduct an accurate and thorough assessment of the potential risks and vulnerabilities to the confidentiality, integrity, and availability of electronic protected health information (ePHI) held by the organization." This document set is that assessment for the **FHIRBridge** platform.

It is written to be audit-ready: an OCR investigator, HIPAA auditor, or SOC 2 examiner should be able to read this set and understand (a) what ePHI FHIRBridge handles and where it lives, (b) the threats and vulnerabilities to that ePHI, (c) the controls currently in place, (d) the residual risk after those controls, and (e) the plan to treat the risk that remains.

## 2. Scope

**In scope** — the FHIRBridge platform and every component that stores, processes, or transmits ePHI:

- The ASP.NET Core **API host** (authentication, RBAC, FHIR/HL7 ingestion and read endpoints).
- The background **Worker** (pipeline orchestration, normalization, de-identification, field mapping, destination writes).
- The **SQL Server** database (configuration, audit store, and any persisted ePHI).
- **Azure Key Vault** (secret and key custody).
- **Blob / object storage** destinations.
- **EHR source connections** (Epic, Healow, MEDITECH, generic FHIR R4 / HL7 v2).
- The **hash-chained audit store**.
- **Backups** of the above.
- The **Angular portal** (administrative and configuration UI).

FHIRBridge is deployed in a **single-organization** model via docker-compose, with Azure/Kubernetes as a candidate target. The assessment covers the current docker-compose deployment and flags infrastructure-dependent risks for the target platform.

**Out of scope** — the internal security posture of the connected EHR systems themselves (Epic, MEDITECH, etc.), which are the responsibility of their operators and are governed by Business Associate Agreements and interconnection agreements. The de-identification correctness of source data prior to ingestion is also the source system's responsibility.

## 3. Methodology

This assessment follows **NIST SP 800-30 Rev. 1, *Guide for Conducting Risk Assessments***, and the **HHS Office for Civil Rights (OCR) "Guidance on Risk Analysis Requirements under the HIPAA Security Rule."** The process is:

1. **Scope the assessment** and identify all ePHI (Section 2; `asset-inventory.md`).
2. **Identify and document threat sources and vulnerabilities** — adversarial (external attacker, malicious insider), accidental (misconfiguration, human error), structural (component/dependency failure), and environmental (physical/facility). See `threat-vulnerability-register.md`.
3. **Assess current security measures** — the technical safeguards implemented in the FHIRBridge codebase and platform are cited per threat.
4. **Determine likelihood** of each threat exploiting a vulnerability (Low / Medium / High).
5. **Determine impact** to confidentiality, integrity, and availability of ePHI (Low / Medium / High).
6. **Determine risk level** as a function of likelihood × impact, expressed as **inherent risk** (before controls) and **residual risk** (after the controls currently in place).
7. **Recommend and track treatment** for every residual risk rated Medium or higher. See `risk-treatment-plan.md`.

### Risk rating scale

Risk level is derived from the likelihood × impact matrix below (NIST SP 800-30 style, qualitative):

| Likelihood ↓ / Impact → | **Low** | **Medium** | **High** |
|---|---|---|---|
| **High** | Medium | High | High |
| **Medium** | Low | Medium | High |
| **Low** | Low | Low | Medium |

- **Low** — acceptable; monitor.
- **Medium** — requires a documented treatment action, owner, and target date.
- **High** — requires prioritized treatment; escalate to the Security Official.

## 4. Document set

| File | Contents |
|---|---|
| `README.md` (this file) | Purpose, scope, methodology, cadence, sign-off. |
| `asset-inventory.md` | Inventory of information assets and systems that handle ePHI, with classification and ownership. |
| `threat-vulnerability-register.md` | Threat/vulnerability register with existing controls, likelihood, impact, inherent and residual risk, and treatment. |
| `risk-treatment-plan.md` | Action plan for every residual risk rated Medium or higher, with owner, target date, and status. |

## 5. Review cadence

Per §164.308(a)(1)(ii)(A) and OCR guidance, risk analysis is **not a one-time activity**. This document set must be reviewed and updated:

- **At least annually.**
- **On any major change** to the platform, including: new source or destination connector types, changes to the authentication or authorization model, changes to encryption or key management, migration to a new hosting platform (e.g., docker-compose → Azure/Kubernetes), onboarding a new subprocessor that handles ePHI, or a security incident that reveals a new threat.

Each review must record the reviewer, date, and summary of changes in the sign-off block below.

## 6. Related compliance documentation

- `../hipaa-soc2-control-matrix.md` — maps HIPAA §164.312 technical safeguards and SOC 2 TSC to implemented FHIRBridge features (technical-safeguards evidence baseline).
- `../backup-disaster-recovery.md` — backup/DR runbook (treatment for availability and contingency-planning risks).
- `../security-policies-templates.md` — administrative-safeguard policy templates (§164.308).

---

## 7. Sign-off

This risk analysis has been reviewed and approved as an accurate and thorough assessment of risks to ePHI held by the organization.

| Role | Name | Signature | Date |
|---|---|---|---|
| **Security Official** (HIPAA §164.308(a)(2)) | [ORGANIZATION TO COMPLETE] | [ORGANIZATION TO COMPLETE] | [ORGANIZATION TO COMPLETE] |
| **Privacy Officer** | [ORGANIZATION TO COMPLETE] | [ORGANIZATION TO COMPLETE] | [ORGANIZATION TO COMPLETE] |
| **Executive Sponsor / Owner** | [ORGANIZATION TO COMPLETE] | [ORGANIZATION TO COMPLETE] | [ORGANIZATION TO COMPLETE] |

### Review history

| Version | Date | Reviewer | Summary of changes |
|---|---|---|---|
| 1.0 | [ORGANIZATION TO COMPLETE] | [ORGANIZATION TO COMPLETE] | Initial risk analysis. |
