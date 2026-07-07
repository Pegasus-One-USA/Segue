# SOC 2 Readiness Assessment — FHIRBridge

**Purpose.** A pre-audit gap analysis across the SOC 2 Common Criteria (CC1–CC9) plus the Availability (A1) and Confidentiality (C1) categories. For each criterion this records **what is in place** (citing real FHIRBridge controls), **what is missing** (with emphasis on organizational controls), and a **readiness status**.

**Assessment date:** [ORGANIZATION TO COMPLETE]
**Assessed by:** [TO ASSIGN]
**Scope under assessment:** FHIRBridge platform (.NET 9, SQL Server, Azure Key Vault, docker-compose) + the organization operating it.
**Target categories:** Security (CC), Availability (A1), Confidentiality (C1).

## Readiness status legend

- **Ready** — control designed, implemented, documented; evidence producible today.
- **Partial** — technical layer exists but an organizational process/record is missing or not yet operating.
- **Gap** — control not established; must be built before Type I.

## Summary

| Criterion | Theme | Status |
|---|---|---|
| CC1 | Control environment (governance, integrity, org structure) | **Gap** — org processes missing |
| CC2 | Communication & information | **Partial** |
| CC3 | Risk assessment | **Gap** — no formal, documented process |
| CC4 | Monitoring of controls | **Partial** |
| CC5 | Control activities | **Partial** |
| CC6 | Logical & physical access | **Ready (technical) / Partial (process)** |
| CC7 | System operations | **Partial** |
| CC8 | Change management | **Partial** |
| CC9 | Risk mitigation (vendors, incidents) | **Gap → Partial** |
| A1 | Availability | **Partial** |
| C1 | Confidentiality | **Ready / Partial** |

**Headline:** the *technical* controls are strong and largely audit-ready. The *organizational* controls — governance cadence, HR onboarding/offboarding evidence, a formal risk assessment, vendor management, and a documented change-management workflow — are the critical path to Type I.

---

## CC1 — Control Environment

*Integrity/ethics, board/management oversight, org structure, authority & responsibility, accountability.*

**In place**
- A HIPAA/security policy manual exists and is structured for adoption (`../policies/`, POL-000…POL-013), including **Assigned Security Responsibility** (POL-002) and a **Security Management Process** (POL-001) with a sanction policy.
- Defined roles referenced throughout the manual (Security Official, Privacy Official, Executive Sponsor).

**Missing (organizational)**
- **Board / executive oversight cadence** — no evidence of a recurring management or board security review with minutes.
- **Signed, dated policy approvals** — Approval blocks are `[ORGANIZATION TO COMPLETE]`; policies are not yet formally ratified.
- **Org chart & job descriptions** defining security responsibilities.
- **Code of conduct / ethics attestation** signed by workforce.
- **Background checks** as a hiring control (see also CC1 / Workforce).

**Readiness: Gap.** Ratify policies (signatures + dates), stand up a quarterly management security review with minutes, publish an org chart and a code of conduct with acknowledgements.

---

## CC2 — Communication & Information

*Internal communication of objectives/responsibilities; external communication with customers/partners.*

**In place**
- Compliance documentation set (README, control matrix, policies) communicates controls internally.
- Policy manual defines distribution & acknowledgement (POL-000 §4.5) and workforce training (POL-005).
- External technical communication of security posture via the control matrix, intended to be shared with auditors/customers.

