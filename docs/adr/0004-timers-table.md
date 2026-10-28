# 0004. Delayed work through a timers table and TimerFired events

Status: accepted (2026-10-28)

Kafka has no delayed delivery. Module code schedules with `TimerScheduler.Schedule(db, kind, dueAt, payload, dedupeKey)` in the same transaction as its business change. The Workers platform group polls due rows with `FOR UPDATE SKIP LOCKED` and writes a `TimerFired` event (kind, payload) to the outbox. Consumers filter by kind. Cancelling uses the dedupe key. This keeps delays transactional and observable (timer backlog metric) without a second scheduler.
