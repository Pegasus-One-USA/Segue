# Upgrading Segue

For operators upgrading a Segue deployment running in their own Azure subscription.

## Before you start

**Back up your database.** Database migrations run automatically when the new version starts, and
some cannot be undone. This is the one step you should never skip — a failed migration is recovered
from your backup, and nothing else.

- **Azure Database for PostgreSQL** — take an on-demand backup, or note that point-in-time restore
  covers you, before proceeding.
- **Containerized PostgreSQL** — the `segue-postgres-backup` job writes `pg_dump` output to Blob
  Storage. Confirm a recent backup exists and that you can read it.

Then check what you are running today: click the version in the portal footer. Note the version and
commit — you will need them if you have to roll back or open a support ticket.

## Supported upgrade paths

| From | To | Supported |
|---|---|---|
| 1.1.0 | 1.1.x (patch) | Yes — direct |
| 1.1.x | 1.2.0 (minor) | Yes — direct |
| 1.x | 2.0.0 (major) | Upgrade to the latest 1.x first, then to 2.0.0 |

Within a major version you may skip intermediate versions — 1.1.0 straight to 1.4.0 is fine.
**Crossing a major version, do not skip**: go to the newest release of your current major first.
Major versions are where manual steps live, and each one's release notes assume you arrived from
the version immediately before it.

## Upgrading

Re-deploy the same ARM template with the new version's `imageTag`. Azure updates the container apps
in place; your data, configuration and secrets are untouched.

1. Open the new version's release page and take its `main.json` and `createUiDefinition.json`.
2. Deploy to **the same resource group** as your existing deployment.
3. Supply **the same parameter values you used originally** — same name prefix, same database, same
   registry. The only value that changes is `imageTag`, which the new template already defaults to
   its own version.
4. Wait for the deployment to report success.

Migrations run automatically as the new containers start. A large schema change can add a minute or
two to first startup.

## Version-specific notes

### OAuth redirect URLs now use a fixed configured domain, not the request's Host header

This release stops deriving `redirect_uri` (and the launch/authorize/standalone URLs) from the
incoming request's `Host`/`Scheme` — a value that a WAF or reverse proxy in front of the app can
alter before it ever reaches the container. It now reads a fixed **OAuth:PublicBaseUrl** setting
instead (Settings → System Settings → OAuth in the portal), falling back to the request's own
Host/Scheme only when that setting is blank.

**If you deploy behind a custom domain (Front Door/WAF), you must set this before your EHR
integrations work again:**

1. If you deploy this release by **re-running the ARM template** (`main.bicep`/`main.json`) with
   `segueAppCustomDomain` already set to your domain, this happens automatically — the template
   seeds `OAuth:PublicBaseUrl` to `https://<your custom domain>` on first startup.
2. If you instead **swap only the container image** (without re-running the template — e.g. `az
   containerapp update --image ...`), the setting is **not** set, and it seeds blank on that first
   startup. Because the seeder only ever inserts a setting that doesn't already exist, a *later*
   template redeploy will **not** retroactively fill it in — you must set it yourself, once, from
   Settings → System Settings → OAuth → `OAuth:PublicBaseUrl` (enter `https://<your custom domain>`,
   no path).
3. Deploying with **no custom domain configured** (direct-to-container access, no WAF/Front Door)
   needs no action — the request-derived fallback is already correct in that case.

Verify by triggering an EHR OAuth launch after upgrading and confirming the authorize request's
`redirect_uri` reads your real domain, not `127.0.0.1:5000` or the container's own
`*.azurecontainerapps.io` address.

### Execution history is purged and PHI columns are removed

This release stops storing patient data in execution and lineage history. Previous versions kept
whole fetched FHIR resources, mapped field values, and per-hop before/after values in the database
(encrypted at rest, but retained and decryptable). Those columns are dropped.

**Two things to know before you upgrade:**

1. **Existing execution history is deleted, not migrated.** The `FieldLineageEntries`,
   `WorkflowNodeRunPayloads` and `PipelineRunResourceRecords` tables are truncated. This is
   observability data rather than a system of record — your workflow configuration, destinations and
   written data are untouched — but past-run detail does not survive the upgrade. Export anything you
   need for an audit **before** upgrading.

2. **Your backups and WAL archives still contain the old values.** The migration clears the live
   database and rewrites the tables so the dropped data is not recoverable from the heap. It cannot
   reach anything outside that database. Backups taken before this upgrade, point-in-time-restore
   windows, WAL/transaction-log archives, and any read replicas all still hold the PHI.

   If your reason for upgrading is to stop retaining PHI, treat expiring those as part of the
   upgrade, not a follow-up: rotate or delete pre-upgrade backups once you are satisfied the new
   version is healthy, and shorten the retention window if it is longer than you need. Keep at least
   one backup until then — see "Before you start".

The migration runs `VACUUM FULL` on the three tables, which takes an exclusive lock for its duration.
On a deployment with a large execution history this can add noticeable time to first startup.

## Verifying

1. Open the portal and click the version in the footer. It should show the new version for both
   **Portal** and **API**.
2. If the two disagree, do a hard reload (Ctrl+Shift+R) — a cached bundle is the usual cause. If
   they still disagree, the upgrade did not complete; see below.
3. Run one pipeline and confirm it completes.

## Rolling back

Container Apps keeps the previous revision, so rollback is fast:

- In the Azure portal, open the container app → **Revisions** → activate the previous revision.
- Or re-deploy the template with the previous `imageTag`.

**Rollback restores the application, not the database.** Within a major version this is safe:
migrations are additive, so the older application runs against the newer schema without trouble.
**Across a major version it is not** — restore your database backup as well.

## If something goes wrong

Collect these before opening a ticket:

- The version and commit from the footer panel (both Portal and API).
- The version you upgraded **from**.
- Container app logs covering startup, and the Azure deployment's error if it failed.

Common cases:

| Symptom | Cause | What to do |
|---|---|---|
| Portal and API versions differ | Cached browser bundle | Hard reload. If it persists, one container app did not update — redeploy. |
| Containers restart repeatedly after upgrade | A migration failed | Check startup logs for the failing migration. Roll back and restore the backup. |
| Deployment fails on an existing resource | A parameter changed that Azure cannot alter after creation | Redeploy with your original parameter values; only `imageTag` should change. |