**Missing (organizational)**
- **Evidence** that internal comms actually happen (training completion records, acknowledgement logs).
- **External-facing security commitments** — a customer-facing security page / trust portal, and a documented way for customers and external parties to report security concerns (an intake channel).
- **System description** document (the "Section III" narrative the auditor's report requires).

**Readiness: Partial.** Produce acknowledgement/training records, publish an external security contact/intake, and draft the system description.

---

## CC3 — Risk Assessment

*Specify objectives, identify & analyze risk, assess fraud risk, evaluate change.*

**In place**
- A risk-assessment workspace and asset inventory exist (`../risk-assessment/asset-inventory.md`).
- Policy manual references periodic risk analysis (POL-001, POL-008 Evaluation).

**Missing (organizational)**
- **A completed, formal risk assessment** — documented methodology, threat/vulnerability identification, likelihood × impact scoring, and a risk register with treatment decisions and owners. This is the single most-cited SOC 2 gap.
- **Fraud-risk consideration** explicitly documented.
- **Annual re-assessment cadence** with retained prior results.

**Readiness: Gap.** Run and document a formal risk assessment producing a risk register; schedule annual refresh. This is a prerequisite for Type I.

---

## CC4 — Monitoring of Controls

*Ongoing and separate evaluations; communication of deficiencies.*

**In place**
- **Startup TDE health check** surfaces at-rest-encryption misconfiguration automatically.
- **OpenTelemetry observability + health-check endpoints** for runtime monitoring.
- **Structured Serilog logging** and a **hash-chained append-only audit log** enabling anomaly detection.
- **CI pipeline** runs dependency scanning, CodeQL SAST, gitleaks secret scanning, and Dependabot — a continuous control-monitoring signal.
- POL-008 (Evaluation) defines periodic self-assessment.

**Missing (organizational)**
- **Alerting/SIEM** — log aggregation with defined alert rules and on-call routing (logs exist; automated alerting is not evidenced).
- **Deficiency-tracking process** — a register where control failures/findings are logged, assigned, and remediated with dates.
- **Periodic internal control review** actually performed and minuted (separate evaluation).
- **Audit-log integrity monitoring** — a scheduled job that re-verifies the hash chain and alerts on breaks.

**Readiness: Partial.** Add alerting on top of existing telemetry, stand up a findings/deficiency register, and schedule the periodic internal review + chain-verification job.

---

## CC5 — Control Activities

*Select & develop control activities (incl. over technology), deploy via policy & procedure.*

**In place**
- Broad technical control set: RBAC permission policies, PBKDF2-SHA256 (350k) hashing, account lockout, TOTP MFA, JWT/Entra SSO, TDE, Key Vault, HTTPS/HSTS/security headers, HMAC webhook signatures, rate limiting, PHI log masking, de-identification, 7-year retention engine.
- **~100+ automated tests** gate code changes.
- Policies map controls to responsibilities (POL-009…POL-013).

**Missing (organizational)**
- **Procedures** (the "how we run it" runbooks) behind several controls — e.g., how access reviews are performed, how retention/disposal is executed and evidenced.
- **Segregation of duties** documented for sensitive actions (deploy, prod data access, key management).
- **Evidence** that policy-mandated activities are actually performed on cadence.

**Readiness: Partial.** Technical activities are strong; document the operating procedures and produce cadence evidence.

---

## CC6 — Logical & Physical Access

*Identity, authentication, authorization, provisioning/deprovisioning, encryption, physical access.*

**In place (technical — strong)**
- **Authentication:** PBKDF2-SHA256 (350k iterations), account lockout (5/15min, configurable), **TOTP MFA**, JWT bearer + **Microsoft Entra SSO**; auth-endpoint rate limiting; HMAC-SHA256 for machine-to-machine webhooks.
- **Authorization:** RBAC permission policies enforced on API endpoints; scoped permissions per user.
- **Encryption:** SQL Server **TDE** at rest + startup health check; **Azure Key Vault** for secrets; Data-Protection key ring encrypting OAuth/launch tokens; HTTPS/HSTS in transit; outbound `BaseUrl` HTTPS validation.
- **De-identification:** Safe Harbor + k-anonymity.
- **Unique identity & attribution:** every action stamped into the hash-chained `UserActivityAuditLog`.
- **Provisioning:** invite / resend-invite onboarding flow; deprovisioning by disabling accounts.

**Missing (organizational / process)**
- **Periodic user access reviews** — a recurring (e.g., quarterly) review of who has what access, with sign-off and retained records. *Not yet operating.*
- **Formal joiner/mover/leaver process** tying access changes to HR events, with evidence (tickets, timestamps).
- **Physical access** — reliance on Azure for data-center physical security must be evidenced via **Azure's SOC 2 report** treated as a subservice organization (carve-out/inclusive method decision). Office/endpoint physical controls per POL-012 need evidence.
- **Privileged-access management** — documented control over admin/prod credentials and key-vault access.

**Readiness: Ready (technical) / Partial (process).** Stand up quarterly access reviews and a documented JML process; obtain and map Azure's SOC 2 report for physical security.

---

## CC7 — System Operations

*Detect/monitor anomalies, respond to security incidents, evaluate events.*

**In place**
- Anomaly-relevant telemetry: OpenTelemetry, health checks, structured logs, tamper-evident audit trail.
- Rate-limit rejections and lockout events observable in logs.
- Security Incident Procedures policy (POL-006) with breach-notification reference (§164.410).
- CI security scanning (CodeQL, dependency scan, gitleaks) detecting code-level issues pre-deploy.

**Missing (organizational)**
- **Operating incident-response evidence** — IR plan tested (tabletop), an incident register, defined severities/SLAs, and post-incident reviews.
- **Alerting/on-call** wiring (see CC4).
- **Vulnerability management** — cadence for scanning/pen-testing and tracked remediation (see CC9).
- **Backup/restore verification** evidence (see A1).

**Readiness: Partial.** Operationalize IR (tabletop + register), add alerting/on-call, and establish vuln-management cadence.

---

## CC8 — Change Management

*Authorize, design, develop, test, approve, and deploy changes.*

**In place**
- **Versioned EF Core migrations** for schema evolution.
- **Reproducible docker-compose** deployment.
- **~100+ automated tests** gate changes; CI runs SAST/secret/dependency scanning on changes.
- Source control (git) provides change history and attribution.

**Missing (organizational)**
- **Documented change-management process** — pull-request review requirement, approval gates, separation between author and approver, and a link from change → ticket/authorization.
- **Branch-protection / required-review** rules evidenced on the repo.
- **Release/deployment approval** records and rollback procedure.
- **Emergency-change** process.

**Readiness: Partial.** Formalize and evidence the PR-review + approval workflow (branch protection, required reviewers), document release approvals and rollback.

---

## CC9 — Risk Mitigation

*Mitigate risk from business disruptions and from vendors/business partners.*

**In place**
- Backup/DR runbook (`../backup-disaster-recovery.md`) addresses disruption risk.
- BAA folder exists as a vendor/subservice register scaffold (`../baa/`).
- Key dependency on Azure (Key Vault, hosting, TDE) is identified in the control matrix.

**Missing (organizational)**
- **Vendor / subservice-organization management** — a vendor inventory with risk tiers, **collection and annual review of vendors' SOC 2 reports** (Azure and any subprocessors), and contractual security terms. *Largely not operating.*
- **Signed BAAs** on file (Azure + downstream partners handling PHI).
- **Business-continuity plan** beyond backup/DR (defined RTO/RTO ownership, communications plan).
- **Insurance** (cyber) as a mitigation, if applicable.

**Readiness: Gap → Partial.** Build a vendor register, collect Azure's SOC 2 + signed BAAs, and formalize the BCP.

---

## A1 — Availability

*Capacity, environmental protections, backup, recovery.*

**In place**
- **Backup/DR runbook** with recommended RPO/RTO, SQL backup strategy, Data-Protection key-ring backup, restore procedure, DR-test cadence, and retention.
- **Health-check endpoints + OpenTelemetry** for availability monitoring.
- **7-year retention engine** (data lifecycle).

**Missing (organizational)**
- **Executed backups + tested restores** with evidence (backup job logs, restore-test reports on cadence).
- **Documented, measured SLA / uptime objective** and monitoring against it.
- **Capacity monitoring** and scaling procedure.
- **DR test actually performed** and after-action report retained.

**Readiness: Partial.** Turn the runbook into evidenced operations: scheduled backups, at least one documented restore test, uptime monitoring, and a DR-test after-action report.

---

## C1 — Confidentiality

*Identify, protect, and dispose of confidential information.*

**In place (strong)**
- **Encryption** at rest (TDE + Key Vault) and in transit (HTTPS/HSTS).
- **De-identification** (Safe Harbor + k-anonymity) minimizing PHI exposure.
- **PHI log masking** (`PhiMaskingEnricher`) preventing PHI leakage into logs.
- **RBAC** restricting access to confidential data.
- **7-year retention + disposal** policy (POL-013) and retention engine.
- **Data-classification** implicit in policies; asset inventory exists.

**Missing (organizational)**
- **Formal data-classification policy** naming confidentiality tiers and handling rules.
- **Disposal evidence** — records showing secure disposal at end of retention.
- **Confidentiality commitments** to customers documented (contracts/DPAs) and mapped.

**Readiness: Ready / Partial.** Publish a data-classification policy and produce disposal evidence; otherwise technically strong.

---

## Critical-path gaps for Type I (do these first)

1. **Formal risk assessment + risk register** (CC3) — prerequisite, most-cited gap.
2. **Ratify policies** with signatures/dates and stand up **management oversight cadence** (CC1).
3. **User access reviews + JML process** operating with records (CC6).
4. **Documented change-management workflow** with branch protection & required reviews (CC8).
5. **Vendor management**: collect Azure SOC 2, signed BAAs, vendor register (CC9).
6. **Alerting + deficiency/findings register** on existing telemetry (CC4/CC7).
7. **Backup executed + restore tested + DR test performed** with evidence (A1).

Technical controls are largely **Ready**; the above organizational items are the gating work.
