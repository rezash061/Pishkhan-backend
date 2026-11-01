# Backup and restore

What is backed up, how, and how to restore. `BackupRestoreTests` runs the same scripts against the integration test database on every CI run: it dumps while other tests are writing, restores into a new database, and compares tables, columns, indexes, extensions, migrations and data.

## What needs a backup

| Store | Backed up? | How | Why |
|---|---|---|---|
| PostgreSQL | **Yes** | `infra/backup/backup.sh`, nightly, plus continuous WAL archiving where the platform offers it | Everything durable: workspaces, inbox, catalog, CRM, knowledge, billing, audit |
| Object storage (`files` bucket) | **Yes** | Bucket versioning, plus a nightly mirror to another zone | Product images and uploads. Rows in `files.files` point at these objects. |
| Object storage (`quarantine`) | No | | Files waiting to be scanned. They are re-uploaded if lost. |
| Redis `state` | No (AOF on, for restarts) | | OTPs, rate-limit windows, locks, idempotency keys, demo chat sessions. All short-lived: losing them means users sign in again. |
| Redis `cache` | No | | Rebuilt from Postgres |
| Kafka | No | | Events are in Kafka at most 2 weeks, and the source of truth is Postgres. The outbox keeps what was not yet produced. |

## Nightly backup

```bash
PGHOST=db.internal PGUSER=backup PGPASSWORD=... PGDATABASE=pishkhan KEEP_DAYS=14 \
  infra/backup/backup.sh /var/backups/pishkhan
```

- The script writes `pishkhan-<UTC stamp>.dump`, a pg_dump custom-format file. It is one consistent snapshot, so it is safe while the app is running.
- It also writes a `.sha256` next to the dump and checks that the dump can be read back before keeping it.
- It deletes dumps older than `KEEP_DAYS`.
- Copy the directory to object storage **in another zone**. A backup on the database's own disk does not count.
- The backup role needs `pg_read_all_data`. The dump has no owners or grants (`--no-owner --no-privileges`). Restore as the role the app connects with, so the restored objects belong to it.

## Restore

Restore **next to** production, check the copy, then switch the connection string. Never restore over the live database.

```bash
PGHOST=db.internal PGUSER=pishkhan PGPASSWORD=... \
  infra/backup/restore.sh /var/backups/pishkhan/pishkhan-20261101T000000Z.dump pishkhan_restore
```

1. Stop the writers so nothing lands in the old database during the switch: scale `Api`, `AdminApi` and every `Workers` group to 0. Ingress can stay up, because Kafka buffers webhooks for 2 weeks.
2. Run `restore.sh`. It checks the checksum and refuses an existing target. It restores with `--exit-on-error` and prints the exact rows per table (`verify.sql`), so you can compare with what you expect.
3. Run the Migrator against the restored database. It applies any migration newer than the dump.
4. Point `ConnectionStrings:Postgres` at the restored database and scale the hosts back up.
5. Afterwards:
   - **Outbox.** Unpublished outbox rows in the dump are produced again. Consumers dedupe by message id, so this is safe.
   - **Redis state.** Clear it if the restore went back in time (`FLUSHDB` on the state instance). Otherwise idempotency keys and locks from after the dump point at rows that no longer exist. Users sign in again.
   - **Search.** Knowledge chunks and their embeddings are in Postgres, so nothing needs re-indexing.
   - **Missing data.** Anything written after the dump is gone. Tell affected sellers, using the audit log for the window.

## Drill

Every quarter, and before the closed beta opens:

1. Restore the latest production dump into a scratch database with `restore.sh`.
2. Compare its `verify.sql` output with production's for the tables that only grow (workspaces, messages, audit).
3. Point a staging Api at it and open one workspace's inbox.
4. Write down how long the restore took (the RTO) and how old the dump was (the RPO), in this file's history.
