# Changelog

## Unreleased

### Added (Phase 1, workspace deletion with a grace period, 2026-10-31)
- `POST /workspaces/{workspaceId}/deletion` (owner, `workspace.delete`) with `confirmName` equal to the workspace name returns 202 and the workspace with status `pending_deletion` and `deletionScheduledFor` (30 days later). Members lose access at once (its endpoints answer 404) and automations stop sending. A wrong name gets 400 `tenancy.deletion.confirm_mismatch`.
- `DELETE /workspaces/{workspaceId}/deletion` cancels while the deletion is pending (owner; other members 403, non-members 404, nothing pending 409). The workspace is active again.
- `GET /workspaces` lists workspaces pending deletion too, so the owner can find and cancel them. `WorkspaceResponse` gains `deletionScheduledFor`.
- The daily `tenancy.workspace-purge` job marks workspaces past the grace period `deleted` and emits the new `tenancy.workspace-deleted.v1` event. The data is then deleted: every row with the workspace's `workspace_id` in the module schemas, and the workspace's files with their stored objects. The audit log is kept, and the workspace row stays as a tombstone with `deleted_at` (migration `WorkspaceDeletion`).

### Added (Phase 1, automation test runs, product images, one set of business hours, 2026-10-29)
- `POST /workspaces/{workspaceId}/automation/test` dry-runs the rules against a sample DM or comment (`text`, `payload`, `source`, `kind`, optional `channelAccountId`, `mediaId`, `at`, `ruleId`, `contact`). It returns the outcome (`rule`, `outside_hours`, `product_details`, `ai`, `fallback`, `none`), the rule, the other rules that also matched, the steps with variables filled in, and the flow the rule starts, run to its first wait. Nothing is sent or stored, and cooldowns are ignored.
- `POST /workspaces/{workspaceId}/automation/flows/{flowId}/test` runs a flow's draft (or `published`) with sample `answers`, turn by turn. Delays end at once, and tag and field actions apply to the conditions that follow. An invalid draft gets 422 with the validation result.
- Product images one at a time: `POST /catalog/products/{productId}/images` (`fileId`, appended; adding one already there changes nothing; at most 10), `DELETE /catalog/products/{productId}/images/{fileId}`, and `PUT /catalog/products/{productId}/images/order` (`fileIds`, every image exactly once). Each returns the product and emits `catalog.product-changed.v1`.

### Changed
- Business hours have one source: the workspace's (`/workspaces/{workspaceId}/business-hours`, with closed dates). The outside-business-hours trigger and flow time conditions now read it. `automation/settings` no longer has `businessHours`, and its column is dropped (migration `AutomationUsesWorkspaceHours`). New workspaces start with the default shop hours (Saturday to Wednesday 9 to 21, Thursday 9 to 14), so an outside-hours rule now fires outside those hours unless the hours are changed.

### Added (Phase 1, user panel gaps, 2026-10-29)
- Inbox: conversations carry `contact` (`id`, `displayName`, `username`) and, while handed off, `handoff` (`reason`, `suggestedReply`, `at`). Leaving `handed_off` (or a new inbound message reopening the conversation) clears it.
- `GET /workspaces/{workspaceId}/conversations?q=` searches the customer's name, username, email and phone (national or +98 form).
- Messages carry `attachments` (`type`, `url`). Instagram story replies keep the story's URL as a `story_reply` attachment.
- `GET /workspaces/{workspaceId}/invitations` lists pending invitations (phone masked, inviter, expiry); `DELETE /workspaces/{workspaceId}/invitations/{invitationId}` revokes one (audited as `member.invitation_revoke`). Both need `workspace.members.manage`.

### Fixed
- OpenAPI: route values read by filters (for example `{workspaceId}`) are now declared as path parameters, and numbers are plain `integer`/`number` (the API no longer accepts numbers as strings).
- Integration tests: a test host is disposed only after its bus has started, so a short test class no longer leaves Kafka consumers behind that swallow the next class's events.

### Added (Phase 1, BE-1.13 part 1: website leads, 2026-10-29)
- New Content module (`content` schema), starting with leads from the website's waitlist and contact forms.
- `POST /api/v1/public/leads` (public, rate-limited per IP): `type` (`waitlist` | `contact`), `phone`, optional `name`, `instagramHandle`, `vertical`, `monthlyDms`, `message`, `utm` (`utm_*` and `ref` keys), and a solved ALTCHA challenge (single use). Same rules as the site's form: the waitlist needs the Instagram page, the contact form a name and a message. Returns 201 `{status: "received"}`.
- The phone is encrypted, with a blind index: a repeat from the same phone and form within a day updates the earlier lead (and counts submissions) instead of adding a duplicate.
- `GET /admin/v1/leads` (SuperAdmin, Support): newest first, filter `type`, `page`/`pageSize`, returns `{items, totalCount, page, pageSize}` with the phone in `09…` form.
- Integration tests raise the per-IP auth limit (all tests sign in from one address).

