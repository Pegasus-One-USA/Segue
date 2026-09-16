#!/usr/bin/env bash
# One-shot pg_dump + upload to Blob Storage, run on a schedule by the postgresBackupJob Container
# Apps Job (see main.bicep) — the fallback backup control for clients who stay on the containerized
# Postgres path instead of useAzurePostgresql=true. See this repo's backup plan for why: it's a
# periodic snapshot (however often the job's cron fires, hourly by default), not continuous
# point-in-time recovery — restoring means downloading the newest dump and running `gunzip | psql`
# against a fresh database, not a one-click Azure restore the way Flexible Server's backups are.
set -euo pipefail

: "${POSTGRES_HOST:?POSTGRES_HOST is required}"
: "${POSTGRES_PORT:=5432}"
: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${POSTGRES_DB:?POSTGRES_DB is required}"
: "${POSTGRES_PASSWORD:?POSTGRES_PASSWORD is required}"
: "${BACKUP_CONTAINER_SAS_URL:?BACKUP_CONTAINER_SAS_URL is required}"

TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
DUMP_FILE="/tmp/${POSTGRES_DB}-${TIMESTAMP}.sql.gz"

echo "==> fhirbridge-postgres-backup: dumping ${POSTGRES_DB} from ${POSTGRES_HOST}:${POSTGRES_PORT}"
PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=plain | gzip > "$DUMP_FILE"

# BACKUP_CONTAINER_SAS_URL is "https://<account>.blob.core.windows.net/<container>?<sas-query>" —
# the destination blob name has to be inserted before the '?', not appended after it.
BASE_URL="${BACKUP_CONTAINER_SAS_URL%%\?*}"
SAS_QUERY="${BACKUP_CONTAINER_SAS_URL#*\?}"
DEST_URL="${BASE_URL}/$(basename "$DUMP_FILE")?${SAS_QUERY}"

echo "==> fhirbridge-postgres-backup: uploading $(basename "$DUMP_FILE") ($(du -h "$DUMP_FILE" | cut -f1))"
azcopy copy "$DUMP_FILE" "$DEST_URL" --output-level=essential

rm -f "$DUMP_FILE"
echo "==> fhirbridge-postgres-backup: done"
