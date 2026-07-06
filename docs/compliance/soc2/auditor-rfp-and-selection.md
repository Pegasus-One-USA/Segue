# SOC 2 Auditor Selection & RFP — FHIRBridge

**Purpose.** How to select the independent firm that will perform the FHIRBridge SOC 2 examination, including an RFP template, questions to ask, cost/timeline expectations, and a comparison table to complete.

---

## 1. Who can issue a SOC 2 report

A SOC 2 report can **only** be issued by a **licensed CPA firm** (or an individual CPA) performing the examination under the AICPA attestation standards (SSAE 18 / AT-C sections). Key selection constraints:

- Must be a **CPA firm in good standing**, ideally **AICPA peer-reviewed**.
- Must be **independent** of FHIRBridge (no conflict — e.g., they can't both build your controls *and* audit them; readiness help is fine if a separate team).
- Look for **healthcare / ePHI experience** and familiarity with **Azure**-hosted, .NET SaaS-style platforms.

> **Two-vendor pattern (common & recommended).** Many companies use a **compliance-automation platform** (Vanta, Drata, Secureframe, OneTrust) to collect evidence continuously, and a **separate CPA audit firm** to perform the examination. Some audit firms partner with specific platforms — factor that into selection.

---

## 2. Selection process

1. **Shortlist 3–4 firms** (see categories below).
2. **Issue the RFP** (template in §4).
3. **Reference checks** — ask for references from healthcare/ePHI clients on Azure.
4. **Scoping call** — confirm TSC scope (Security + Availability + Confidentiality), subservice (Azure) treatment, and Type I → Type II plan.
5. **Compare** on price, timeline, experience, and platform fit (table in §6).
6. **Engagement letter** — signed scope, fees, deliverables, timeline.

### Firm categories

| Category | Examples (illustrative) | Fit for FHIRBridge |
|---|---|---|
| Tech/startup-focused audit firms (often platform-integrated) | Prescient Assurance, Johanson Group, Sensiba, A-LIGN, Insight Assurance, Barr Advisory | Fast, cost-effective, familiar with SaaS + automation platforms |
| Mid-market / healthcare-specialist firms | Coalfire, Schellman, Dansa D'Arata, KirkpatrickPrice | Deeper healthcare/HITRUST crossover; higher cost |
| Big-4 / national | Deloitte, PwC, EY, KPMG | Brand weight for large enterprise deals; highest cost, longest lead time — usually overkill for a first report |

*(Names are illustrative examples of the market, not endorsements — verify current licensing/peer-review status.)*

---

## 3. Questions to ask candidate firms

**Credentials & independence**
- Are you a licensed CPA firm, in good standing, and AICPA peer-reviewed? (Ask for the peer-review letter.)
- Who signs the report, and what are their credentials?
- How do you maintain independence if you also offer readiness services?

**Experience**
- How many SOC 2 reports have you issued in the last 12 months?
- Do you have **healthcare / ePHI** clients? **Azure**-hosted .NET platforms?
- Can you provide **references** from comparable clients?

**Scope & method**
- How do you handle **subservice organizations** (Azure) — carve-out vs. inclusive?
- Can you cover **Security + Availability + Confidentiality** in one engagement?
- Do you support a **Type I then Type II** sequence, and how do you price it?

**Process & tooling**
- Do you integrate with **Vanta / Drata / Secureframe / OneTrust**? Which do you prefer?
- What is your **evidence-request (PBC) process** and portal?
- How much **auditor time** will our team need (walkthroughs, interviews)?

**Timeline & deliverables**
- What's the lead time to start, and total fieldwork duration?
- What is the **turnaround** from fieldwork end to final report?
- Do you provide a **bridge letter** between report periods?
- What does a **management response** to exceptions look like?

**Cost**
- Fixed fee or T&M? What's included/excluded (e.g., readiness, remediation retest)?
- Multi-year pricing for the recurring annual Type II?

---

## 4. RFP template

> Copy this into a document and send to shortlisted firms. Fill bracketed items.

```
REQUEST FOR PROPOSAL — SOC 2 EXAMINATION

1. About us
   - Company: [ORGANIZATION]
   - Product: FHIRBridge — a .NET 9 healthcare data-integration platform
     ingesting/transforming/routing FHIR/HL7 data (including ePHI) between
     EHR systems and downstream destinations.
   - Hosting: Microsoft Azure (Key Vault, SQL Server w/ TDE), docker-compose deployment.
   - Role: Business Associate (and Covered Entity in some deployments).
   - Headcount: [N] · Environments: production [+ staging/dev, out of scope]

2. Engagement scope requested
   - Report: SOC 2 [Type I then Type II] — see timeline below.
   - Trust Services Criteria: Security (CC1–CC9) + Availability (A1) + Confidentiality (C1).
     Processing Integrity and Privacy are OUT of scope this cycle.
   - Subservice organization: Microsoft Azure — [carve-out preferred; advise].
   - Observation window (Type II): [6 months, dates TBD].

3. Current control posture (summary)
   - Technical controls implemented and documented: PBKDF2 hashing, account lockout,
     TOTP MFA, JWT/Entra SSO, RBAC, hash-chained append-only audit log, PHI log masking,
     Safe Harbor/k-anonymity de-identification, Azure Key Vault, SQL TDE + startup health
     check, HTTPS/HSTS/security headers, HMAC webhook signatures, rate limiting, 7-year
     retention engine, OpenTelemetry + health checks, CI with dependency scan + CodeQL SAST
     + gitleaks + Dependabot.
   - Documentation: HIPAA/SOC 2 control matrix, adopted policy manual, backup/DR runbook,
     risk-assessment workspace, this SOC 2 readiness package.
   - Known organizational gaps in remediation: formal risk assessment, vendor management,
     access reviews, change-management formalization, management oversight cadence.

4. Please provide
   a. Firm credentials (license, peer-review letter, report signer).
   b. Relevant healthcare/Azure/SaaS experience + references.
   c. Proposed approach, subservice treatment, and PBC process.
   d. Compliance-automation platform integrations supported.
   e. Timeline (start lead time, fieldwork duration, report turnaround).
   f. Fixed-fee pricing broken out: Type I, Type II, multi-year; inclusions/exclusions.
   g. Estimated internal time required from our team.

5. Response due: [DATE] · Contact: [NAME/EMAIL]
```

---

## 5. Cost & timeline expectations

Ballpark market ranges (USD; verify with quotes — varies by scope, firm tier, headcount):

| Item | Typical range | Notes |
|---|---|---|
| **Readiness assessment** (if outsourced) | $10k–$25k | Optional; can be done internally with this package. |
| **SOC 2 Type I** | $10k–$30k | Point-in-time design opinion. |
| **SOC 2 Type II** | $20k–$60k+ | Scales with # of criteria, controls, window length, firm tier. |
| **Compliance-automation platform** (annual) | $7k–$50k/yr | Vanta/Drata/Secureframe/OneTrust; optional but high-leverage for Type II. |
| **Penetration test** (prereq evidence) | $5k–$30k | Independent; separate vendor typically. |

**Timeline** (see also `README.md` §3): auditor start lead time 2–4 weeks; Type I fieldwork 2–4 weeks; Type II window **6 months** (3–12); Type II fieldwork + report 3–6 weeks. **~9–14 months** total to a first Type II from a standing start; Type I report at ~month 3–4.

---

## 6. Firm comparison table [TO COMPLETE]

Fill during evaluation:

| Criterion | Firm A: [___] | Firm B: [___] | Firm C: [___] |
|---|---|---|---|
| Licensed CPA firm / peer-reviewed | | | |
| SOC 2 reports issued (last 12 mo) | | | |
| Healthcare / ePHI experience | | | |
| Azure / .NET SaaS experience | | | |
| References provided (quality) | | | |
| Subservice (Azure) treatment approach | | | |
| Covers Security + Availability + Confidentiality | | | |
| Type I → Type II sequencing supported | | | |
| Automation-platform integration (Vanta/Drata/etc.) | | | |
| PBC / evidence portal quality | | | |
| Estimated internal time required | | | |
| Lead time to start | | | |
| Report turnaround after fieldwork | | | |
| Bridge letter provided | | | |
| **Type I fee** | | | |
| **Type II fee** | | | |
| Multi-year pricing | | | |
| Inclusions / exclusions | | | |
| **Overall fit (1–5)** | | | |

---

## 7. Red flags

- Not a licensed CPA firm, or can't produce a **peer-review** letter.
- A **guaranteed "pass"** or a report issued without fieldwork — a legitimate report can contain exceptions; guarantees signal a low-quality mill.
- **Independence conflict** — the same team that builds/operates your controls also audits them.
- No **healthcare** or **Azure** experience and no willingness to learn the subservice model.
- Vague scope or T&M with no cap on a first engagement.

## 8. Decision & next step

- [ ] Shortlist finalized [TO ASSIGN]
- [ ] RFP issued [DATE]
- [ ] Responses scored in §6 table
- [ ] Reference checks complete
- [ ] Firm selected: [___] · Engagement letter signed [DATE]
- [ ] Kick off using `audit-prep-checklist.md`
