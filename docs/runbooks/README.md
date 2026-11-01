# On-call runbooks

Closed beta (BE-1.15). Each runbook says how to tell what is wrong, what the system already does about it on its own, and what on-call does. Read the "already does" part first: most of these failures are designed to wait, not to lose data.

| Runbook | Alert or symptom |
|---|---|
| [Meta outage](meta-outage.md) | Instagram API error spike, sends failing, webhooks stopped, many channels `reauth_required` |
| [Kafka lag](kafka-lag.md) | Consumer lag > 30 s, outbox backlog > 1,000, DLQ growing, replies slow |
| [ClamAV down](clamav-down.md) | `/health/ready` shows `clamav` Degraded, uploads stuck in `pending_scan` |
| [Backup and restore](backup-restore.md) | Nightly backup, restore drill, data loss or corruption |

Conventions:

- Hosts: `Api` (panel and public API, SignalR; see [deployment notes](../deployment.md) for the same-origin hub), `Ingress` (Meta webhooks), `Workers` (one deployment per group: `ingest`, `engine`, `ai`, `outbound`, `media`, `platform`), `AdminApi`, `Migrator`.
- Health: `GET /health/live` (process) and `GET /health/ready` (Postgres, both Redis instances, Kafka, ClamAV; ClamAV and the cache Redis report Degraded, not Unhealthy).
- Metrics are in the `Pishkhan` meter: `pishkhan.instagram.api.errors`, `pishkhan.messaging.dead_lettered`, `pishkhan.outbox.relayed`, `pishkhan.av.scan.results`, `pishkhan.timers.fired`.
- Tell sellers about anything they can see through the status banner and an in-app notification. Never message customers on a seller's behalf.
