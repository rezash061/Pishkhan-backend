# M2 public launch checklist (backend)

From the overview's milestones: **P1 features, paid plans live, eNamad seal visible, load and penetration tests passed.** This is the
backend's part; the panels and the website keep their own. Tick an item only with the evidence linked.

## Features (Phase 2 work packages)

| | Work package | Status |
|---|---|---|
| ✅ | BE-2.1 Orders, BE-2.2 Zarinpal payment links | Merged |
| ✅ | BE-2.3 Finglish, BE-2.4 voice notes, BE-2.5 feed import, BE-2.6 bulk price | Merged |
| ✅ | BE-2.7 Outage resilience (detector, phone capture, SMS and Bale, campaigns) | Merged; Bale media and the AI phone-capture tool are follow-ups |
| ✅ | BE-2.8 Checkout, overage with cap, alerts, dunning | Merged; wallet and installments deferred |
| ✅ | BE-2.9 Industry templates, BE-2.10 card-to-card receipts | Merged; receipt auto-approve stays off until the labeled-set precision gate |
| ⬜ | BE-2.11 Launch hardening | This list |

## Paid plans live

- [ ] Production Zarinpal merchant for Pishkhan's own account (`Checkout:MerchantId`), `Checkout:Sandbox=false`, a real payment and a
      refund made and reconciled
- [ ] Plans published with prices and overage prices; free plan limits confirmed
- [ ] Invoices numbered from the production sequence; the finance role can read them in Backoffice

## Load test (2× capacity plan)

- [ ] `webhook-burst.js` at 4,000 events/s for 5 minutes: thresholds passed — summary and Grafana links:
- [ ] `panel-api.js` at 400 agents for 10 minutes: thresholds passed — summary and Grafana links:
- [ ] Rule reply p95 < 2 s and AI reply p95 < 8 s during the burst; consumer lag back under 30 s within 5 minutes; nothing dead-lettered
- [ ] `pg_stat_statements` top 20 reviewed after the run; no sequential scans on hot tables

See [tests/load/README.md](../backend/tests/load/README.md).

## Penetration test

- [ ] Contract signed with the scope in [security/pentest-scope.md](security/pentest-scope.md)
- [ ] Report received; no open critical or high findings; mediums fixed or accepted with a date
- [ ] Re-test of the fixes passed

## Disaster recovery

- [ ] Drill done on staging as written in [runbooks/disaster-recovery.md](runbooks/disaster-recovery.md#drill); RTO and RPO recorded and
      within target
- [ ] WAL archiving to another zone on in production; a point-in-time restore tried once
- [ ] Data keys in the offline escrow, retrieved once by someone other than the person who put them there

## Kubernetes

- [ ] Images built and pushed by the `deploy-artifacts` workflow; chart lint and schema check green
- [ ] Production values file reviewed (hosts, resources, allowlists, TLS secrets); secrets from Vault only
- [ ] `helm upgrade` done twice on staging, once with a migration, without errors or dropped requests (blue/green for Api per backend
      plan 13 if the rolling update shows errors)
- [ ] Alerts wired for the runbooks (Kafka lag, DLQ, ClamAV, Meta errors, 5xx rate, pod restarts, HPA at max)

## Compliance and operations

- [ ] eNamad seal on the website (website checklist)
- [ ] Meta app review approved for the permissions in production
- [ ] On-call rota and the runbooks linked from the alerting tool
