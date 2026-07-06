# Integrity & Transmission Security Policy

**Policy ID:** POL-011
**HIPAA Citation:** §164.312(c)(1) — Integrity (Mechanism to Authenticate ePHI); §164.312(e)(1) — Transmission Security (Integrity Controls, Encryption)
**Status:** ADOPTED
**Owner:** [ROLE — Security Official]
**Version:** 1.0

---

## 1. Purpose

This policy protects electronic Protected Health Information (ePHI) in FHIRBridge from improper alteration or destruction and secures it during electronic transmission, implementing §164.312(c) and §164.312(e).

## 2. Scope

This policy applies to ePHI at rest and in transit across FHIRBridge — ingestion from EHR sources, internal processing/transformation, the audit store, secrets, and delivery to downstream destinations and webhooks.

## 3. Policy Statements

### 3.1 Data Integrity (§164.312(c)(1)) — REQUIRED
- ePHI is protected from unauthorized or accidental modification and destruction.
- The **hash-chained, append-only audit log** provides tamper-evidence for security-relevant records (see POL-010).
- Application access controls (RBAC) and encryption limit who and what can alter ePHI.
- Backups and restore validation (POL-007) support recovery to a known-good state if integrity is compromised.

### 3.2 Mechanism to Authenticate ePHI (§164.312(c)(2)) — ADDRESSABLE
- FHIRBridge validates the authenticity and integrity of received data. Inbound webhooks are authenticated using **HMAC-SHA256 signature validation**; messages failing signature verification are rejected and the failure is logged (POL-010).
- Where source systems provide integrity signals, they are validated during ingestion. [ORGANIZATION TO COMPLETE — note any source-specific integrity checks in use.]

### 3.3 Transmission Encryption (§164.312(e)(2)(ii)) — ADDRESSABLE
- **All network transmission of ePHI is encrypted.** HTTPS is mandatory (HTTPS redirection and HSTS enforced outside development); outbound source/destination endpoints must use HTTPS.
- Minimum protocol: **TLS 1.2 or higher**; weak ciphers/protocols are disabled. [ORGANIZATION TO COMPLETE — confirm minimum TLS and cipher policy.]

### 3.4 Encryption at Rest
- ePHI at rest is encrypted via SQL Server **Transparent Data Encryption (TDE)**; production must not operate without it (verified at startup).
- Secrets, keys, OAuth/launch tokens, webhook secrets, and passwords are protected: secrets are stored in **Azure Key Vault**; passwords are hashed with **PBKDF2**; the Data-Protection key ring is safeguarded and backed up (its loss makes encrypted tokens unrecoverable).

### 3.5 Key and Secret Management
- Secrets are never committed to source control or written to logs.
- Keys and secrets are rotated on a defined cadence: [ORGANIZATION TO COMPLETE — e.g., annually or on compromise]. Key Vault soft-delete and purge protection are enabled.
- Rotation of a compromised secret is immediate and coordinated with incident response (POL-006).

### 3.6 De-Identification Integrity
- When data is de-identified via Safe Harbor / k-anonymity de-identification, the process is applied consistently so that output does not permit re-identification; de-identification configuration changes are reviewed before deployment.

## 4. Roles & Responsibilities

| Role | Responsibility |
|------|----------------|
| [ROLE — Security Official] | Owns integrity/transmission policy; approves crypto standards and rotation cadence. |
| [ROLE — IT Administrator / DevOps] | Configures TLS/HSTS, TDE, Key Vault, HMAC validation, and key rotation. |
| [ROLE — Integration Engineer] | Ensures source/destination endpoints use HTTPS and integrity checks. |
| All workforce members | Never disable transport/at-rest encryption or expose secrets. |

## 5. Enforcement / Sanctions

Disabling encryption, transmitting ePHI over insecure channels, or mishandling keys/secrets is subject to the sanction policy in POL-001 §3.3.

## 6. Review Cadence

Reviewed at least **annually** and when cryptographic standards or transmission mechanisms change.

## 7. Revision History

| Version | Date | Author | Summary |
|---------|------|--------|---------|
| 1.0 | [ORGANIZATION TO COMPLETE] | [ROLE — Security Official] | Initial adopted policy. |

## 8. Approval

| Role | Name | Signature | Date |
|------|------|-----------|------|
| Security Official | [ORGANIZATION TO COMPLETE] | | |
| IT Administrator | [ORGANIZATION TO COMPLETE] | | |