### Added (Phase 1, Audit log, 2026-10-29)
- New Audit module (`audit` schema): append-only `audit.audit_entries`, range-partitioned by month on `occurred_at` (partitions made by the Migrator and the maintenance job).
- Modules publish `audit.entry-recorded.v1` through their outbox, in the same transaction as the change. The sink (Workers platform group) is idempotent by entry id and fills in a user's display name or masked phone.
- Snapshots (`before`, `after`) have passwords, tokens, secrets, TOTP and OTP codes, phone numbers, card numbers and Sheba redacted at any depth.
- Audited now: staff sign-in, failed sign-in (with lockout), TOTP enrollment, step-up, logout and staff creation; workspace create, update and business hours; member invite, join, role change, removal; channel status changes (connect, reconnect, disconnect, reauth needed, deauthorized); Meta data deletion requests. A staff member impersonating a user is recorded as the staff member, on behalf of the user.
- `GET /admin/v1/audit` (SuperAdmin, Support): filters `actor` (name or id), `action`, `entityType`, `workspaceId`, `from`, `to`; `page`/`pageSize` (max 100); returns `{items, totalCount, page, pageSize}`. Support sees only entries of a workspace.
- `GET /api/v1/workspaces/{workspaceId}/audit` for the workspace owner (new permission `workspace.audit.read`, owner only). Staff actions show as actor `support`, without the staff member's name or address.

### Fixed
- Staff sign-in without a TOTP code (the panel's first step) no longer counts as a failed attempt; a wrong code still does.
- README: the first staff account's role is `SuperAdmin` (role names are case-sensitive).

### Added (Phase 1, BE-1.6 Catalog and storefront cards, 2026-10-29)
- New Catalog module (`catalog` schema): categories, products (title, description, tags, status `draft`/`active`/`archived`, fixed price in Rial, images, link), variants (SKU unique per workspace, attributes such as color and size, own price or the product's, stock with `track`/`unlimited`), storefront card sets, imports.
- Endpoints under `/workspaces/{ws}/catalog`: categories CRUD, products CRUD with Persian-normalized search, filters and cursor pagination, `GET /export`, `GET /import-template`, `POST /import` + `GET /imports/{id}`, storefront card sets CRUD and `/preview`.
- Excel import/export with Persian headers (one row per variant, prices in Toman, Persian or Latin digits). Imports read a clean `import_sheet` file in the commerce group, match products by id then SKU, create missing categories, keep variant ids, and report bad rows without stopping the import.
- Product images are Files uploads (`product_image`); only clean files get URLs, and cards use 3-day signed URLs that Instagram can fetch.
- Automation: `send_cards` action (rules and flow action nodes) and flow storefront nodes with `cardSetId` send the set as a carousel built at send time from current prices and stock (active products only, at most 10, Instagram text limits). A card's button returns the product's details (prices per variant, stock) from the catalog.
- Every product change emits `catalog.product-changed.v1` (for Knowledge, BE-1.4) and invalidates the workspace's card cache on all hosts.
- Money in the API is `{ "amount": <Rial>, "currency": "IRR" }`.
- Deferred: bulk price update and price-change batches (BE-2.6), formula pricing (Phase 3), the AI catalog tool (BE-1.4).

### Added (Phase 1, BE-1.3 Automation engine, 2026-10-29)
- New Automation module (`automation` schema): rules, flows (draft and published versions), flow runs, comment records, backlog runs, per-rule daily stats, workspace settings (AI on or off, business hours, fallback message).
- Triggers: DM keyword (exact, contains, any-of, safe `*`/`|` patterns), comment keyword, every comment on a post, story reply, story mention, live comment, post share, new conversation (welcome), postback and quick reply payloads, outside business hours. Comment rules cover all posts, one post, or the next post (bound on its first comment).
- Persian matching: Arabic ي/ك, Persian and Arabic-Indic digits, kashida, punctuation, emoji, and spaces versus half-spaces (ZWNJ) are all ignored when comparing keywords.
- Actions: send message (text, quick replies, buttons, cards, media, with `{{contact.first_name|fallback}}` style variables), private reply, public reply (random from a list), start flow, add or remove tag, set field, hand off, pause AI, notify team.
- Evaluation order per DM: waiting flow run → rules by priority (then specificity, then age) → outside-hours rule → AI reply request (`conversations.reply-requested.v1`, consumed from BE-1.4) → fallback message with a cooldown. The inbound message records `handled_by` and the rule id.
- Flows: question (text, number, phone, choice with retries), condition (tag, field, variable, business hours), delay, message, storefront cards, action, AI hand-off, end. Validated server-side (unknown nodes, bad edges, loops without a wait); a delay that ends after the 24-hour window closed expires the run instead of sending.
- Comments: ingest now parses `comments` and `live_comments` webhooks; each comment is processed once (`automation.comment_events`), private replies are limited to one per comment and to Meta's 7-day age, and both private and public replies are paced per channel account from its rate profile.
- Backlog: `POST /rules/{id}/backlog-run` pages through a post's existing comments (also ones that arrived before the rule) and answers them with the same dedupe and pacing.
- Outbound: a send refused by the account's rate limit now waits in a timer until the bucket refills, instead of failing after six retries.
- Contacts carry tags and custom fields (until the CRM module, BE-1.7).
- Meta data deletion also clears the customer's comment records and flow runs.
- Load test: 2,000 comments on one post (plus 500 redelivered) got exactly 2,000 private replies, paced within the rate profile.
- Deferred: follow-gate node and follow-up timers (BE-1.8), form-completed trigger and form submissions (BE-1.7), catalog-driven storefront cards (BE-1.6), order-status trigger and phone capture (Phase 2).

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
