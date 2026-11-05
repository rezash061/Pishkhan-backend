# Disaster recovery

BE-2.11. What to do when a whole part of the platform is lost, not just degraded. Read [backup and restore](backup-restore.md) first: this
runbook builds on its scripts. Day-to-day failures (Meta, Kafka lag, ClamAV) have their own runbooks.

## Targets

| | Target | Why it is reachable |
|---|---|---|
| **RPO** (data lost) | ≤ 15 minutes with WAL archiving, ≤ 24 hours from the nightly dump alone | WAL is shipped to another zone continuously; the nightly dump is the fallback |
| **RTO** (back to answering) | ≤ 4 hours for a full rebuild, ≤ 1 hour for a database restore | The cluster is code (Helm chart `backend/deploy/helm/pishkhan`), images are in Harbor, and Ingress keeps taking webhooks into Kafka meanwhile |

Webhooks are the one thing that cannot be replayed from our side: Meta retries for hours and then gives up. Keep **Ingress and Kafka
up first** in every scenario below; they hold up to two weeks of customer messages while everything behind them is rebuilt.

## Scenarios

| Lost | Impact | Recovery |
|---|---|---|
| One application pod or node | None visible (PDB, HPA, topology spread) | Nothing; Kubernetes reschedules |
| A whole application namespace or cluster | Panel and replies down; webhooks queue only if Ingress is elsewhere | **Rebuild** below |
| PostgreSQL primary | Everything but Ingress stops writing | Promote the replica; if none, **Restore the database** below |
| PostgreSQL data (corruption, a bad migration, deleted rows) | Wrong or missing data | **Restore the database** to a point before it, next to production |
| Kafka cluster | Ingress cannot hand webhooks on and answers with an error, so Meta retries; consumers stop | Rebuild the brokers, run the Migrator (it creates the topics), restart hosts. The outbox republishes what was unpublished; consumers dedupe |
| Redis `state` | Everyone signs in again; rate limits and locks reset | Start an empty instance; nothing else |
| Redis `cache` | Slower for minutes | Start an empty instance |
| Object storage (`files`) | Product images and uploads missing | Fail over to the mirror in the other zone (`Storage:*` settings), then re-mirror |

## Rebuild the application (cluster lost)

1. Create the namespace and the secrets from Vault (`pishkhan-secrets`: connection strings, data keys, Instagram, Zarinpal, SMS, AI).
   The data keys (`Secrets:Keys:*`, `Secrets:BlindIndexKey`) must be the **same** as before: without them encrypted columns (tokens,
   merchant ids, phone numbers) cannot be read. They are in Vault and in the offline key escrow.
2. `helm upgrade --install pishkhan backend/deploy/helm/pishkhan -f values-production.yaml --set image.tag=<last released sha>`.
   The Migrator hook runs first; on an intact database it changes nothing.
3. Check `/health/ready` on every host, then the M2 smoke list: sign in, open an inbox, send a reply on the test account, see it arrive.
4. Watch Kafka consumer lag drain (the webhooks queued while the cluster was gone). Expect a burst of replies to old messages: the
   outbound workers skip conversations whose 24-hour window closed, so nothing is sent outside Meta's rules.

## Restore the database

Follow [backup and restore](backup-restore.md#restore), with a point in time when WAL archiving is on:

1. Scale `api`, `adminapi` and every `workers-*` deployment to 0 (`kubectl scale deploy -l app.kubernetes.io/instance=pishkhan --replicas=0`,
   then scale `pishkhan-ingress` back up). Ingress keeps writing to Kafka.
2. Restore next to the live database, to the latest point before the incident (pgBackRest `--type=time`), or `restore.sh` with the last
   good dump.
3. Run the Migrator against it (`helm upgrade` with the new connection string runs it as the hook).
4. Switch `ConnectionStrings__Postgres` in the secret, `helm upgrade`, and let the HPA bring the hosts back.
5. Clear Redis `state` if the restore went back in time. Tell affected sellers about the gap, using the audit log for the window.

## Drill

Twice a year, and once before the public launch (M2), on staging sized like production:

1. Note the time. Delete the staging application namespace (not the databases).
2. Rebuild it from the chart and Vault as above. Note when `/health/ready` is green on every host and when the first test reply is sent.
3. Restore the staging database from last night's dump into a new database, switch to it, and repeat the smoke list.
4. During steps 2 and 3, keep sending webhooks with `tests/load/webhook-burst.js` at a low rate (`RATE=50`) and check afterwards that
   every message reached its inbox (no gaps in the test conversations).
5. Record below: date, commit, how long each step took (RTO), how old the restored data was (RPO), and anything that did not go as
   written here. Fix this runbook in the same pull request.

| Date | Commit | Rebuild (RTO) | DB restore (RTO) | Data age (RPO) | Messages lost | Notes |
|---|---|---|---|---|---|---|
| | | | | | | |
