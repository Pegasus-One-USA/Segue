# POL-014 — Breach Notification Procedure

| | |
|---|---|
| **Policy ID** | POL-014 |
| **HIPAA citation** | §164.400–414 (Breach Notification Rule); §164.410 (Business Associate obligations); §164.402 (definitions) |
| **Related** | [POL-006 Security Incident Procedures](./06-security-incident-procedures.md), [POL-001 Security Management Process](./01-security-management-process.md), [BAA program](../baa/README.md) |
| **Status** | Required |
| **Owner** | [ROLE — Security Official] |
| **Review cadence** | Annual and after any reportable incident |

> This is an operational runbook that expands the breach-handling step of POL-006. It states what FHIRBridge does **when a security incident is determined to involve unsecured PHI**.

## 1. Purpose

To ensure that any acquisition, access, use, or disclosure of unsecured Protected Health Information (PHI) not permitted by the HIPAA Privacy Rule is assessed, and — where it constitutes a **breach** — reported within the timeframes and to the parties the law requires.

## 2. Scope

All PHI processed by FHIRBridge in any form (in the SQL database, audit store, logs, backups, blob/object destinations, or in transit to/from EHR sources). Applies to the workforce, contractors, and is flowed down to subprocessors via BAA.

## 3. FHIRBridge's role and reporting direction

FHIRBridge operates as a **Business Associate**. Under **§164.410**, when a breach of unsecured PHI is discovered, FHIRBridge must notify the affected **Covered Entity** (its customer) **without unreasonable delay and no later than 60 calendar days** from discovery. The Covered Entity is generally responsible for notifying individuals, HHS, and (where applicable) the media — but the BAA may contractually assign some of these duties to FHIRBridge, so **check the governing BAA** ([../baa/](../baa/README.md)).

A breach is **"discovered"** on the first day it is known, or by exercising reasonable diligence would have been known, to anyone in the workforce (other than the person who committed it).

## 4. Step 1 — Is it a breach? (Risk assessment, §164.402)

An impermissible use/disclosure of PHI is **presumed to be a breach** unless FHIRBridge demonstrates a **low probability that PHI was compromised** through a documented four-factor risk assessment:

1. **Nature & extent of the PHI** — identifiers involved and likelihood of re-identification (note: FHIRBridge's Safe Harbor/k-anonymity de-identification may make some data not PHI).
2. **Who used it / to whom disclosed** — was the recipient another obligated entity?
3. **Was the PHI actually acquired or viewed**, or merely exposed?
4. **Extent to which risk has been mitigated** (e.g., recovery, attestation of destruction).

Document the assessment and conclusion in the incident record. **Exceptions** (not a breach): unintentional good-faith access by workforce within scope; inadvertent disclosure between authorized persons; disclosure where the recipient could not reasonably have retained the information.

**Encryption safe harbor:** PHI that was **encrypted to NIST standards** (e.g., TDE at rest, TLS in transit) and whose keys were not compromised is **"secured"** and its exposure is **not** a reportable breach. Record whether the affected data met this bar.

## 5. Step 2 — Notify the Covered Entity (§164.410)

If a breach is confirmed, notify each affected Covered Entity **without unreasonable delay, ≤ 60 days from discovery** (sooner if the BAA requires). The notification must include, to the extent known:

- Identification of each individual whose PHI was involved (or the identifiers).
- A description of what happened, date of breach, date of discovery.
- The types of PHI involved.
- Any steps taken to investigate, mitigate, and protect against further breaches.
- A point of contact.

Use the **breach notification log** in §8 to track. Provide supplemental information as the investigation continues.

## 6. Step 3 — Downstream (if a subprocessor caused it)

If the breach originated with a FHIRBridge subprocessor, the subprocessor must notify FHIRBridge per its flow-down BAA; FHIRBridge then performs steps 4–5 toward the affected Covered Entities. Track via [../baa/subprocessor-register.md](../baa/subprocessor-register.md).

## 7. Timeline summary

| Party | Obligation | Deadline |
|---|---|---|
| Subprocessor → FHIRBridge | Report breach | Per flow-down BAA (default ≤ 60 days from discovery; negotiate shorter) |
| **FHIRBridge (BA) → Covered Entity** | **Report breach (§164.410)** | **Without unreasonable delay, ≤ 60 calendar days from discovery** |
| Covered Entity → Individuals | Notify (§164.404) | ≤ 60 days from discovery |
| Covered Entity → HHS OCR | Notify (§164.408) | ≤ 60 days (≥500 affected); annually (<500) |
| Covered Entity → Media | Notify (§164.406) | ≤ 60 days if >500 in a state/jurisdiction |

*State breach-notification laws may impose shorter deadlines or additional recipients — [ORGANIZATION TO COMPLETE: applicable state law review].*

## 8. Breach notification log (record every determination, breach or not)

| ID | Discovery date | Description | PHI involved | Encrypted? | 4-factor outcome | Breach? | CE notified (date) | Owner |
|----|---------------|-------------|--------------|-----------|------------------|---------|--------------------|-------|
| _[example]_ | | | | | | | | |

Retain breach documentation and the risk-assessment for **6 years** (§164.414 burden of proof; POL-013 retention).

## 9. Roles & responsibilities

- **[ROLE — Security Official]** — owns the process, makes the breach determination, approves notifications.
- **[ROLE — Privacy Official]** — reviews Privacy Rule implications and individual-rights impact.
- **[ROLE — Legal/Counsel]** — reviews notification content and state-law obligations.
- **Workforce** — must report suspected incidents immediately per POL-006.

## 10. Enforcement

Failure to report a known incident is subject to the sanction policy in [POL-001 §3.3](./01-security-management-process.md).

## Revision history

| Version | Date | Author | Change |
|---------|------|--------|--------|
| 1.0 | [DATE] | [AUTHOR] | Initial version |

## Approval

| Role | Name | Signature | Date |
|------|------|-----------|------|
| Security Official | [ORGANIZATION TO COMPLETE] | | |
| Privacy Official | [ORGANIZATION TO COMPLETE] | | |
