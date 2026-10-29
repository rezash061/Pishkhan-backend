# Changelog

## Unreleased

### Added (Phase 1, BE-1.5 Live inbox, 2026-10-29)
- SignalR hub `/hubs/inbox` (realtime token only) with `JoinWorkspace` / `JoinConversation`; membership and `inbox.read` checked on every join.
- Redis backplane on the state instance, so events reach clients on every Api replica.
- Api `realtime` consumer group turns pipeline events into `conversation.updated`, `message.created`, `message.status`, `handoff.requested` and `channel.status`; inbox actions push `conversation.updated` directly.
- The build-time OpenAPI export no longer starts the Kafka bus.
- Deferred: persistent handoff notifications (bell, web push, SMS) come with BE-1.14.

### Added (Phase 1, BE-1.2 Conversations and outbound pipeline, 2026-10-29)
- Outbound policy: normal messages only inside the 24-hour window; a team member's reply within 7 days goes out with the `HUMAN_AGENT` tag (never automated messages); private replies limited to one per comment; suspended workspaces cannot send.
- Read receipts from Instagram mark our earlier messages `read` (`conversations.messages-read`); sent message ids are kept for receipts and echoes.
- Inbox API: filters (status, assignee, channel account, unread), team replies with quick replies or buttons (idempotent, 202), assign, status (handed_off pauses the AI and emits `conversations.handoff-requested.v1`), AI pause, mark read, internal notes, quick reply templates CRUD.
- Inbox indexes; load test: list p95 7 ms with 1 M messages and 20 k conversations in one workspace (target 200 ms).
- Deferred: plan quota soft-check (BE-1.11), attachments in team replies (with the Files picker), downloading inbound media.

### Added (Phase 1, BE-1.1 Instagram production-grade, 2026-10-29)
- Channel status machine (`connecting → active → token_expiring → reauth_required | disconnected`) with reason and time; every change emits `channels.channel-status-changed.v1`.
- Hourly token refresh job (Quartz, Workers platform group): refreshes 60-day tokens 10 days before expiry; a revoked token moves the account to `reauth_required`; repeated transient failures within 3 days of expiry show `token_expiring`.
- Rich sends: text with quick replies, button template, generic template (carousel cards), image/audio/video, private replies to comments; public comment replies; customer profile fetch with follow flags (for the follow-gate).
- `GET /workspaces/{ws}/channels/{id}/media` lists posts and reels for post-specific rules.
- Rate profiles per API family (send, private reply, comment reply, read) with admin overrides in `channels.rate_profiles`.
- Meta data deletion is now processed: Conversations deletes the customer's conversations, messages, identity and contact, and the request turns `completed`.
- Default webhook subscription fields extended (reactions, referrals, comments, live comments).
- Deferred: viral-mode comment pacing moves to BE-1.3 (it needs comment ingest); admin editing of rate profiles comes with BE-1.12.

### Added (Phase 0, 2026-10-28)
- Solution skeleton (.NET 10, central packages, analyzers as errors), shared kernel and Persian text library.
- Building blocks: persistence with tenancy filters, outbox/inbox messaging over Kafka, timers, Quartz jobs, Redis caching, locks and rate limits, JWT auth, secrets, SSRF-safe HTTP, antivirus, telemetry, OpenAPI.
- Modules: Identity, Tenancy, Backoffice, Files, Channels (Instagram), Conversations, Agent (evals).
- AI gateway with per-task routes, fallback, circuit breaker, metering and prompt registry.
- Hosts: Api, AdminApi, Ingress, Workers, Migrator; EvalRunner CLI; local docker compose; M0 smoke test; CI workflow with OpenAPI drift check.
- ADRs 0001 to 0008.
