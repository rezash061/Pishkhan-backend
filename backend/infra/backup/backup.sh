#!/usr/bin/env bash
# Logical backup of the Pishkhan database (BE-1.15, runbook docs/runbooks/backup-restore.md).
#
#   PGHOST=... PGPORT=... PGUSER=... PGPASSWORD=... PGDATABASE=pishkhan backup.sh /var/backups/pishkhan
#
# Writes <db>-<UTC stamp>.dump (pg_dump custom format, one consistent snapshot) and a .sha256 next to it, checks that the dump can be
# read back, and deletes dumps older than KEEP_DAYS (default 14). Prints the dump's path. Copy the directory off the database host
# (object storage in another zone) after it runs; a backup on the same disk is not a backup.
set -euo pipefail

out="${1:-./backups}"
db="${PGDATABASE:-pishkhan}"
keep_days="${KEEP_DAYS:-14}"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
file="$out/$db-$stamp.dump"

mkdir -p "$out"
pg_dump --format=custom --compress=6 --no-owner --no-privileges --dbname="$db" --file="$file.partial"

# A dump whose table of contents cannot be read is corrupt; never let it replace a good one.
pg_restore --list "$file.partial" > /dev/null
mv "$file.partial" "$file"
(cd "$out" && sha256sum "$(basename "$file")" > "$(basename "$file").sha256")

find "$out" -maxdepth 1 -name "$db-*.dump*" -mtime +"$keep_days" -delete
echo "$file"
