# Information Access Management Policy

**Policy ID:** POL-004
**HIPAA Citation:** §164.308(a)(4) — Information Access Management (Access Authorization, Access Establishment & Modification)
**Status:** ADOPTED
**Owner:** [ROLE — Security Official]
**Version:** 1.0

---

## 1. Purpose

This policy governs how access to electronic Protected Health Information (ePHI) in FHIRBridge is authorized, granted, modified, and reviewed, ensuring access aligns with the minimum necessary principle and each workforce member's role. It implements the access-authorization and access-establishment/modification specifications of §164.308(a)(4).

## 2. Scope

This policy applies to all logical access to FHIRBridge and its ePHI: local accounts, Entra SSO identities, service/machine identities, and the FHIRBridge Role-Based Access Control (RBAC) permission model.

## 3. Policy Statements

### 3.1 Least Privilege / Minimum Necessary
- Access is granted on a least-privilege, need-to-know basis. Workforce members receive only the RBAC permissions required to perform their job function.
- Blanket or standing administrative access is minimized, justified in writing, and logged.

### 3.2 Access Authorization (§164.308(a)(4)(ii)(B)) — ADDRESSABLE
- New or expanded access requires documented approval by [ROLE — Manager / Data Owner] before it is provisioned, via the FHIRBridge invitation/provisioning flow.
- Requests specify the role(s) and RBAC permission set requested and the business justification.
- Service/machine identities are authorized the same way and scoped to the specific integration they serve.

### 3.3 Access Establishment and Modification (§164.308(a)(4)(ii)(C)) — ADDRESSABLE
- Access is established by mapping the approved role to FHIRBridge RBAC permissions. The RBAC model is the single source of truth for what each identity can do.
- Every workforce member has a **unique** user identity; shared or generic accounts are prohibited (see POL-009).
- Modifications (grant, change, or revoke) follow the same approval path and are recorded in the FHIRBridge audit trail (POL-010).
- On role change or termination, access is modified/revoked per POL-003.

### 3.4 Periodic Access Review
- RBAC assignments and privileged access are reviewed at least every **[ORGANIZATION TO COMPLETE — e.g., 90 days]**. Reviewers confirm each grant is still justified; unneeded access is revoked.
- Each review is documented (reviewer, date, scope, actions taken) and retained for six (6) years.

### 3.5 Segregation and Isolation
- Where FHIRBridge serves multiple customers or data domains, access is scoped so that a workforce member or integration can reach only the ePHI it is authorized for. Cross-domain access requires explicit, documented authorization.

## 4. RBAC Mapping Guidance

The Organization maintains a current mapping of job roles to FHIRBridge RBAC permission sets. [ORGANIZATION TO COMPLETE — attach or reference the authoritative role→permission matrix.] At minimum, distinguish:

| Example Role | Typical Access | Notes |
|--------------|----------------|-------|
| Administrator | Full configuration + user management | MFA required; heavily audited |
| Integration Engineer | Configure sources/mappings/destinations | Least-privilege to assigned integrations |
| Operator / Support | Monitor pipelines, view non-PHI operational data | No standing PHI export rights |
| Auditor / Compliance | Read-only audit log access | Cannot modify configuration |

## 5. Roles & Responsibilities

| Role | Responsibility |
|------|----------------|
| [ROLE — Manager / Data Owner] | Approves access requests and modifications; certifies periodic reviews. |
| [ROLE — Security Official / IT Administrator] | Establishes/modifies access; maintains RBAC mapping; runs access reviews. |
| All workforce members | Use only authorized access; request changes through the approved flow. |

## 6. Enforcement / Sanctions

Granting, using, or retaining access outside this policy is subject to the sanction policy in POL-001 §3.3.

## 7. Review Cadence

Reviewed at least **annually**; RBAC role→permission mapping reviewed each access-review cycle.

## 8. Revision History

| Version | Date | Author | Summary |
|---------|------|--------|---------|
| 1.0 | [ORGANIZATION TO COMPLETE] | [ROLE — Security Official] | Initial adopted policy. |

## 9. Approval

| Role | Name | Signature | Date |
|------|------|-----------|------|
| Security Official | [ORGANIZATION TO COMPLETE] | | |
| Data Owner | [ORGANIZATION TO COMPLETE] | | |
