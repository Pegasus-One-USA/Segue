# Access Control & Authentication Policy

**Policy ID:** POL-009
**HIPAA Citation:** §164.312(a)(1) — Access Control (Unique User ID, Emergency Access, Automatic Logoff, Encryption/Decryption); §164.312(d) — Person or Entity Authentication
**Status:** ADOPTED
**Owner:** [ROLE — Security Official]
**Version:** 1.0

---

## 1. Purpose

This policy defines the technical controls that ensure only authenticated, authorized identities can access electronic Protected Health Information (ePHI) in FHIRBridge. It implements the access-control specifications of §164.312(a) and the authentication requirement of §164.312(d).

## 2. Scope

This policy applies to all authentication and access-control mechanisms of FHIRBridge: local accounts, Entra SSO, JWT-based API access, service/machine identities, and session handling.

## 3. Policy Statements

### 3.1 Unique User Identification (§164.312(a)(2)(i)) — REQUIRED
- Every user and service identity has a **unique** identifier. Shared, generic, or anonymous accounts are prohibited.
- All access is attributable to a specific identity in the audit trail (POL-010).

### 3.2 Person or Entity Authentication (§164.312(d)) — REQUIRED
- **Local accounts:** passwords are stored using **PBKDF2 hashing** (never plaintext or reversible encryption) and must satisfy the **minimum 12-character complexity policy**.
- **Account lockout** is enforced after a configured number of consecutive failed attempts to resist brute-force and credential-stuffing attacks.
- **Multi-factor authentication (TOTP MFA)** is required for [ORGANIZATION TO COMPLETE — at minimum all administrative accounts; recommended for all ePHI-accessing accounts].
- **Federated authentication:** Entra SSO (JWT) is supported; SSO identities are subject to the Organization's Entra conditional-access and MFA policies.
- **Service/API authentication:** machine identities authenticate via scoped credentials/JWT; secrets are stored in Azure Key Vault and rotated (see POL-011).

### 3.3 Authorization
- After authentication, authorization is enforced through FHIRBridge RBAC permissions on a least-privilege basis (see POL-004).

### 3.4 Automatic Logoff / Session Management (§164.312(a)(2)(iii)) — ADDRESSABLE
- Sessions and access tokens have bounded lifetimes and expire automatically. Idle/inactive sessions are terminated after **[ORGANIZATION TO COMPLETE — e.g., 15 minutes]** of inactivity, requiring re-authentication.
- Token issuance/expiry is governed centrally; revoked or expired tokens are rejected.

### 3.5 Emergency Access Procedure (§164.312(a)(2)(ii)) — REQUIRED
- A documented emergency ("break-glass") access procedure exists to obtain necessary ePHI during an emergency while preserving authentication and audit logging. Break-glass use is pre-authorized by [ROLE — Security Official], time-limited, and fully logged. [ORGANIZATION TO COMPLETE — document the break-glass mechanism and reviewer.]

### 3.6 Encryption and Decryption (§164.312(a)(2)(iv)) — ADDRESSABLE
- ePHI at rest is protected by encryption (SQL Server Transparent Data Encryption); ePHI in transit is protected by HTTPS/TLS. Cryptographic details are governed by POL-011.

### 3.7 Credential Hygiene
- Credential sharing is prohibited. Suspected credential compromise is reported immediately (POL-006) and triggers credential rotation.

## 4. Roles & Responsibilities

| Role | Responsibility |
|------|----------------|
| [ROLE — Security Official / IT Administrator] | Configures and maintains authentication controls, MFA, lockout, and session settings. |
| [ROLE — Manager / Data Owner] | Authorizes access levels (per POL-004). |
| All workforce members | Maintain unique credentials; use MFA; never share credentials. |

## 5. Enforcement / Sanctions

Credential sharing, MFA circumvention, or unauthorized access is subject to the sanction policy in POL-001 §3.3.

## 6. Review Cadence

Reviewed at least **annually** and when authentication mechanisms change.

## 7. Revision History

| Version | Date | Author | Summary |
|---------|------|--------|---------|
| 1.0 | [ORGANIZATION TO COMPLETE] | [ROLE — Security Official] | Initial adopted policy. |

## 8. Approval

| Role | Name | Signature | Date |
|------|------|-----------|------|
| Security Official | [ORGANIZATION TO COMPLETE] | | |
| IT Administrator | [ORGANIZATION TO COMPLETE] | | |
