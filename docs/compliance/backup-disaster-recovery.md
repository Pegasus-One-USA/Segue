# Backup & Disaster Recovery Runbook — FHIRBridge

**Scope.** This runbook defines backup and disaster-recovery (DR) requirements for the FHIRBridge platform (.NET 9, SQL Server, Azure Key Vault, docker-compose). These are **recommendations that require infrastructure implementation** (Azure Backup, SQL Agent jobs, geo-redundant storage, volume backups) — they are **not** implemented in application code. Targets marked as recommended **must be ratified by the business** and formalized in the organization's contingency plan (HIPAA §164.308(a)(7)).

---

## 1. Data that must be protected

| Asset | What it is | Impact if lost |
|---|---|---|
| **SQL Server `FHIRBridge` database** | Primary operational data store: tenant/org config, mapping profiles, pipeline routes, users/RBAC, pipeline run history. | Total service data loss. |
| **Append-only audit tables** (`UserActivityAuditLog`, lineage/data-access logs) | Hash-chained, tamper-evident audit trail. Subject to the **7-year retention** requirement. | Loss of compliance-mandated audit evidence; broken hash-chain continuity. |
| **Data Protection key ring** (`DataProtection:KeyRingPath` volume) | ASP.NET Core Data-Protection keys that encrypt OAuth/SMART launch tokens and other protected payloads. | **Critical:** after a restore without these keys, all previously encrypted OAuth/launch tokens become **undecryptable**. This volume **must** be included in every backup set. |
| **Azure Key Vault secrets** | Connection strings, signing keys, client secrets, webhook HMAC secrets, downstream credentials. | Loss of secrets; inability to reconnect sources/destinations. |
| **Blob / object storage destinations** | Data written to configured object-storage destinations (e.g., exported FHIR bundles/files). | Loss of delivered/exported healthcare data. |

> **Do not overlook the Data Protection key ring.** Backing up only the SQL database will produce a restore in which stored OAuth/launch tokens cannot be decrypted. The `DataProtection:KeyRingPath` volume must be captured in the same backup window as the database.

---

## 2. Recommended RPO / RTO (must be ratified by the business)

For a healthcare integration platform handling PHI, the following are reasonable starting targets. **These require business ratification and may be tightened by contractual SLAs.**

| Objective | Recommended target | Notes |
|---|---|---|
| **RPO** (max acceptable data loss) | **≤ 1 hour** | Achievable with transaction-log backups every 15 minutes (or Azure SQL PITR). |
| **RTO** (max acceptable downtime) | **≤ 4 hours** | Time to restore service to a working state in the DR region. |
| **Audit-data RPO** | **≤ 15 minutes** | Audit continuity is compliance-critical; prioritize log-backup frequency. |

---

## 3. SQL Server backup strategy

Choose the model that matches the deployment:

**Option A — Self-managed SQL Server (SQL Agent jobs):**
- **Full backup:** daily.
- **Differential backup:** every 6–12 hours.
- **Transaction-log backup:** every **15 minutes** (requires the FULL recovery model) — this is what delivers the ≤ 1h RPO.
- Verify each backup with `RESTORE VERIFYONLY` and periodic `CHECKSUM` validation.

**Option B — Azure SQL Database:**
- Rely on **automated backups + Point-In-Time Restore (PITR)** and configure **Long-Term Retention (LTR)** to meet the 7-year requirement.
- Enable **geo-redundant** backup storage.

**Cross-cutting requirements (both options):**
- **Geo-redundant off-site copies:** replicate backups to a second Azure region (or an independent off-site location). No single-region single-point-of-failure.
- **Backup encryption:** encrypt all backups at rest (SQL Server backup encryption or storage-account encryption). TDE protects the live DB but backups must be independently encrypted/verified.
- **Immutability for audit backups:** store audit-table backups on immutable/WORM storage where possible to reinforce the append-only guarantee.

---

## 4. Data Protection key-ring backup

- Include the **`DataProtection:KeyRingPath`** volume in every backup set, on the same schedule as the database.
- Encrypt the key-ring backup at rest and restrict access to break-glass operators only.
- Test that a restored key ring can decrypt a token created before the backup (see restore procedure step 6).

---

## 5. Azure Key Vault & object-storage backups

- **Key Vault:** enable **soft-delete** and **purge protection**; document the recovery procedure. Export/record secret inventory (names/versions, not values) so a rebuild is auditable.
- **Object-storage destinations:** enable versioning and geo-redundant (GRS/RA-GRS) replication; apply lifecycle/retention aligned to the 7-year requirement for any PHI-bearing outputs.

---

## 6. Restore / DR procedure (outline)

Perform in the DR region. Assign a DR lead and document start/end times for RTO measurement.

1. **Declare DR** and notify stakeholders per the incident-response policy.
2. **Provision infrastructure** — stand up the docker-compose stack (or equivalent) in the DR region.
3. **Restore Key Vault access** — recover/reconnect Azure Key Vault; confirm the app can read secrets.
4. **Restore the Data Protection key ring** — mount the restored `DataProtection:KeyRingPath` volume **before** starting the app.
5. **Restore the SQL database** — restore latest full + differential + transaction logs (or PITR to just before the incident). Run `RESTORE VERIFYONLY` / integrity checks.
6. **Validate encryption & tokens** — confirm TDE is enabled (startup TDE health check passes) and that a previously encrypted OAuth/launch token decrypts (proves key-ring restore succeeded).
7. **Validate audit integrity** — verify the `UserActivityAuditLog` hash chain is intact from the restore point.
8. **Restore object-storage destinations** as needed.
9. **Smoke test** — authenticate, run a sample pipeline, confirm source/destination connectivity.
10. **Cut over traffic / DNS**, monitor, and record RTO/RPO actually achieved.
11. **Post-incident review** — document gaps and update this runbook.

---

## 7. DR testing cadence

- **Full restore/DR drill: at least annually** (recommend semi-annually for a PHI platform). Restore to an isolated environment and execute the procedure above end-to-end.
- **Backup restore spot-checks: quarterly** — restore a recent backup and run `RESTORE VERIFYONLY` plus a token-decrypt check.
- Retain evidence (dates, participants, results, RTO/RPO achieved, remediation items) for the audit period.

---

## 8. Backup retention

- Retain backups sufficient to satisfy the **7-year retention** requirement, consistent with the application's retention engine and immutable audit trail.
- Retain **audit-table backups for the full 7 years** on immutable storage.
- Define shorter operational-restore tiers (e.g., 35 days of PITR/daily granularity) layered on top of long-term (7-year) archival, so routine restores are fast while long-term compliance retention is preserved.
- Document a defensible, logged disposal process at end-of-retention (see `security-policies-templates.md` — Data Retention & Disposal Policy).

---

> **Implementation note.** Everything in this runbook is delivered through **infrastructure** (Azure Backup, SQL Agent jobs, storage replication, volume snapshots), not FHIRBridge application code. The application contributes the 7-year retention engine, TDE startup health check, hash-chained audit trail, and Data-Protection encryption; the ops team must build and operate the backup/DR mechanisms that protect and recover those assets.
