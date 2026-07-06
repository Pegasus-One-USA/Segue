# Security Awareness & Training Policy

**Policy ID:** POL-005
**HIPAA Citation:** §164.308(a)(5) — Security Awareness and Training (Security Reminders, Malicious-Software Protection, Log-in Monitoring, Password Management)
**Status:** ADOPTED
**Owner:** [ROLE — Security Official]
**Version:** 1.0

---

## 1. Purpose

This policy establishes a security awareness and training program for all workforce members so they understand their responsibilities in protecting electronic Protected Health Information (ePHI) in FHIRBridge. It implements the four addressable specifications of §164.308(a)(5): security reminders, protection from malicious software, log-in monitoring, and password management.

## 2. Scope

This policy applies to all workforce members, including new hires, and to all who administer or use FHIRBridge.

## 3. Policy Statements

### 3.1 Training Program
- All workforce members complete HIPAA security and privacy awareness training at onboarding, **before or immediately upon** being granted ePHI access, and at least **annually** thereafter.
- Additional targeted training is delivered when material changes occur (new threats, new features, post-incident lessons learned).
- Completion is tracked; records (who, what, when) are retained for six (6) years by [ROLE — Security Official].
- Role-specific training is provided to administrators and integration engineers covering secure configuration of FHIRBridge (RBAC, MFA, secrets handling, source/destination configuration).

### 3.2 Security Reminders (§164.308(a)(5)(ii)(A)) — ADDRESSABLE
- Periodic security reminders are issued (e.g., phishing-awareness notices, policy refreshers, alerts on emerging threats) on a **[ORGANIZATION TO COMPLETE — e.g., quarterly]** basis and after notable incidents.

### 3.3 Protection from Malicious Software (§164.308(a)(5)(ii)(B)) — ADDRESSABLE
- Workstations and servers that access FHIRBridge or ePHI run approved anti-malware/endpoint protection with automatic updates enabled. [ORGANIZATION TO COMPLETE — endpoint protection product/standard.]
- Workforce members are trained to recognize and report suspected malware, phishing, and social-engineering attempts.
- Operating systems, the .NET runtime, FHIRBridge dependencies, and infrastructure are kept patched under a defined patch-management cadence: [ORGANIZATION TO COMPLETE].

### 3.4 Log-in Monitoring (§164.308(a)(5)(ii)(C)) — ADDRESSABLE
- FHIRBridge records authentication events and enforces **account lockout** after repeated failed attempts. Lockout and failed-login events are captured in the audit trail and reviewed under the information system activity review (POL-001) and audit controls (POL-010).
- Repeated failures, lockouts, or anomalous log-in patterns are investigated and escalated per POL-006.

### 3.5 Password Management (§164.308(a)(5)(ii)(D)) — ADDRESSABLE
- Local FHIRBridge passwords are subject to a **minimum 12-character complexity policy** and are stored using **PBKDF2 hashing** — never in plaintext or reversible form.
- Multi-factor authentication (**TOTP MFA**) is required for [ORGANIZATION TO COMPLETE — e.g., all administrative accounts, at minimum]; SSO logins use Entra with the organization's Entra MFA/conditional-access policy.
- Workforce members are trained never to share credentials, to use unique passwords, and to report suspected credential compromise immediately. Detailed technical standards are in POL-009.

## 4. Roles & Responsibilities

| Role | Responsibility |
|------|----------------|
| [ROLE — Security Official] | Owns the training program; issues reminders; retains completion records. |
| [ROLE — IT Administrator] | Maintains anti-malware, patching, and log-in monitoring. |
| [ROLE — HR / People Operations] | Ensures new hires complete training before ePHI access. |
| All workforce members | Complete training; follow password/MFA rules; report threats. |

## 5. Enforcement / Sanctions

Failure to complete required training or comply with awareness practices is subject to the sanction policy in POL-001 §3.3.

## 6. Review Cadence

Reviewed at least **annually**; training content refreshed to reflect current threats and platform changes.

## 7. Revision History

| Version | Date | Author | Summary |
|---------|------|--------|---------|
| 1.0 | [ORGANIZATION TO COMPLETE] | [ROLE — Security Official] | Initial adopted policy. |

## 8. Approval

| Role | Name | Signature | Date |
|------|------|-----------|------|
| Security Official | [ORGANIZATION TO COMPLETE] | | |
| HR / People Operations | [ORGANIZATION TO COMPLETE] | | |
