# RFP & Vendor Selection — FHIRBridge Penetration Test

Guidance and a ready-to-issue Request for Proposal (RFP) for engaging a qualified third-party firm to penetration test FHIRBridge. Complete the `[TO COMPLETE]` fields, issue to shortlisted firms, score responses in the [comparison table](#vendor-comparison-table-to-complete), and retain the selection record as audit evidence.

---

## 1. What a qualified firm must have

### Required certifications (firm and/or lead testers)
- **OSCP** (Offensive Security Certified Professional) — hands-on exploitation competence. Prefer at least one OSCP-holding tester on the engagement.
- **CREST** — firm-level accreditation (CREST-registered/certified pentester, e.g. CRT/CCT) demonstrating vetted methodology and quality.
- Nice-to-have: **GWAPT / GPEN / GXPN** (SANS/GIAC), **OSWE** (advanced web exploitation), **CEH** (baseline only, not sufficient alone).

### Required experience & qualifications
- **Healthcare / ePHI experience** — prior pen tests of HIPAA-regulated systems handling PHI; understands HIPAA §164.308(a)(8) evaluation context.
- **API + modern web app depth** — REST API, JWT/OAuth2/OIDC, SMART-on-FHIR / SMART launch, SSO (Entra/Azure AD), and SPA (Angular) testing.
- **Cloud / container experience** — Azure, Azure Key Vault, docker-compose deployments.
- **FHIR/HL7 familiarity** — a strong differentiator given the data model and ingestion paths.
- Willingness to sign an **NDA** and, if any real ePHI could be reached, a **BAA**.
- Professional **liability / cyber insurance** at an acceptable level.
- Testers with **clean background checks** (relevant for ePHI environments).

### Methodology
- Follows recognized standards: **OWASP ASVS**, **OWASP Top 10**, **OWASP API Security Top 10**, **PTES**, **NIST SP 800-115**.
- Combines automated tooling with **manual, objective-based** testing (not a scan-and-report shop).
- Provides a clear ROE process and works to the scope in [`scope-and-rules-of-engagement.md`](./scope-and-rules-of-engagement.md) and coverage in [`test-plan-checklist.md`](./test-plan-checklist.md).

---

## 2. Required deliverables (must be in the SOW)

1. **Executive summary** — business-level risk narrative, suitable for leadership and (redacted) for customers/auditors.
2. **Detailed technical findings** — each with: description, affected component/endpoint, **CVSS v3.1 (or v4.0) vector + score**, severity, reproduction steps, evidence, and specific remediation guidance.
3. **Coverage mapping** — findings and tested areas mapped back to the OWASP ASVS / API Top 10 checklist in this repo.
4. **Overall risk rating** and prioritized remediation roadmap.
5. **Included retest** — verification of remediated findings within a defined window (e.g., within 30–90 days) at **no additional cost**, with a delta report.
6. **Attestation / letter of engagement** — a signed attestation summarizing scope, dates, and outcome, shareable with customers and SOC 2 auditors as evidence of the annual test.
7. **Debrief call** — walkthrough with engineering.
8. Secure delivery of the report (encrypted); secure destruction of engagement data afterward.

---

## 3. RFP template (issue to vendors)

> Copy the block below into your RFP communication and complete the `[TO COMPLETE]` fields.

---

**Request for Proposal — Third-Party Penetration Test**

**Issuing organization:** [TO COMPLETE]
**Contact:** [TO COMPLETE — name, email]
**RFP issue date / response due date:** [TO COMPLETE]
**Confidentiality:** Responses are confidential; an NDA is available on request and required before scope details are shared.

**1. About the system.** FHIRBridge is a healthcare integration platform (.NET 9 ASP.NET Core API + background Worker, Angular portal, SQL Server, Azure Key Vault, docker-compose) that ingests and processes **ePHI** from EHR systems via **FHIR/HL7**. Authentication uses JWT + Microsoft Entra SSO, RBAC, TOTP MFA, account lockout, and rate limiting. Public surface includes login/refresh, forgot/reset-password, SSO, HMAC-signed webhook ingestion, JWKS, and OAuth SMART launch.

**2. Engagement objective.** Independent, manual, objective-based penetration test of the application, API, and supporting infrastructure against OWASP ASVS, OWASP Top 10, and OWASP API Security Top 10, satisfying HIPAA §164.308(a)(8) evaluation and SOC 2 CC4.1 needs. Testing will be performed against a **staging environment seeded with synthetic data** (no production ePHI).

**3. Scope.** Approximate scope: 1 REST API surface (`/api/v1`), 1 Angular portal, and the listed public/anonymous endpoints, plus supporting infra as agreed. Authenticated + unauthenticated + multi-tenant testing. Detailed scope and ROE provided under NDA.

**4. Please provide in your response:**
- Firm overview, relevant **healthcare/ePHI** engagements (anonymized references welcome).
- **Certifications** held by the firm and by the specific testers who will staff this engagement (OSCP, CREST, GIAC, etc.).
- **Methodology** and standards followed; sample (redacted) report.
- Team composition and named lead tester(s).
- **Testing approach** for: JWT/OAuth/SSO, multi-tenant authorization/IDOR, webhook HMAC, SSRF, and FHIR/HL7 ingestion.
- **Timeline / earliest availability** and estimated duration (testing + reporting).
- **Retest** terms (included? window? cost?).
- Deliverables (confirm all items in §2 above).
- **Pricing** — fixed-fee preferred; state assumptions and any change-order terms.
- Data-handling & destruction practices; willingness to sign **NDA/BAA**; insurance coverage.
- Any subcontracting or offshore-testing disclosures.

**5. Evaluation criteria (weighting):** technical competence & certs [TO COMPLETE %] · healthcare experience [%] · methodology & deliverables [%] · retest terms [%] · timeline [%] · price [%].

**6. Submission:** email response to [TO COMPLETE] by [TO COMPLETE].

---

## 4. Vendor comparison table [TO COMPLETE]

Score each 1–5 (5 = best); weight per your evaluation criteria; retain the completed table as the selection record.

| Criterion | Weight | Vendor A [TO COMPLETE] | Vendor B [TO COMPLETE] | Vendor C [TO COMPLETE] |
|---|---|---|---|---|
| Certifications (OSCP / CREST / GIAC) | [%] | | | |
| Healthcare / ePHI experience | [%] | | | |
| API + JWT/OAuth/SSO / SMART-on-FHIR depth | [%] | | | |
| Cloud / Azure / container experience | [%] | | | |
| Methodology (OWASP ASVS / API Top 10, manual) | [%] | | | |
| Deliverables quality (sample report) | [%] | | | |
| **Retest included** | [%] | | | |
| Data handling / NDA / BAA / insurance | [%] | | | |
| Timeline / availability | [%] | | | |
| Price | [%] | | | |
| References | [%] | | | |
| **Weighted total** | 100% | | | |

**Recommended vendor:** [TO COMPLETE] — **Rationale:** [TO COMPLETE]
**Approved by:** [TO ASSIGN] · **Date:** __________

---

## 5. Selection checklist

- [ ] NDA signed before sharing detailed scope/ROE.
- [ ] BAA in place if any real ePHI could be reachable.
- [ ] Chosen firm's staffed testers hold required certs (verified, not just firm-level).
- [ ] SOW includes all §2 deliverables **and** an included retest.
- [ ] ROE ([`scope-and-rules-of-engagement.md`](./scope-and-rules-of-engagement.md)) signed by both parties.
- [ ] Staging environment with synthetic data provisioned and confirmed.
- [ ] Test accounts (multiple roles, multiple tenants) provisioned.
- [ ] Report and attestation retained as compliance evidence; findings loaded into [`remediation-tracker.md`](./remediation-tracker.md).
