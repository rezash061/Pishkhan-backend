# Changelog

## Unreleased

### Added (Phase 0, 2026-10-28)
- Solution skeleton (.NET 10, central packages, analyzers as errors), shared kernel and Persian text library.
- Building blocks: persistence with tenancy filters, outbox/inbox messaging over Kafka, timers, Quartz jobs, Redis caching, locks and rate limits, JWT auth, secrets, SSRF-safe HTTP, antivirus, telemetry, OpenAPI.
- Modules: Identity, Tenancy, Backoffice, Files, Channels (Instagram), Conversations, Agent (evals).
- AI gateway with per-task routes, fallback, circuit breaker, metering and prompt registry.
- Hosts: Api, AdminApi, Ingress, Workers, Migrator; EvalRunner CLI; local docker compose; M0 smoke test; CI workflow with OpenAPI drift check.
- ADRs 0001 to 0008.
- Integration tests on Testcontainers (outbox to consumer ordering, inbox dedupe, DLQ, Redis lock and token bucket), run in CI.

### Fixed
- UUID v7 ids are now monotonic within a millisecond. `Guid.CreateVersion7` is not, so the outbox relay could publish one key's events out of order.
