# Asset Inventory — FHIRBridge Security Risk Analysis

**Supports:** HIPAA §164.308(a)(1)(ii)(A) Risk Analysis; NIST SP 800-30 Step 1 (system characterization / scope).
**Parent document:** `README.md`

This inventory enumerates the information assets and systems that store, process, or transmit ePHI within the FHIRBridge platform, plus supporting components in the trust boundary. It is the basis for the threat/vulnerability register (`threat-vulnerability-register.md`). Every asset must have an assigned owner; placeholders are marked `[TO ASSIGN]`, and hosting/location details to be confirmed by the organization are marked `[TO CONFIRM]`.

## Data classification scheme

- **Restricted (ePHI)** — electronic protected health information; highest protection; encryption, access control, and audit required.
- **Confidential** — secrets, credentials, keys, and security-relevant configuration; disclosure enables compromise of ePHI.
- **Internal** — operational configuration and metadata with no ePHI.
- **Public** — no sensitivity.

## Asset table

| # | Asset / System | Description & role | Data classification | Handles ePHI (Y/N) | Owner | Hosting / location |
|---|---|---|---|---|---|---|
| A1 | **API host (ASP.NET Core)** | Authenticates users, enforces RBAC, exposes FHIR/HL7 ingestion and patient-read endpoints; entry point to the platform. | Restricted (ePHI) | Y — ePHI in transit through ingestion/read paths | [TO ASSIGN] | [TO CONFIRM — docker-compose container; candidate Azure App Service / AKS] |
| A2 | **Background Worker** | Executes ingestion pipelines: normalization, de-identification (Safe Harbor + k-anonymity), field mapping, and destination writes. | Restricted (ePHI) | Y — processes ePHI in memory during pipeline runs | [TO ASSIGN] | [TO CONFIRM — docker-compose container; candidate AKS] |
| A3 | **SQL Server database** | Persists platform configuration, mapping profiles, pipeline definitions, RBAC data, the audit store, and any persisted ePHI/lineage. TDE enabled at the database layer. | Restricted (ePHI) | Y | [TO ASSIGN] | [TO CONFIRM — docker-compose container / mounted volume; candidate Azure SQL / SQL MI] |
| A4 | **Azure Key Vault** | Custody of application secrets, connection strings, OAuth/SMART client secrets, and cryptographic material. | Confidential | N (holds keys/secrets, not ePHI) | [TO ASSIGN] | [TO CONFIRM — Azure Key Vault instance + region] |
| A5 | **Blob / object storage (destination)** | Destination target for exported/transformed data (files, blobs). May contain ePHI or de-identified data depending on pipeline configuration. | Restricted (ePHI) | Y — when a pipeline writes identifiable output here | [TO ASSIGN] | [TO CONFIRM — Azure Blob / S3-compatible bucket + region] |
| A6 | **EHR source connections** | Outbound integrations to Epic, Healow, MEDITECH, and generic FHIR R4 / HL7 v2 sources. ePHI enters the platform over these links (HTTPS + SMART/OAuth or HMAC-authenticated webhooks). | Restricted (ePHI) | Y — ePHI in transit inbound | [TO ASSIGN] | [TO CONFIRM — external EHR endpoints; governed by BAA / interconnection agreement] |
| A7 | **Audit store (hash-chained UserActivityAuditLog)** | Append-only, hash-chained record of user and data-access activity; tamper-evident security-event log. Stored PHI-free. | Confidential (security records) | N — recorded PHI-free by design | [TO ASSIGN] | [TO CONFIRM — resides in SQL Server (A3)] |
| A8 | **Backups** | Backups of the SQL database, Data-Protection key ring, and configuration required for restore/DR. Inherit the classification of their source data. | Restricted (ePHI) | Y — DB backups contain ePHI | [TO ASSIGN] | [TO CONFIRM — backup destination + region; DR/backup infra not yet implemented] |
| A9 | **Angular portal** | Administrative / configuration UI for managing sources, mappings, users, and pipelines. Displays configuration and may render patient/read data to authorized operators. | Restricted (ePHI) | Y — may display ePHI to authorized users | [TO ASSIGN] | [TO CONFIRM — static hosting / container served behind API] |
| A10 | **Secrets & cryptographic keys** | Passwords (PBKDF2-SHA256 hashes), JWT signing keys, Data-Protection key ring, HMAC webhook secrets, SMART/OAuth client secrets. | Confidential | N (protect ePHI indirectly) | [TO ASSIGN] | [TO CONFIRM — Azure Key Vault (A4) + DP key-ring storage] |
| A11 | **Application/diagnostic logs (Serilog)** | Structured operational logs. PHI-masked via `PhiMaskingEnricher`; classified Internal on the assumption masking holds. | Internal (Confidential if masking fails) | N — PHI masked by design | [TO ASSIGN] | [TO CONFIRM — log sink / aggregation target] |
| A12 | **Identity provider — Microsoft Entra ID** | Federated SSO for user authentication (subprocessor for identity). | Confidential | N (authenticates access to ePHI) | [TO ASSIGN] | Microsoft Entra ID (Azure AD) tenant [TO CONFIRM] |
| A13 | **Container host / orchestration platform** | The docker-compose host (and candidate Azure/Kubernetes platform) on which A1–A3 run; underlies all in-boundary compute. | Confidential | N (hosts ePHI-processing components) | [TO ASSIGN] | [TO CONFIRM — host OS, patch responsibility, network segmentation] |

## Data flow summary (for context)

1. **Ingest** — ePHI enters via EHR source connections (A6) over HTTPS/SMART-OAuth or HMAC-authenticated webhooks into the API host (A1).
2. **Process** — the Worker (A2) normalizes, optionally de-identifies (Safe Harbor + k-anonymity), and field-maps the data; secrets pulled from Key Vault (A4).
3. **Persist / emit** — output is written to SQL Server (A3), blob/object storage (A5), files, BI tools, or FHIR repositories per pipeline configuration.
4. **Observe** — user and data-access activity is recorded in the hash-chained audit store (A7); operational events go to PHI-masked logs (A11).
5. **Administer** — operators configure and monitor via the Angular portal (A9), authenticated by Entra ID (A12) or local accounts.

> **Note.** Destinations that receive identifiable ePHI (SQL Server, blob storage, files, downstream FHIR repos, BI tools) require a signed Business Associate Agreement with the operating party where that party is a separate legal entity. See the BAA checklist in `../security-policies-templates.md`.
