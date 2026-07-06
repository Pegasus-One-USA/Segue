# Rules of Engagement (ROE) — FHIRBridge Penetration Test

**Document status:** Template — complete all `[TO CONFIRM]`, `[TO ASSIGN]`, and `[ORGANIZATION TO SIGN]` fields before testing begins.

This Rules of Engagement document governs the authorized penetration test of the FHIRBridge platform. It is binding on both the **Organization** (owner of FHIRBridge) and the **Tester** (the engaged third-party firm). No testing may begin until the [Authorization block](#authorization-get-out-of-jail-card) is signed by both parties.

| Field | Value |
|---|---|
| Organization | [TO CONFIRM — legal entity name] |
| Tester (firm) | [TO CONFIRM — firm + lead tester name] |
| Engagement ID | [TO CONFIRM] |
| ROE version / date | v1.0 — [TO CONFIRM] |

---

## 1. Objectives

- Validate the effectiveness of FHIRBridge technical safeguards against a realistic external and authenticated attacker.
- Assess the public/anonymous attack surface, authentication and authorization controls, multi-tenant/data isolation, and the ePHI-handling data paths (FHIR/HL7 ingestion, EHR source connectors, webhook ingestion).
- Produce prioritized, CVSS-scored, reproducible findings with remediation guidance and a retest.
- Assess against **OWASP Top 10**, **OWASP ASVS**, and the **OWASP API Security Top 10** (see [`test-plan-checklist.md`](./test-plan-checklist.md)).

---

## 2. Environment — test STAGING, not production

> **Mandatory.** All active testing is performed against a dedicated **STAGING** environment that is **seeded with SYNTHETIC data only**. Production ePHI must never be used as a test target, and no test traffic may be directed at production systems that hold real ePHI.

- The Organization will provision a staging environment that mirrors production configuration (auth schemes, RBAC, TLS, headers, rate limits, Key Vault wiring) as closely as possible.
- The staging database is seeded with **synthetic patients / synthetic FHIR & HL7 messages** (e.g., Synthea-generated or equivalent). No record in staging may correspond to a real person.
- If, despite these controls, the Tester encounters data that appears to be **real ePHI**, testing against the affected component **stops immediately** and the Tester follows [§8 ePHI handling](#8-ephi-and-sensitive-data-handling).

---

## 3. In-scope targets

> Confirm every URL/host against the actual staging deployment before sign-off.

| # | Target | Address | Notes |
|---|---|---|---|
| 1 | FHIRBridge API (`/api/v1`) | `https://[TO CONFIRM — staging API host]` | Primary attack surface. |
| 2 | Public/anonymous endpoints | See list below | Unauthenticated surface — priority. |
| 3 | Angular portal (SPA) | `https://[TO CONFIRM — staging portal host]` | Client-side + auth flows. |
| 4 | Webhook ingestion endpoint(s) | `https://[TO CONFIRM]/api/v1/.../webhook*` | HMAC-signed ingestion. |
| 5 | JWKS / discovery endpoints | `https://[TO CONFIRM]/.well-known/*`, JWKS URL | Key exposure / rotation. |
| 6 | Background Worker (indirect) | Reached via ingestion/pipeline triggers | Test via inputs it consumes; no direct host exposure assumed. |
| 7 | Supporting infra (staging only) | `[TO CONFIRM — SQL Server host, container network]` | Only if explicitly listed here; otherwise out of scope. |

**Authenticated testing accounts** (Organization to provision in staging): at least two accounts per tenant with **different RBAC roles/permission levels**, plus accounts in **two separate tenants**, to test authorization and multi-tenant isolation. Credentials delivered out-of-band. [TO ASSIGN — who provisions]

**Known public/anonymous endpoints in scope:**
- `POST /auth/internal/login`
- `POST /auth/refresh`
- `POST /auth/forgot-password` and `POST /auth/reset-password`
- SSO login initiation / callback (Entra)
- Webhook ingestion (HMAC-signed)
- JWKS endpoint
- OAuth / SMART launch endpoints

---

## 4. Out of scope

Unless explicitly added to §3 in writing:

- **Production environment and any system holding real ePHI.**
- Third-party / SaaS providers the Organization does not own: **Microsoft Entra / Azure AD**, **Azure Key Vault** service internals, **Microsoft Azure infrastructure**, and any **external EHR/FHIR servers** used as sources. (Testing *how FHIRBridge talks to* these — e.g., SSRF via source `BaseUrl`, JWKS handling — is in scope; attacking the third-party service itself is not.)
- Physical security, social engineering / phishing of staff, and telephone pretexting — **unless separately authorized** in an appendix.
- Denial-of-service and volumetric/stress testing against any environment (see §6 prohibited actions).
- End-user workstations, CI/CD provider infrastructure, and source-control hosting provider infrastructure.

---

## 5. Test window

| Field | Value |
|---|---|
| Start | [TO CONFIRM — date/time + timezone] |
| End | [TO CONFIRM] |
| Permitted testing hours | [TO CONFIRM — e.g., 24/7 on staging, or business-hours-only] |
| Change freeze on staging | [TO CONFIRM — recommended, to keep results stable] |
| Source IP ranges (Tester) | [TO CONFIRM — allowlist for correlation; do not rely on allowlisting as a control being tested] |

Testing outside this window is **not authorized**.

---

## 6. Authorized techniques and prohibited actions

### Authorized techniques
- Automated and manual vulnerability discovery, web/API scanning, and fuzzing against **in-scope staging** targets.
- Authentication and session attacks: credential brute force to validate lockout/rate-limiting, JWT tampering and `alg` confusion (`none`/HS/RS), refresh-token abuse/replay, MFA bypass attempts.
- Authorization testing: RBAC/permission escalation (horizontal and vertical), **IDOR** on tenant IDs and resource IDs, cross-tenant access attempts.
- Injection testing (SQLi, command, template, XSS, header/CRLF), input-validation and file-handling abuse.
- Webhook **HMAC** verification testing (signature stripping, weak/empty secret, replay, timing).
- **SSRF** via EHR source `BaseUrl` and any server-side fetch, restricted to safe internal-network probing (see prohibited list).
- Secrets exposure review, security-headers/TLS configuration testing, and dependency/CVE identification.
- Proof-of-concept exploitation **sufficient to demonstrate impact only**.

### Prohibited actions
- **No real-PHI access or exfiltration.** If real ePHI is encountered, stop and follow §8.
- **No destructive denial-of-service.** No volumetric floods, resource-exhaustion, or availability attacks against **any** environment; **never** against production. Rate-limit/lockout controls are tested with the *minimum* volume needed to demonstrate behavior.
- **No pivoting to out-of-scope systems**, and no attacks against Microsoft/Azure/EHR third-party services themselves.
- No modification, deletion, or encryption (ransomware-style) of data; no planting of persistent backdoors or web shells left in place.
- No use of malware not disclosed to and approved by the Organization.
- **SSRF demonstration is limited to read-only, non-destructive internal probing** — no exploitation of internal services beyond proving reachability.
- No exfiltration of live secrets/keys off Tester-controlled, encrypted storage; redact secrets in the report.
- No public disclosure of any finding.

---

## 7. Rules for handling live findings

- **Critical finding / active compromise:** notify the Organization emergency contact (§9) **immediately** (target: within [TO CONFIRM — e.g., 2 hours]) with enough detail to assess risk. Do not continue exploiting once impact is demonstrated.
- **Suspected real-PHI exposure or prior breach evidence:** stop, preserve nothing off-environment, and invoke §8 + §9 immediately.
- The Tester maintains a timestamped activity log for deconfliction and post-engagement review.

---

## 8. ePHI and sensitive-data handling

1. Testing uses **synthetic data only**; the Tester must not intentionally seek out or collect real ePHI.
2. If real ePHI is encountered: **stop testing the affected component**, do **not** download/copy/screenshot the data beyond the minimum needed to prove the issue, **redact** it, and notify the Organization per §9.
3. Any data captured during testing (including synthetic data, screenshots, request/response captures) is:
   - stored **encrypted at rest** on Tester-controlled systems,
   - transmitted only over **encrypted channels**,
   - **not** shared with any third party,
   - **securely destroyed** within [TO CONFIRM — e.g., 30 days] of report acceptance, with written confirmation of destruction.
4. The engagement is covered by a mutual **NDA** and, if any real ePHI could be reached, a **Business Associate Agreement (BAA)** must be in place with the Tester **before** testing begins. [ORGANIZATION TO CONFIRM BAA status]
5. Final report and evidence are delivered via [TO CONFIRM — secure channel], marked confidential.

---

## 9. Emergency contacts

| Role | Name | Contact (phone / email) | Availability |
|---|---|---|---|
| Organization — Engagement owner | [TO ASSIGN] | [TO ASSIGN] | Business hours |
| Organization — Technical / on-call (incident) | [TO ASSIGN] | [TO ASSIGN] | 24/7 during test window |
| Organization — Security / Privacy Officer (ePHI incidents) | [TO ASSIGN] | [TO ASSIGN] | 24/7 during test window |
| Tester — Lead consultant | [TO ASSIGN] | [TO ASSIGN] | During test window |
| Tester — Engagement manager | [TO ASSIGN] | [TO ASSIGN] | Business hours |

Deconfliction: if the Organization observes activity it cannot attribute, it contacts the Tester lead before treating it as a real incident.

---

## 10. Deliverables

See [`rfp-vendor-selection.md`](./rfp-vendor-selection.md) §Deliverables. At minimum: an executive summary, detailed findings with CVSS scores and reproduction steps, remediation guidance, and an **included retest** of remediated findings, with an attestation letter suitable for sharing with customers/auditors.

---

## Authorization ("get-out-of-jail" card)

> This section constitutes written authorization for the Tester to perform the activities described in this ROE against the in-scope staging targets, during the stated test window, subject to all limitations herein. It is **not** authorization to test any system, network, or data not explicitly listed. This authorization does not waive any law; the Tester remains bound by applicable law and this ROE.

**The undersigned confirm they are authorized to grant/accept this authorization on behalf of their organizations, and that they have read and agree to this ROE.**

| | Organization | Tester |
|---|---|---|
| Name | [ORGANIZATION TO SIGN] | [TESTER TO SIGN] |
| Title | [ORGANIZATION TO SIGN] | [TESTER TO SIGN] |
| Signature | ______________________ | ______________________ |
| Date | __________ | __________ |

**Scope of this authorization:** in-scope targets in §3 · test window in §5 · authorized techniques in §6, subject to prohibited actions and ePHI handling in §6/§8.

_Retain the signed ROE with the engagement record as audit evidence (HIPAA §164.308(a)(8) / SOC 2 CC4.1)._
