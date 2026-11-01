# ClamAV down

## How it shows

- `/health/ready` reports `clamav` as **Degraded**. The hosts stay in service on purpose.
- Uploads (product images, import sheets, knowledge files) stay in `pending_scan`, and the panel shows them as "being checked".
- Log: `ClamAV unreachable; file {FileId} stays pending_scan` from the `media` group.

## What the system already does

- **It fails closed.** Nothing is used, sent to Instagram or indexed until it is scanned clean and re-encoded. A file waiting for ClamAV is not available, which is safer than trusting it.
- **Unreachable ClamAV is not an error.** The file stays `pending_scan`, and a timer queues it again every minute (dedupe key `files.rescan:{fileId}`) until ClamAV answers. Nothing needs to be re-uploaded.
- **Scan errors** (ClamAV answers, but with an error) are retried 5 times with backoff from 1 s to 30 s. After that the file is `scan_failed` and the seller sees "could not be checked; upload again".

## What to do

1. Check the `clamd` deployment (2+ replicas behind the TCP service, backend plan 8.2). Look for pods crash-looping on memory: `clamd` needs 2 GB or more to load the signatures.
2. Check `freshclam`. Production uses the **private signature mirror**, because the official CDN blocks Iran. If the mirror is unreachable, `clamd` keeps the last signatures and keeps scanning. Old signatures are a warning, not an outage. If `clamd` cannot start because the signature database is missing or corrupt, restore it from the mirror's last good copy.
3. When `clamd` answers again, the pending files are scanned within about a minute. Watch `pishkhan.av.scan.results` rise and the pending count fall:
   ```sql
   SELECT status, count(*) FROM files.files WHERE created_at > now() - interval '1 day' GROUP BY status;
   ```
4. Files that reached `scan_failed` during the incident (only on ClamAV *errors*, not on downtime) are not retried automatically. Ask the affected sellers to upload them again. There is no admin re-scan action yet: it is a follow-up.

## Do not

- Do not bypass the scanner (no config flag does this in production, and none should be added).
- Do not mark files `clean` in the database. Clean files are re-encoded and copied out of quarantine by the scan handler, so a row changed by hand points at an object that does not exist.
