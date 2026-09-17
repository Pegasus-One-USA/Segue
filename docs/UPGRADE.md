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
