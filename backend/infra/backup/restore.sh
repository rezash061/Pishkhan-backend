#!/usr/bin/env bash
# Restores a dump made by backup.sh into a NEW database (BE-1.15, runbook docs/runbooks/backup-restore.md).
#
#   PGHOST=... PGUSER=... PGPASSWORD=... restore.sh /var/backups/pishkhan/pishkhan-20261101T000000Z.dump pishkhan_restore
#
# Checks the .sha256 when present, refuses a target that already exists (restore next to production, check, then switch the
# connection string; never restore over the live database), restores with --exit-on-error and prints a summary to compare with the
# source: migrations applied and rows per schema.
set -euo pipefail

dump="$1"
target="$2"

if [[ -f "$dump.sha256" ]]; then
  (cd "$(dirname "$dump")" && sha256sum --check --quiet "$(basename "$dump").sha256")
fi

if [[ -n "$(psql --dbname=postgres --tuples-only --no-align --command="SELECT 1 FROM pg_database WHERE datname = '$target'")" ]]; then
  echo "database $target already exists; restore into a new name" >&2
  exit 2
fi

createdb "$target"
pg_restore --no-owner --no-privileges --exit-on-error --jobs="${JOBS:-2}" --dbname="$target" "$dump"
psql --dbname="$target" --no-psqlrc --quiet --file="$(dirname "$0")/verify.sql"
