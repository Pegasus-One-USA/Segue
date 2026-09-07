#!/usr/bin/env bash
# Wraps the official postgres:16-alpine entrypoint (docker-entrypoint.sh) with a copy-in/copy-out
# backup step around PGDATA, which stays on the container's own local (ephemeral) disk — see this
# directory's Dockerfile for why: Postgres's own startup permission check can never pass directly
# on Azure Files (SMB), so PGDATA can't live there. Azure Files is instead mounted at PG_BACKUP_DIR
# and used purely as an at-rest snapshot target, restored into local storage on start and saved
# back out on a graceful stop.
#
# Durability tradeoff (accepted here for a demo/test environment specifically — see this image's
# own Dockerfile comment): the backup only happens on a GRACEFUL shutdown (this script traps
# SIGTERM, stops postgres cleanly, then copies PGDATA out). Any restart that skips that — a crash,
# a forcibly-killed container, Container Apps exceeding its termination grace period — loses every
# write since the last graceful shutdown. There is no continuous replication or WAL archiving here;
# this is a full-directory snapshot copy on stop/start, not a substitute for real backup tooling on
# anything that matters.
#
# PG_BACKUP_DIR unset or empty disables backup/restore entirely (falls back to plain ephemeral
# Postgres — data lost on every restart, no exceptions) rather than failing outright, so this image
# still works as a drop-in for non-persistent uses (e.g. a throwaway CI/test instance).
#
# Deliberately `cp -r`, not `cp -a`/`cp --preserve`: Azure Files (SMB) can't honor preserved
# ownership/permission bits on the way out any more than it can honor a direct chmod (see the
# Dockerfile comment) — attempting to preserve them would just replace one permission failure with
# another. Ownership doesn't need to survive the round trip anyway: docker-entrypoint.sh always
# chowns/chmods PGDATA itself (running as root) before re-executing as the postgres user, whether
# this is a fresh initdb or a restored directory.
set -e

PG_BACKUP_DIR="${PG_BACKUP_DIR:-}"
PGDATA="${PGDATA:-/var/lib/postgresql/data}"

if [ -n "$PG_BACKUP_DIR" ] && [ -f "$PG_BACKUP_DIR/PG_VERSION" ]; then
  echo "==> fhirbridge-postgres: restoring PGDATA from $PG_BACKUP_DIR (found an existing backup)"
  mkdir -p "$PGDATA"
  cp -r "$PG_BACKUP_DIR/." "$PGDATA/"
elif [ -n "$PG_BACKUP_DIR" ]; then
  echo "==> fhirbridge-postgres: no existing backup at $PG_BACKUP_DIR - starting fresh (initdb will run)"
else
  echo "==> fhirbridge-postgres: PG_BACKUP_DIR not set - running fully ephemeral, no backup/restore"
fi

backup_out() {
  if [ -n "$PG_BACKUP_DIR" ]; then
    echo "==> fhirbridge-postgres: backing up PGDATA to $PG_BACKUP_DIR"
    mkdir -p "$PG_BACKUP_DIR"
    # Non-fatal: a failed backup shouldn't prevent the container from exiting cleanly on shutdown.
    cp -r "$PGDATA/." "$PG_BACKUP_DIR/" || echo "==> fhirbridge-postgres: WARNING - backup copy failed, continuing shutdown anyway"
  fi
}

# Backgrounds the official image's own entrypoint (not `exec`) so this script stays alive to catch
# the termination signal below and run the backup — `exec`ing it directly would replace this
# process entirely, leaving no chance to run cleanup code afterward.
docker-entrypoint.sh "$@" &
PG_PID=$!

terminate() {
  echo "==> fhirbridge-postgres: caught termination signal - stopping postgres gracefully first"
  kill -TERM "$PG_PID" 2>/dev/null || true
  wait "$PG_PID" 2>/dev/null || true
  backup_out
  exit 0
}
trap terminate TERM INT

wait "$PG_PID"
EXIT_CODE=$?
# Covers postgres exiting on its own (not just an external signal) - still worth preserving
# whatever made it to disk rather than only backing up on the external-signal path above.
backup_out
exit $EXIT_CODE
