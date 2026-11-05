# Load tests (BE-2.11)

[k6](https://k6.io) scripts for the launch load test: **2× the capacity plan** of backend plan 3.5 (10,000 workspaces, 50 M messages a
month, a 2,000 events/s webhook peak), with the plan's latency targets as k6 thresholds, so a run fails when a target is missed.

| Script | Host | Load (2× plan) | Thresholds |
|---|---|---|---|
| `webhook-burst.js` | Ingress | 4,000 signed Meta webhook events/s for 5 minutes, 60% comments (a viral post) | p99 < 150 ms, p95 < 80 ms, errors < 0.1% |
| `panel-api.js` | Api | 400 agents (two busy shifts of the plan's active workspaces), inbox list, thread, notifications, orders, dashboard, notes | reads p95 < 200 ms, writes p95 < 400 ms, errors < 0.5% |

What k6 cannot see is measured from the same run in Grafana (traces and the `Pishkhan` meter), against backend plan 3.5:

- rule reply, webhook → Graph API send: p95 < 2 s (the webhook's trace, from Ingress to the outbound worker's Graph API call);
- AI reply: p95 < 8 s, with the AI routes on the providers' sandbox keys or the `fake` provider;
- inbox realtime update after an inbound message: < 1 s;
- Kafka consumer lag back under 30 s within 5 minutes of the burst ending, nothing dead-lettered, outbox backlog drained;
- no `429` from Meta: outbound pacing turns the burst into "slower", not "blocked" (Meta is stubbed by `tools/smoke/mock_meta.py` in staging).

## Running

Against staging, sized like production (the Helm chart with production values, PgBouncer in transaction mode, three Kafka brokers):

```bash
# Webhook burst: the app secret of the staging Meta app, and Instagram account ids connected there.
k6 run -e BASE_URL=https://hooks.staging.example.ir -e APP_SECRET=$APP_SECRET -e ACCOUNTS=$ACCOUNTS tests/load/webhook-burst.js

# Panel: workspaceId:accessToken pairs of the seeded load-test workspaces.
k6 run -e BASE_URL=https://api.staging.example.ir -e TOKENS=$TOKENS tests/load/panel-api.js
```

The `load` workflow (Actions → load → Run workflow) runs either script against a target given as input, with the secrets
`LOAD_APP_SECRET`, `LOAD_ACCOUNTS` and `LOAD_TOKENS`. It is manual: a load test belongs on a staging cluster, never on a CI runner or on
production during business hours.

Seed staging before the run (there is no seeding tool yet; a restored, anonymised copy of beta data scaled up works): at least 1,000 workspaces with a connected (stubbed) Instagram account, 30 days of conversations
(50 M messages a month ÷ 30 for a day's partition), and `pg_stat_statements` reset, so the queries of the run can be read back afterwards.

## Recording a run

Copy the k6 summary and the Grafana screenshots (latency, consumer lag, pod count from the HPA, PostgreSQL CPU and connections) into the
M2 checklist ([docs/launch-m2.md](../../../docs/launch-m2.md)) with the date and the commit. A release whose p95 regresses by more than 20%
against the last recorded run does not ship (backend plan 3.5).
