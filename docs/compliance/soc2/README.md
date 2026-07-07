# SOC 2 Audit-Readiness Package — FHIRBridge

This folder is the working package for taking the FHIRBridge platform through a **SOC 2** examination. It explains what SOC 2 is, what it will cost in time and effort, and the sequence of steps to get from "we have technical controls" to "we hold a clean SOC 2 report we can hand to customers."

> **Scope reminder.** SOC 2 is an attestation about an *organization's* controls, not just a codebase. FHIRBridge's engineering controls (encryption, RBAC, hash-chained audit log, CI security scanning, etc.) are strong evidence, but a SOC 2 report also requires **organizational** controls that live outside the repo — HR onboarding/offboarding, vendor management, a formal risk assessment, management/board oversight, and a documented change-management process. This package tracks both.

---

## 1. What SOC 2 is

**SOC 2** (System and Organization Controls 2) is an attestation examination performed by an **independent, licensed CPA firm** under the AICPA's attestation standards (SSAE 18, AT-C 105/205). The auditor issues an opinion on whether the service organization's controls meet the **Trust Services Criteria (TSC)**.

SOC 2 is *not* a certification you pass/fail — it is a **report** (typically distributed under NDA) that your customers' security and procurement teams read to decide whether to trust you with their data. In healthcare data integration, a SOC 2 report is frequently requested alongside a signed BAA.

### Trust Services Criteria (TSC)

There are five Trust Services Categories. You choose which to include in scope:

| Category | In scope for FHIRBridge? | Why |
|---|---|---|
| **Security (Common Criteria, CC1–CC9)** | **Required** | The mandatory baseline. Every SOC 2 report includes it. Covers control environment, communication, risk assessment, monitoring, control activities, logical/physical access, system operations, change management, and risk mitigation. |
| **Availability (A1)** | **Recommended** | FHIRBridge is a data-integration service other systems depend on; customers care about uptime, backup, and DR. We already have a backup/DR runbook and health checks. |
| **Confidentiality (C1)** | **Recommended** | We handle ePHI and other confidential data; we already implement encryption, de-identification, PHI log masking, and retention/disposal. This category maps cleanly to controls we have. |
| **Processing Integrity (PI1)** | Optional | Relevant to "is data processed completely, accurately, timely" — arguably applicable to a transformation pipeline, but adds scope and evidence burden. Defer to a later cycle unless a customer demands it. |
| **Privacy (P1–P8)** | Optional | Concerns collection/use/retention/disposal of **personal information** per notice and consent. HIPAA already covers most of our privacy obligations as a Business Associate; adding the Privacy category is a larger organizational lift. Defer. |

**Recommended scope for the first FHIRBridge report: Security + Availability + Confidentiality.**

---

## 2. Type I vs. Type II

| | **Type I** | **Type II** |
|---|---|---|
| What the auditor opines on | Whether controls are **suitably designed** and **in place** | Whether controls are suitably designed **and operating effectively** |
| Point vs. period | A **point in time** (a single "as of" date) | A **period of time** (an **observation window**, typically 3–12 months) |
| Evidence | Control descriptions, config snapshots, policy docs — proof the control *exists* | Samples pulled across the whole window — proof the control *ran, every time, over months* |
| Effort / cost | Lower | Higher (continuous evidence collection) |
| Customer value | Modest — "they designed it right" | High — "they actually did it, consistently"; this is what most customers ultimately want |

**Type I is a milestone, not the destination.** It proves the design is sound and gets a report into customers' hands quickly. Type II proves the controls actually operate day-to-day and is the report enterprise customers expect for renewal.

---

## 3. Recommended path

```
  Readiness Assessment  →  Remediate gaps  →  Type I examination  →  Observation window  →  Type II examination
   (2-6 weeks)              (1-3 months)        (2-4 weeks)            (3-12 months, we                (3-6 weeks)
                                                                       operate + collect evidence)
```

1. **Readiness assessment** — internal (or auditor-led) gap analysis across the TSC. Output: the list of design gaps to close. See `readiness-assessment.md`.
2. **Remediate gaps** — mostly organizational: adopt/ratify policies, stand up vendor management, run a formal risk assessment, establish access reviews and a change-management process, assign owners. Technical gaps are comparatively small.
3. **Type I examination** — auditor validates design at a point in time. Gets a report out fast.
4. **Observation window** — the controls simply *run* for the chosen period (start with 6 months as a pragmatic first Type II window) while you collect evidence continuously.
5. **Type II examination** — auditor samples evidence across the window and issues the operating-effectiveness opinion.

### Realistic timeline (from a standing start)

| Phase | Duration | Notes |
|---|---|---|
| Readiness assessment | 2–6 weeks | Faster because technical controls already exist and are documented. |
| Gap remediation | 1–3 months | Dominated by organizational controls (HR, vendor mgmt, risk assessment, oversight cadence). |
| Auditor selection & scoping | 2–4 weeks (parallelizable) | RFP, references, engagement letter. See `auditor-rfp-and-selection.md`. |
| Type I examination | 2–4 weeks | Report issued shortly after fieldwork. |
| Type II observation window | **6 months** (3 min, 12 max) | Controls run; evidence accrues. Start the clock the day controls are demonstrably operating. |
| Type II examination | 3–6 weeks | Fieldwork + report. |
| **Total to first Type II report** | **~9–14 months** | Type I report available at roughly month 3–4. |

---

## 4. Files in this package

| File | Purpose |
|---|---|
| `README.md` | This overview: TSC, Type I vs II, the path and timeline. |
| `readiness-assessment.md` | Gap analysis across CC1–CC9, A1, C1 — what's in place, what's missing, readiness status. |
| `evidence-matrix.md` | The auditor's PBC ("provided by client") request list: control → TSC ref → evidence artifact → location → owner. |
| `audit-prep-checklist.md` | Step-by-step run-of-show from scoping through Type I → Type II. |
| `auditor-rfp-and-selection.md` | How to pick a licensed CPA firm: RFP template, questions, cost/timeline, comparison table. |

## 5. Related documents

- `../hipaa-soc2-control-matrix.md` — technical controls mapped to HIPAA §164.312 and SOC 2 Common Criteria (the technical-evidence baseline).
- `../policies/` — the adopted HIPAA/security policy manual (POL-000 … POL-013), most of which double as SOC 2 policy evidence.
- `../risk-assessment/` — asset inventory and risk-assessment workspace.
- `../backup-disaster-recovery.md` — backup/DR runbook (Availability evidence).
- `../baa/` — Business Associate Agreement register (vendor/subservice-organization evidence).
