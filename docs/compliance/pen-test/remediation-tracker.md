# Penetration Test Findings & Remediation Tracker — FHIRBridge

The single source of truth for tracking penetration-test (and vulnerability-scan) findings from discovery through remediation and retest. Retain the completed tracker as audit evidence for HIPAA §164.308(a)(8) and SOC 2 CC4.1 / CC7.1.

**Instructions:** load every Fail from [`test-plan-checklist.md`](./test-plan-checklist.md) and every finding from the vendor report into the table. Assign a severity (with CVSS vector), an owner, and a remediation SLA date per the [SLA table](#remediation-sla-by-severity). Update **Status** as work progresses. Do not close a finding until it is **Retested — Closed**.

---

## Remediation SLA by severity

SLA clock starts at finding **acceptance** (validated as a true positive). Adjust to your risk appetite and document the rationale if changed.

| Severity | CVSS v3.1 range | Remediation SLA (target) | Retest | Notes |
|---|---|---|---|---|
| **Critical** | 9.0 – 10.0 | **7 days** | Mandatory | Consider emergency change / temporary mitigation immediately; notify leadership. |
| **High** | 7.0 – 8.9 | **30 days** | Mandatory | Prioritize in the next sprint. |
| **Medium** | 4.0 – 6.9 | **90 days** | Recommended | Schedule into normal backlog. |
| **Low** | 0.1 – 3.9 | **180 days** | Optional | Fix or formally risk-accept. |
| **Informational** | 0.0 | Best effort | N/A | Hardening / defense-in-depth. |

**Risk acceptance:** any finding not remediated within SLA requires a documented, time-boxed risk acceptance approved by [TO ASSIGN — Security/Privacy Officer], recorded in the row's Notes.

**Status values:** `Open` · `In Progress` · `Fixed – Awaiting Retest` · `Retested – Closed` · `Risk Accepted` · `False Positive` · `Duplicate`.

---

## Findings tracker

| ID | Finding | Severity (CVSS score + vector) | Affected component | Status | Owner | Remediation | Remediation due (per SLA) | Retest date | Notes |
|---|---|---|---|---|---|---|---|---|---|
| PT-001 | _[finding title]_ | _[e.g., High — 8.1 / CVSS:3.1/AV:N/AC:L/PR:L/UI:N/S:U/C:H/I:H/A:N]_ | _[e.g., `/api/v1/tenants/{id}/...`]_ | Open | [TO ASSIGN] | _[fix description]_ | _[date]_ | _[date]_ | |
| PT-002 | | | | Open | [TO ASSIGN] | | | | |
| PT-003 | | | | Open | [TO ASSIGN] | | | | |
| PT-004 | | | | Open | [TO ASSIGN] | | | | |
| PT-005 | | | | Open | [TO ASSIGN] | | | | |

_Add rows as needed. Keep IDs stable (PT-NNN) so retests and reports can reference them._

---

## Summary dashboard (update per engagement)

| Severity | Total | Open / In Progress | Fixed – Awaiting Retest | Closed | Risk Accepted |
|---|---|---|---|---|---|
| Critical | 0 | 0 | 0 | 0 | 0 |
| High | 0 | 0 | 0 | 0 | 0 |
| Medium | 0 | 0 | 0 | 0 | 0 |
| Low | 0 | 0 | 0 | 0 | 0 |
| Informational | 0 | 0 | 0 | 0 | 0 |
| **Total** | **0** | **0** | **0** | **0** | **0** |

---

## Engagement metadata

| Field | Value |
|---|---|
| Engagement / test date | [TO COMPLETE] |
| Testing firm | [TO COMPLETE] |
| Environment tested | Staging (synthetic data) |
| Report received date | [TO COMPLETE] |
| Retest completed date | [TO COMPLETE] |
| Overall risk rating (from report) | [TO COMPLETE] |
| Remediation program owner | [TO ASSIGN] |
| Approved / accepted by | [TO ASSIGN] |

---

## Process notes

- **Every** Critical or High finding must be remediated **and retested** before the finding is closed; a code-only fix without retest confirmation stays `Fixed – Awaiting Retest`.
- Where a finding maps to a code change, link the PR/commit in Notes for traceability.
- Recurring finding types should feed back into the SDLC (secure-coding guidance, CI rules, ASVS gaps) to prevent regression.
- Retain the report, this tracker, and retest evidence for the audit retention period (align with the 7-year HIPAA documentation retention where applicable).
