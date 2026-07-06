# FHIRBridge Subprocessor / Vendor BAA Register

This register lists every third-party vendor and subprocessor that may create, receive, maintain, or transmit ePHI **on FHIRBridge's behalf**, and tracks whether a Business Associate Agreement is required and its status. Under **§164.308(b)** and **§164.502(e)**, a signed BAA must be in place with each ePHI-touching subprocessor **before** that vendor processes ePHI, and the vendor's obligations must flow down from FHIRBridge's own BAAs.

> **This is a living document.** Every pre-populated row is a **likely** subprocessor inferred from the FHIRBridge tech stack (.NET 9, Azure hosting/SQL/Key Vault/Blob, an email sender, OpenTelemetry/App Insights, Seq in dev) and is marked **[ORGANIZATION TO CONFIRM]**. The organization must (a) confirm which vendors are actually used in production, (b) correct the "Touches ePHI?" determination for its real configuration, (c) obtain and record BAAs, and (d) add any vendor not listed. Owners are marked **[TO ASSIGN]**.

## Status legend

- **BAA required?** — YES / NO / N/A (with basis).
- **BAA status** — `NOT STARTED` · `REQUESTED` · `SIGNED` (or `N/A`).
- A vendor that "maintains" (stores) ePHI needs a BAA even if the data is encrypted and the vendor never views it. The "mere conduit" exception is narrow and does **not** apply to vendors that persistently store data.

## Register

| Vendor | Service | Touches ePHI? | BAA required? | BAA status | Date | Owner |
|--------|---------|:-------------:|:-------------:|:----------:|:----:|-------|
| Microsoft Azure — Compute/Hosting | Runs FHIRBridge containers/app services | Yes (processes ePHI) | YES | `NOT STARTED` `[ORGANIZATION TO CONFIRM]` | — | `[TO ASSIGN]` |
| Microsoft Azure — Azure SQL Database | Primary datastore for ePHI | Yes (stores ePHI) | YES | `NOT STARTED` `[ORGANIZATION TO CONFIRM]` | — | `[TO ASSIGN]` |
| Microsoft Azure — Key Vault | Secret/key management (protects but may hold ePHI-adjacent keys) | Yes (keys guarding ePHI) | YES | `NOT STARTED` `[ORGANIZATION TO CONFIRM]` | — | `[TO ASSIGN]` |
| Microsoft Azure — Blob Storage | Object storage for files/payloads | Yes (may store ePHI) | YES | `NOT STARTED` `[ORGANIZATION TO CONFIRM]` | — | `[TO ASSIGN]` |
| Microsoft Azure — Azure Monitor / Application Insights | APM, OpenTelemetry export, metrics/traces/logs | Likely (logs/traces may contain ePHI) | YES | `NOT STARTED` `[ORGANIZATION TO CONFIRM]` | — | `[TO ASSIGN]` |
| Email / SMTP provider — **[SENDGRID / AWS SES / M365 — CONFIRM]** | Outbound email from the platform's Email sender (invites, notifications) | Depends — YES if any message body/attachment can contain PHI | YES if PHI possible | `NOT STARTED` `[ORGANIZATION TO CONFIRM]` | — | `[TO ASSIGN]` |
| Container registry / hosting — **[ACR / GHCR / DOCKER HUB — CONFIRM]** | Stores container images (build artifacts) | No (images should not contain ePHI) | NO (confirm no ePHI baked into images) | `N/A` `[ORGANIZATION TO CONFIRM]` | — | `[TO ASSIGN]` |
| Managed logging — Seq | Structured log aggregation (**dev only** per current usage) | Yes IF used in prod and logs contain ePHI | YES if prod + ePHI in logs; else `N/A` | `NOT STARTED` `[ORGANIZATION TO CONFIRM]` | — | `[TO ASSIGN]` |
| **[OTHER VENDOR — ADD]** | **[DESCRIBE]** | **[YES/NO]** | **[YES/NO]** | `NOT STARTED` | — | `[TO ASSIGN]` |

## Vendor-specific notes

### Microsoft Azure (all services above)
Microsoft offers a **BAA via the Microsoft Product Terms / Online Services Data Protection Addendum (DPA)** covering in-scope Azure services. For most Microsoft commercial (EA/MCA/CSP) and volume-licensing customers the BAA is incorporated automatically; verify it is in effect for the tenant and that **every Azure service actually used** appears on Microsoft's list of BAA-covered services. Record the DPA/BAA effective date and the coverage confirmation.

### Email / SMTP sender
The platform has an Email sender component. Whether a BAA is needed depends entirely on **what the emails contain**. If any email body, subject, or attachment can include PHI, a BAA with the email provider is required (SendGrid/Twilio, AWS SES, and Microsoft 365 all offer BAAs to eligible customers). The safer design is to keep PHI out of email entirely (send links/notifications only); if that is enforced and documented, the BAA may not be required — record that decision here.

### Monitoring / APM (OpenTelemetry → Azure Monitor / Application Insights)
Telemetry frequently leaks PHI through URLs, query strings, exception messages, request/response bodies, and custom log fields. Assume ePHI may reach the monitoring backend unless active scrubbing/redaction is implemented and verified. Because Application Insights is an Azure service, its BAA coverage rides on the Azure DPA above — confirm it is in the covered-services list.

### Managed logging (Seq)
Seq is currently used **in development**. Dev environments should never contain production ePHI. If Seq (or any log sink) is promoted to an environment that handles real ePHI, a BAA with the provider becomes required and this row must be updated. Prefer log scrubbing/redaction of ePHI regardless of environment.

## Cloud IaaS shared-responsibility guidance (reference rows, not vendors)

Signing a cloud provider's BAA does **not** make the deployment HIPAA-compliant by itself. Under the shared-responsibility model the provider secures the *cloud* while FHIRBridge secures what it runs *in* the cloud. Confirm each of the following is owned and evidenced:

| Responsibility | Typically the cloud provider | Typically FHIRBridge |
|----------------|:---------------------------:|:--------------------:|
| Physical datacenter security | ✔ | |
| Host/hypervisor & platform patching (PaaS) | ✔ | |
| Guest OS / runtime patching (IaaS/containers) | | ✔ |
| Network segmentation, firewall/NSG rules | shared | ✔ |
| Encryption **enabled & configured** (TLS in transit, TDE/CMK at rest) | provides capability | ✔ (must enable) |
| Key management & rotation | provides Key Vault | ✔ (must configure) |
| Identity, access control (RBAC), least privilege | provides IAM | ✔ (must configure) |
| Application-level audit logging of ePHI access | | ✔ |
| Backup/DR configuration & testing | provides tools | ✔ (see backup-DR runbook) |
| Log/telemetry PHI scrubbing | | ✔ |

## Maintenance

Review this register at least **quarterly** and whenever a subprocessor is added, removed, or changed. See [`baa-tracking-checklist.md`](./baa-tracking-checklist.md) for the operating procedure.
