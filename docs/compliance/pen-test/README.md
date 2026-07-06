# Penetration Testing & Vulnerability Management Program — FHIRBridge

This folder is the program of record for **third-party penetration testing** and **vulnerability management** of the FHIRBridge platform — a .NET 9 ASP.NET Core API (`/api/v1`) plus a background Worker, an Angular portal, SQL Server, Azure Key Vault, and a docker-compose deployment that ingests and processes **ePHI** from EHR systems over FHIR/HL7.

> **Scope statement.** The FHIRBridge codebase implements **technical safeguards** (authentication, RBAC, encryption, audit logging, HMAC-verified webhooks, rate limiting, security headers). Penetration testing is an **organizational control** that validates those safeguards against a live adversary. This program does not replace the automated scanning already running in CI — it complements it (see [Automated scanning vs. manual pen testing](#automated-scanning-vs-manual-penetration-testing) below).

---

## Why penetration testing is required

| Driver | Requirement | What it obligates |
|---|---|---|
| **HIPAA Security Rule** | **§164.308(a)(8) — Evaluation** | Perform a periodic **technical and non-technical evaluation** that establishes the extent to which security controls meet the requirements of the Security Rule, in response to environmental or operational changes. Penetration testing is the industry-standard technical component of that evaluation for a system handling ePHI. |
| **HIPAA Security Rule** | **§164.308(a)(1)(ii)(A) — Risk Analysis** | An accurate and thorough assessment of potential risks and vulnerabilities. Pen-test findings feed directly into the risk register. |
| **SOC 2** | **CC4.1 — Monitoring of controls** | The entity selects, develops, and performs **ongoing and/or separate evaluations** to ascertain whether the components of internal control are present and functioning. An independent pen test is a "separate evaluation." |
| **SOC 2** | **CC7.1 — Vulnerability detection** | The entity uses detection and monitoring procedures to identify (a) changes to configurations that introduce vulnerabilities and (b) susceptibilities to newly discovered vulnerabilities. |
| **Contractual / customer** | BAA and enterprise security reviews | Covered entities and enterprise customers routinely require evidence of an annual third-party pen test before signing a BAA or completing vendor security due diligence. |
| **Good practice** | OWASP / NIST | NIST SP 800-115 and OWASP recommend regular testing of internet-facing applications processing sensitive data. |

Neither HIPAA nor SOC 2 prescribes a specific tool or a fixed frequency, but both require a **repeatable, documented evaluation process with tracked remediation** — which is exactly what this folder provides.

---

## Cadence

| Trigger | Action |
|---|---|
| **At least annually** | Full-scope third-party manual penetration test of the application, API, and supporting infrastructure. |
| **After any significant change** | Targeted pen test / re-test when there is a material change to the attack surface — e.g., new authentication flow or SSO integration, new public/anonymous endpoint, new EHR source connector or destination writer, a change to the webhook HMAC scheme, a major dependency/framework upgrade, or a change of hosting/deployment topology. |
| **Continuously (automated)** | CI security scanning on every pull request and on a schedule (see below). |
| **On every Critical/High finding** | Remediate per the SLA in [`remediation-tracker.md`](./remediation-tracker.md) and schedule a **retest** to confirm closure. |

"Significant change" is interpreted per HIPAA §164.308(a)(8) ("in response to environmental or operational changes affecting the security of ePHI").

---

## Automated scanning vs. manual penetration testing

These are complementary layers, not substitutes. Automated scanning gives breadth and continuous coverage; manual testing gives depth, business-logic insight, and exploit chaining that scanners cannot reach.

| | Automated scanning (already in CI) | Third-party manual pen test (this program) |
|---|---|---|
| **What runs today** | Dependency/SCA scanning, **CodeQL** SAST, **gitleaks** secret scanning (and any DAST configured in the pipeline). | Human-led, objective-based testing by a qualified external firm. |
| **Cadence** | Every PR + scheduled runs. | At least annually + after significant change. |
| **Finds** | Known-CVE dependencies, common code-level sink/source flaws, committed secrets, misconfigurations matching known rules. | Broken authorization / IDOR, business-logic abuse, auth/session flaws, JWT tampering, MFA bypass, SSRF, chained exploits, multi-tenant isolation breaks — issues that require reasoning about the application's intent. |
| **Misses** | Anything requiring an understanding of *intended* behavior (e.g., "can user A read tenant B's Patient?"). Low signal-to-noise on logic flaws. | Nothing structurally, but it is a point-in-time snapshot; it does not run continuously. |
| **Independence** | Internal, developer-owned. | Independent third party — required for SOC 2 "separate evaluation" and customer assurance. |

**Bottom line:** CI scanning is necessary and running, but it does **not** satisfy the §164.308(a)(8) evaluation or SOC 2 CC4.1 on its own. An independent, manual, annual pen test is required.

---

## Documents in this folder

- **[scope-and-rules-of-engagement.md](./scope-and-rules-of-engagement.md)** — The Rules of Engagement (ROE): in/out-of-scope targets, test window, authorized and prohibited techniques, ePHI data-handling rules, emergency contacts, and the authorization ("get-out-of-jail") sign-off block. **Testing is performed against a staging environment seeded with SYNTHETIC data — never production ePHI.**
- **[test-plan-checklist.md](./test-plan-checklist.md)** — Concrete test checklist mapped to OWASP ASVS, OWASP Top 10, and the OWASP API Security Top 10, tailored to FHIRBridge (authn, authz/IDOR, injection, webhook HMAC, SSRF via source `BaseUrl`, secrets, headers/TLS, sessions, rate limiting, dependency CVEs).
- **[rfp-vendor-selection.md](./rfp-vendor-selection.md)** — Guidance and an RFP template for selecting a qualified pen-test firm (required certifications, healthcare experience, deliverables, included retest), plus a vendor comparison table to complete.
- **[remediation-tracker.md](./remediation-tracker.md)** — Findings and remediation tracker with a severity/CVSS SLA table and retest tracking.

## How to use these documents

1. **Before the engagement:** complete the placeholders in the ROE (`[TO CONFIRM]`, `[TO ASSIGN]`, `[ORGANIZATION TO SIGN]`), stand up the staging environment with synthetic data, and issue the RFP.
2. **During the engagement:** give the tester the ROE and the test-plan checklist as the agreed baseline of coverage.
3. **After the engagement:** load every finding into the remediation tracker, assign owners, remediate to SLA, retest, and retain the report and the closed tracker as audit evidence.

> Placeholders `[TO CONFIRM]`, `[TO ASSIGN]`, `[ORGANIZATION TO SIGN]`, and `[TO COMPLETE]` mark items the organization must supply — they are intentionally left blank.
