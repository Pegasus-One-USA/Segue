# Facility & Device Security Policy

**Policy ID:** POL-012
**HIPAA Citation:** §164.310 — Physical Safeguards (Facility Access Controls, Workstation Use, Workstation Security, Device & Media Controls)
**Status:** ADOPTED
**Owner:** [ROLE — Security Official]
**Version:** 1.0

---

## 1. Purpose

This policy establishes physical safeguards to limit physical access to systems and facilities that house electronic Protected Health Information (ePHI), and to govern the use and security of workstations, devices, and media. It implements §164.310.

## 2. Scope

FHIRBridge production infrastructure is hosted in Microsoft Azure. This policy covers both the **inherited** physical controls of the cloud provider and the **Organization-managed** physical controls for offices, workstations, and portable devices/media used to access ePHI.

## 3. Policy Statements

### 3.1 Facility Access Controls (§164.310(a)(1)) — REQUIRED-standard
- **Cloud/data-center (inherited):** FHIRBridge production systems run in Microsoft Azure data centers, whose physical security (perimeter, badge/biometric access, surveillance, environmental controls) is inherited and covered by the executed Business Associate Agreement with Microsoft. The Organization does not operate its own ePHI data center. [ORGANIZATION TO COMPLETE — retain Azure/Microsoft compliance attestations, e.g., SOC 2 / ISO 27001, as evidence.]
- **Organization offices:** physical access to any Organization facility where ePHI may be accessed is restricted to authorized personnel via [ORGANIZATION TO COMPLETE — e.g., badge access, visitor sign-in, locked areas]. Contingency operations and facility access during emergencies are addressed in POL-007.
- Access records for Organization facilities are maintained and reviewed. [ORGANIZATION TO COMPLETE — describe facility access-control and maintenance-record procedures.]

### 3.2 Workstation Use (§164.310(b)) — REQUIRED-standard
- Workstations used to access FHIRBridge or ePHI are used only for authorized business purposes and are configured to protect ePHI: automatic screen lock, encrypted disk, current OS/patch level, and approved endpoint protection (see POL-005).
- Workstations are positioned/configured to prevent unauthorized viewing of ePHI (e.g., privacy screens in shared spaces). Remote/home workstations meet the same standard.

### 3.3 Workstation Security (§164.310(c)) — REQUIRED-standard
- Physical safeguards restrict access to workstations that can reach ePHI: workstations are secured against theft, locked when unattended, and not left logged in and unattended. [ORGANIZATION TO COMPLETE — cable locks, secured rooms, or other measures per environment.]

### 3.4 Device and Media Controls (§164.310(d)(1)) — REQUIRED-standard
Governs receipt, removal, and movement of hardware and electronic media containing ePHI:
- **Disposal (§164.310(d)(2)(i)) — REQUIRED:** ePHI-bearing media and devices are securely sanitized or destroyed before disposal, per [ORGANIZATION TO COMPLETE — standard, e.g., NIST SP 800-88]. Disposal is documented (see POL-013). For cloud-stored data, deletion follows the provider's certified data-destruction processes.
- **Media Re-use (§164.310(d)(2)(ii)) — REQUIRED:** ePHI is removed (cryptographic erase or sanitization) from media before re-use.
- **Accountability (§164.310(d)(2)(iii)) — ADDRESSABLE:** movement of hardware/media containing ePHI is recorded, with the person responsible identified. Portable media use is minimized and, where used, encrypted. [ORGANIZATION TO COMPLETE — media inventory/tracking method.]
- **Data Backup and Storage (§164.310(d)(2)(iv)) — ADDRESSABLE:** a retrievable, exact copy of ePHI is created before equipment is moved when needed (aligns with backup requirements in POL-007).

### 3.5 Portable Devices and BYOD
- Personal or portable devices used to access ePHI must meet Organization security requirements (encryption, screen lock, endpoint protection, remote wipe capability). [ORGANIZATION TO COMPLETE — BYOD/MDM policy reference.]

## 4. Roles & Responsibilities

| Role | Responsibility |
|------|----------------|
| [ROLE — Security Official] | Owns physical-safeguard policy; retains cloud attestations; oversees media disposal. |
| [ROLE — IT Administrator / Facilities] | Manages workstation configuration, facility access, device inventory, and sanitization. |
| All workforce members | Secure workstations/devices; follow media-handling and disposal rules. |

## 5. Enforcement / Sanctions

Improper handling, insecure disposal, or loss of ePHI-bearing devices/media is subject to the sanction policy in POL-001 §3.3 and may trigger incident/breach procedures (POL-006).

## 6. Review Cadence

Reviewed at least **annually** and upon facility, hosting, or device-fleet changes.

## 7. Revision History

| Version | Date | Author | Summary |
|---------|------|--------|---------|
| 1.0 | [ORGANIZATION TO COMPLETE] | [ROLE — Security Official] | Initial adopted policy. |

## 8. Approval

| Role | Name | Signature | Date |
|------|------|-----------|------|
| Security Official | [ORGANIZATION TO COMPLETE] | | |
| IT Administrator / Facilities | [ORGANIZATION TO COMPLETE] | | |
