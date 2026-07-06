# Security Incident Procedures Policy

**Policy ID:** POL-006
**HIPAA Citation:** §164.308(a)(6) — Security Incident Procedures (Response and Reporting); references §§164.400–164.414 (Breach Notification)
**Status:** ADOPTED
**Owner:** [ROLE — Security Official]
**Version:** 1.0

---

## 1. Purpose

This policy establishes how the Organization identifies, responds to, mitigates, documents, and reports security incidents affecting FHIRBridge and the electronic Protected Health Information (ePHI) it processes. It implements §164.308(a)(6) and cross-references the HITECH breach-notification obligations.

## 2. Scope

This policy applies to all suspected or confirmed security incidents affecting FHIRBridge, its data, its infrastructure, or its workforce, regardless of source (external attack, insider action, vendor/subprocessor, or accidental).

## 3. Definitions

- **Security Incident:** the attempted or successful unauthorized access, use, disclosure, modification, or destruction of information, or interference with system operations.
- **Breach:** the acquisition, access, use, or disclosure of unsecured PHI in a manner not permitted by the Privacy Rule that compromises its security or privacy (subject to the risk-assessment exception of §164.402).

## 4. Policy Statements

### 4.1 Reporting
- Any workforce member who suspects an incident reports it **immediately** to [ROLE — Security Official] via [ORGANIZATION TO COMPLETE — contact/channel, e.g., security@org, on-call number].
- Good-faith reporting is encouraged and protected from retaliation.

### 4.2 Incident Response Lifecycle
The Organization follows a structured lifecycle, with roles assigned by the Incident Commander:

1. **Detection & Identification** — Incidents are detected via the FHIRBridge audit trail, log monitoring, anti-malware/endpoint alerts, rate-limiting/lockout signals, hash-chain verification failures, or workforce/customer reports. The Security Official triages and assigns a severity.
2. **Containment** — Limit the scope and impact (e.g., disable compromised accounts, rotate secrets/keys and HMAC webhook secrets, isolate affected components, block malicious sources via rate limiting/firewalling). Preserve evidence before altering systems where feasible.
3. **Eradication** — Remove the root cause (malware, vulnerability, misconfiguration, compromised credential).
4. **Recovery** — Restore systems and data to normal operation using validated backups (see POL-007) and confirm integrity before returning to service.
5. **Post-Incident Review** — Conduct a documented lessons-learned review; feed findings into risk management (POL-001) and remediation.

### 4.3 Evidence Preservation
- Relevant logs, the hash-chained audit trail, timelines, and forensic artifacts are preserved to support investigation and any legal/regulatory obligations. The tamper-evident audit log (POL-010) is a primary evidentiary source.
- Evidence handling maintains chain-of-custody where an incident may lead to legal action.

### 4.4 Documentation
- Every incident is documented: description, discovery date/time, affected systems and data, severity, actions taken, individuals involved, and resolution. Incident records are retained for six (6) years.

### 4.5 Breach Determination and Notification (HITECH)
- For any incident involving PHI, the Security Official (with the Privacy Official) performs a **four-factor risk assessment** (§164.402) to determine whether a reportable breach occurred, unless the Organization treats the incident as a breach without assessment.
- **As a Business Associate:** upon discovery of a breach of unsecured PHI, the Organization notifies the affected covered entity/entities **without unreasonable delay and no later than 60 calendar days** from discovery, providing the information required by §164.410 (identification of affected individuals, description of the breach, and mitigation steps). Downstream notification obligations are coordinated per the applicable Business Associate Agreement.
- **As a Covered Entity (where applicable):** notifications to individuals, HHS, and (where required) the media follow §§164.404–164.408 timelines.
- The detailed breach-notification procedure (decision tree, templates, contact lists, regulator addresses) is maintained separately. [ORGANIZATION TO COMPLETE — confirm location/reference of the standalone breach-notification detail.]
- Breach-notification decisions and regulator/customer communications are authorized by [ROLE — Privacy Official / Legal].

### 4.6 De-Identified Data
- Data de-identified via the FHIRBridge Safe Harbor / k-anonymity de-identification (see POL-011/POL-013) is not PHI; incidents limited strictly to properly de-identified data do not trigger breach notification, but are still logged and reviewed.

## 5. Roles & Responsibilities

| Role | Responsibility |
|------|----------------|
| [ROLE — Security Official] | Incident Commander; triage, coordination, response, documentation. |
| [ROLE — Privacy Official / Legal] | Breach determination; regulatory/customer notification decisions. |
| [ROLE — IT Administrator] | Executes containment/eradication/recovery actions. |
| All workforce members | Report suspected incidents immediately; assist as directed. |

## 6. Enforcement / Sanctions

Failure to report a known incident, or interference with response, is subject to the sanction policy in POL-001 §3.3.

## 7. Review Cadence

Reviewed at least **annually** and after any significant incident; procedures tested/tabletop-exercised at least annually (coordinate with POL-007).

## 8. Revision History

| Version | Date | Author | Summary |
|---------|------|--------|---------|
| 1.0 | [ORGANIZATION TO COMPLETE] | [ROLE — Security Official] | Initial adopted policy. |

## 9. Approval

| Role | Name | Signature | Date |
|------|------|-----------|------|
| Security Official | [ORGANIZATION TO COMPLETE] | | |
| Privacy Official / Legal | [ORGANIZATION TO COMPLETE] | | |
