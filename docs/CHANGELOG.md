# Changelog

## Unreleased

### Added (Phase 2, BE-2.6: bulk price update, 2026-11-02)
- Change many prices at once under `/workspaces/{id}/catalog/price-changes` (`workspace.catalog.write`; reading the history needs `workspace.catalog.read`). Migration `PriceChanges` (`catalog.price_changes`).
- Which products (`scope`): any of `productIds` (up to 1,000), `categoryId` (with its subcategories), `tag` and `status`, which must all match. Or `all: true`. Products priced by formula are never changed. At most 5,000 products (422 `catalog.price_change_too_large`).
- How prices change:
  - `kind: "percent"` with `percent` from −90 to 500 (up to two decimals).
  - `kind: "amount"` with `amount` in Rial; negative lowers prices.
  - New prices are rounded to `roundTo` Toman (1, 100, 500, 1000, 5000, 10000, 50000 or 100000; default 1) by `roundMode` (`nearest`, the default, `up` or `down`).
- Changed prices: the product's own price and every variant's own price. Variants that use the product's price follow it.
- `POST /preview` saves nothing. It returns every price that changes (old and new, with product title and variant label), the prices that would reach zero or below (`invalid`), and a `fingerprint`.
- `POST` (accepts `Idempotency-Key`) applies the same calculation as one batch, so the result is exactly the preview.
  - With the preview's `fingerprint`, a catalog change since the preview gives 409 `catalog.price_change_stale`.
  - 422 `catalog.price_change_below_zero` while any price would reach zero or below; 422 `catalog.price_change_empty` when nothing changes.
  - Audited as `catalog.prices_changed`.
- `GET` lists the last 50 batches. `GET /{batchId}` adds every price changed.
- `POST /{batchId}/undo` within 24 hours puts back every price that still has the batch's new value. Prices changed by hand since are left alone and counted in `undoSkipped`. 409 `catalog.price_change_undone` or `catalog.price_change_undo_expired`. Audited as `catalog.prices_change_undone`.
- Every changed product publishes `ProductChanged` with the new change kind `prices_changed`, so the AI's knowledge re-indexes it. Each batch and each undo also publishes `catalog.prices-updated.v1` (`PricesUpdated`). Catalog caches, cards included, are invalidated in the same transaction.

### Added (Phase 2, BE-2.2: Zarinpal payment links, 2026-11-02)
- New Payments module (`payments` schema). Migration `InitialPayments`. Decisions are in ADR 0011.
- The seller connects their own Zarinpal merchant account. Money goes straight to the seller; Pishkhan never holds it.
  - `GET`, `PUT` and `DELETE /workspaces/{id}/payments/merchant-account`. Reading needs `workspace.settings.read`; changing needs `workspace.settings.write`.
  - `PUT` takes `{provider?, merchantId?, sandbox, enabled}`. The merchant ID must be Zarinpal's 36-character ID (`payments.merchant_id_invalid`). Leave it out to keep the stored one.
  - The merchant ID is stored encrypted. Responses show only its last four characters (`merchantIdHint`).
  - Connecting, updating and disconnecting are audited (`payments.merchant_connected`, `payments.merchant_updated`, `payments.merchant_disconnected`).
- Payment links:
  - `POST /workspaces/{id}/orders/{orderId}/payment-links` (`workspace.orders.write`, accepts `Idempotency-Key`). A complete draft moves to `awaiting_payment`. Errors: 422 `payments.merchant_missing`, 409 `orders.not_payable`, 400 `orders.incomplete`.
  - The link's amount is the order total when the link is made. A new link cancels the older active ones.
  - `GET /workspaces/{id}/orders/{orderId}/payment-links` lists them, newest first.
  - `POST /workspaces/{id}/payment-links/{linkId}/cancel`. A link that isn't active is 409 `payments.link_not_active`.
  - Link `status` is `active`, `paid`, `cancelled` or `expired`. A link can be paid for 3 days (`Payments:LinkLifetime`) and allows 20 payment attempts.
  - Each link also returns `url`, `attempts`, `lastError` (Zarinpal's code, or `cancelled` when the customer left the gateway), `referenceId` and `cardMask`.
  - When an order leaves `awaiting_payment` (back to draft, cancelled, or marked paid by hand), its active links are cancelled.
- The customer's payment page, `GET /pay/{token}`, is a small Persian page with the order lines, the total in Toman and a pay button. It is served by the Api host, outside the API documents, and rate-limited per IP.
  - The token is 32 random URL-safe characters.
  - The page has its own CSP: no scripts, and the form may only go to us and to Zarinpal.
  - The button (`POST /pay/{token}`) opens a payment at Zarinpal and redirects the customer there (303).
- Zarinpal's callback, `GET /pay/callback/zarinpal`:
  - Verifies the payment with Zarinpal for the link's own amount. The callback's `Status` only says whether the customer finished; it is never trusted to mark anything paid.
  - A verified payment marks the link paid and publishes `payments.payment-confirmed.v1` (`PaymentConfirmed`), audited as `payment.confirmed`. The order then moves to `paid` (System actor, with the reference ID in its timeline), which takes the stock.
  - A forged, repeated or late callback verifies nothing again and never pays twice. An unknown authority is 404.
  - A verified payment for an order no longer awaiting payment, or for a different total, is recorded as a note on the order and does not change its status.
- Configuration: `Payments:PublicBaseUrl` (where `/pay` is served, for links and the callback), `Payments:Zarinpal:BaseUrl` and `Payments:Zarinpal:SandboxBaseUrl`.
- The security headers middleware now keeps a `Content-Security-Policy` an endpoint has set.

### Added (Phase 2, BE-2.1: orders, 2026-11-01)
- New Orders module (`orders` schema). Migration `InitialOrders`. Decisions are in ADR 0010.
- Statuses: `draft → awaiting_payment → paid → preparing → shipped → delivered → completed`, plus `cancelled`, `refunded` and `returned`.
  - Only the allowed moves are accepted. Each order lists them as `nextStatuses`, and any other move is 409 `orders.status_move_invalid`.
  - `awaiting_payment` needs items and a total above zero.
  - Only a draft can be edited (409 `orders.not_editable`). An order awaiting payment can be moved back to `draft` to change it.
- Order numbers count up per workspace. Numbers are unique, but a failed request can leave a gap.
- Catalog lines are always priced from the catalog: the product must be active and in stock, and a request cannot override the price. Panel orders can also have custom lines with a `title` and `unitPrice`.
  - Line errors: `orders.product_not_found`, `orders.variant_required`, `orders.variant_not_found`, `orders.out_of_stock`, `orders.quantity_invalid` and `orders.price_missing`. Each is reported on `items[i]`.
- The customer's mobile number is encrypted and has a blind index, so orders can be searched by an exact mobile number.
- Addresses:
  - Stored as province code, province, city, line and postal code.
  - Can be sent as structured fields, or as `text` to be split.
  - Postal codes are validated (`orders.postal_code_invalid`) and provinces must be known (`orders.province_unknown`).
  - `missing` lists what an order still lacks for shipping: `address`, `city`, `postalCode` and `phone`.
- Endpoints, all under `/workspaces/{id}/orders`:
  - `GET` lists orders. Filters: `status` (repeatable), `contactId`, `conversationId`, `number` and `phone`. Cursor paging.
  - `POST` creates a draft. It accepts `Idempotency-Key`. It takes a `conversationId` or a `contactId`.
  - `GET` and `PUT /{orderId}`.
  - `POST /{orderId}/status` takes `{status, note}`.
  - `POST /{orderId}/notes`.
  - `GET /{orderId}/timeline` returns created, edited, status-change and note entries, each with its actor (`user`, `ai` or `system`).
  - `GET /{orderId}/invoice` returns Persian digits, Jalali dates and amounts in Toman.
  - `GET /provinces`.
  - `POST /parse-address`.
- New permissions:
  - `workspace.orders.read`: owner, admin, agent and viewer.
  - `workspace.orders.write`: owner, admin and agent.
- Every status change by a user writes an audit entry, `order.status_changed`.
- `orders.order-status-changed.v1` (`OrderStatusChanged`) is published when an order is created (with `fromStatus` null) and on every status change.
- Stock: the Catalog module takes counted stock when an order is paid. It puts the stock back when a paid order is cancelled, refunded or returned. Stock never goes below zero.
- AI tool `create_order`:
  - Saves the conversation's draft order, with catalog prices only and one AI draft per conversation. Calling it again updates that draft.
  - Takes the customer's name, mobile, address text and a note.
  - Returns the order number, the lines, the total in Toman and what is still missing.
  - In the test chat and the demo it only prices the lines.
- Reply prompt `agent.reply` version 2 (now active) tells the model when to call `create_order`. Version 1 is kept, so the change can be rolled back in the admin AI settings.
- `IConversationQueries.GetContactAsync`.
- `Pishkhan.Persian`:
  - `IranianProvinces`: the 31 provinces with their larger cities.
  - `AddressParser`: splits a pasted address into province, city, postal code, mobile and the rest of the line.
- Not in this package: the `order_status_changed` automation trigger (still listed as coming later) and payment links (BE-2.2).

### Added (user-panel gaps: knowledge files and URLs, contact pictures, 2026-11-01)
- Knowledge sources from uploaded documents and web pages:
  - `POST /workspaces/{id}/knowledge/sources` takes `kind: "file"` with `fileId` (an upload with purpose `knowledge_document`: PDF, DOCX or TXT, scanned clean) or `kind: "url"` with `url` (https, public host). Anything else is 400 on `fileId` or `url`.
  - The AI group extracts the text before indexing: PDF text layer (PdfPig, NFKC-normalised for Persian presentation forms), DOCX paragraphs and table rows, UTF-8 or BOM-marked text, and the visible text of an HTML page (no scripts, styles, navigation, header, footer or forms). Pages are fetched through `SafeHttpClient` with a 2 MB cap; text is capped at 50,000 characters.
  - The title defaults to "سند" for a file and to the host for a URL; a page's own `<title>` replaces the host.
  - A source that yields no text goes to `failed` with `error` set to one of `knowledge.empty`, `knowledge.file_unavailable`, `knowledge.file_unreadable`, `knowledge.file_type`, `knowledge.page_unreachable`, `knowledge.page_too_large`, `knowledge.page_type`.
  - `PATCH` on a file or URL source accepts only `title`, `enabled` and (URL sources) `url`; a new URL re-indexes the source. `body`, `question` or `answer` give 400 `validation.knowledge.extracted`.
  - `SourceResponse` gains `fileId` and `url`. Migration `KnowledgeFilesAndUrls`.
- Contact profile pictures:
  - When a new Instagram customer writes for the first time, the outbound group reads their profile (name, username, picture), copies the picture into the Files module (purpose `avatar`, 2 MB, Meta CDN hosts only) and fills the contact. A name the seller set is kept. A repeated request within 7 days is skipped. Existing contacts are not backfilled.
  - New topic `conversations.contact-profile-requested.v1` (`ContactProfileRequested`).
  - `contact.profilePicUrl` on conversation responses and `profilePicUrl` on CRM contacts: a signed URL valid for 6 hours, or null. Instagram's own CDN URL is never returned. Migration `ContactProfiles`.
- `docs/deployment.md`: the inbox hub has no CORS policy and must be served at `/hubs/inbox` on the panel's own origin (Nginx block for production, Next rewrite for development, `access_token` kept out of access logs).

### Added (Phase 1, BE-1.15: beta hardening, 2026-11-01)
- Cross-tenant access suite (`CrossTenantTests`). It walks every route the Api host maps under `/workspaces/{workspaceId}` (about 100 route and method pairs) and seeds one resource of each kind in workspace B:
  - A non-member calling any of B's routes gets 404.
  - A member of A calling A's routes with B's IDs gets 404.
  - B still finds all its resources afterwards.
  - A route parameter the suite cannot seed fails the test, so new endpoints are covered automatically.
  - Revoking another user's session is also checked.
- Rate limits tuned (`RateLimits` section):
  - New per-workspace limit, `WorkspacePerMinute` (3,000). It is counted after the membership check, so outsiders cannot use up a workspace's budget, and answers 429 `common.rate_limited` with `Retry-After`.
  - New per-staff limit on the authenticated back office, `StaffPerMinute` (600).
  - Public reads (GET) have their own per-IP budget, `PublicReadsPerMinute` (600), so the website's server can fetch pages while building. Public writes stay at `PublicPerMinute` (60).
  - Sign-ins per IP raised from 20 to 120 an hour (`AuthPerHour`), because Iranian mobile carriers put many users behind one address. Per-phone and per-account attempt limits are unchanged, and so is the OTP request limit (20 an hour per IP).
- Backups: `infra/backup/backup.sh` (pg_dump custom format with a checksum and retention), `restore.sh` (refuses an existing target, checks the checksum) and `verify.sql` (exact rows per table).
  - `BackupRestoreTests` dumps the live test database while other tests write to it, restores it into a new database, and compares tables, columns, indexes, extensions, every module's migrations and the seeded data.
- On-call runbooks in `docs/runbooks/`: Meta outage, Kafka lag and DLQ replay, ClamAV down, backup and restore.

### Fixed (BE-1.15)
- `GET /workspaces/{id}/crm/forms/{flowId}/submissions` answered 200 with an empty page for a flow of another workspace. It now answers 404 unless the flow exists in this workspace or has submissions here. A deleted flow keeps its submissions. This uses the new `IFlowQueries` in Automation.Contracts.
- Dev JWT keys: two hosts starting at the same moment could read a half-written key file, or sign with different keys. The key is now written aside and moved into place, and the first host wins.

### Added (Phase 1, BE-1.14: notifications v1 completed, 2026-11-01)
- Web push (VAPID, RFC 8292; payloads encrypted with `aes128gcm`, RFC 8291, checked against the RFC's test vector):
  - `GET /api/v1/me/push/key` returns the VAPID public key. It is 404 `push.unavailable` when `WebPush:PublicKey`/`PrivateKey` are not set.
  - `PUT /api/v1/me/push-subscriptions` stores the browser's `PushSubscription.toJSON()` (`endpoint`, `keys.p256dh`, `keys.auth`). Sending the same endpoint again updates it. A user can have up to 10 browsers.
  - The endpoint must be https on a known push service (`WebPush:AllowedHosts`: FCM, Mozilla, Windows, Apple), so the server never posts anywhere else.
  - `GET /api/v1/me/push-subscriptions` lists the caller's browsers. `DELETE /api/v1/me/push-subscriptions?endpoint=` removes one.
  - Notifications whose `push` preference is on are sent to every subscribed browser after the commit, as JSON `{kind, title, body, link, workspaceId}`.
  - A subscription the push service reports gone (404/410) is deleted. One that fails 5 times in a row is dropped.
- Handoff SMS now follows the plan ("handoff waiting > N minutes"). In-app and push go out at once, and a timer checks after `Notifications:HandoffSmsAfter` (10 minutes).
  - The SMS is sent only if the conversation is still handed off and the customer spoke last.
  - The 30-minute cooldown per kind still applies.
- Live in-app notifications:
  - Each stored notification publishes `notifications.notification-created.v1`.
  - The Api hosts push it on the inbox hub as `notification.created` to the user's own connections. The payload is `{workspaceId, id, kind, title, body, link, createdAt}`, and no join is needed.

### Fixed (2026-11-01)
- AdminApi failed DI validation at startup in Development: `ReplyPipeline` (needs Knowledge and Catalog) was registered by `AddAgentModule`. It now comes from `AddAgentReplies()`, which only the Api and Workers hosts call. The admin integration host now validates on build, so this is caught by tests.

### Added (Phase 1, BE-1.13 part 2: website CMS, 2026-11-01)
- CMS entries (Content module, new tables `content.entries`, `content.categories` and `content.redirects`). Kinds are `page`, `post`, `help`, `case_study`, `legal` and `faq`.
  - Each entry has a draft and a published copy. Editors save the draft, and the site keeps showing the published copy until the next publish.
  - `body` is an array of blocks with allowed `type`s, and plain-text strings only. Link and image fields must be https or a site path.
  - `data` holds kind-specific fields (seoTitle, cover, keywords, version, consent, metrics, tags).
  - Slugs are Latin or Persian words joined by hyphens. A slug is fixed once the entry has been published.
- Back office (`/admin/v1/content`, ContentEditor or SuperAdmin to write; Support and ReadOnly read):
  - Entries: list, get, create, save draft (`revision` gives 409 `content.stale`), publish now or with `at`, cancel the schedule, archive, delete (never-published only), and a one-hour preview token.
  - Blog and help categories, and redirects.
  - Publishing is refused for a case study without `data.consent: true` (409 `content.consent_required`) and for a post or help article without a category (409 `content.category_required`).
  - Entry changes are audited.
- Scheduled publishing: the Workers' platform job `content.scheduled-publish` publishes drafts whose time has come, every minute.
- Every publish, archive, category change or redirect change drops the `content` cache tag on all hosts. It also asks the website to revalidate the tags `content` and `content:{kind}`, the entry's paths (`/blog/{slug}`, `/help/{category}/{slug}`, `/{slug}`, ...) and `/sitemap.xml`.
- Public reads under `/api/v1/public` (published entries only, cached, Website tag):
  - `pages/{slug}`, `legal/{slug}`, `posts?category=&cursor=&limit=`, `posts/{slug}`, `help?category=`, `help/{slug}`, `help/search?q=` (Persian-normalised, every word must match, title matches first), `case-studies`, `case-studies/{slug}`, `faqs?tag=`, `categories?kind=blog|help` and `redirects`.
  - `preview?token=` returns the draft for the site's preview mode.

### Added (Phase 1, BE-1.13 part 1: website revalidation webhook and demo chat, 2026-11-01)
- Website revalidation webhook:
  - New event `content.site-revalidation-requested.v1` (`tags`, `paths`). The Workers (platform group, Content module, now in the Workers host) answer it with `POST {Website:RevalidateUrl}` and body `{"tags":[...],"paths":[...]}`.
  - The call is signed as the site's `lib/hmac.ts` expects: `X-Pishkhan-Timestamp` (unix seconds) and `X-Pishkhan-Signature: sha256=<hex HMAC-SHA256(Website:RevalidateSecret, "<timestamp>.<body>")>`. A failed call is retried by the consumer, then dead-lettered.
  - With no `Website:RevalidateUrl` nothing is called. Deploys set it, with the same secret as the site's `REVALIDATE_SECRET`.
  - Publishing a plan, or archiving a published one, now also asks the site to revalidate the `plans` tag.
- Demo chat: `POST /api/v1/public/demo-chat` with `{ sessionId, message (≤ 500), altcha? }`.
  - It runs the reply pipeline against the demo shop's workspace (`DemoChat:WorkspaceId`), with that shop's knowledge and products. Without it the answer is 404 `demo.unavailable`.
  - It returns `{ reply, confidence, sources (knowledge titles used), remaining, handedOff }`. When the agent would hand off, `reply` is the shop's handoff message, or a line saying a person would take over here.
  - ALTCHA is needed on a session's first message. The limits are 10 messages per session (429 `demo.limit`), 30 per IP per hour and 3,000 per day (429 `demo.rate_limited`). They are configurable under `DemoChat`.
  - Nothing is stored in the database. A session's last messages stay in Redis for 6 hours as context.

### Added (Phase 1, BE-1.4 part 3: test mode, unanswered questions and eval set v1, 2026-11-01)
- Test mode: `POST /api/v1/workspaces/{workspaceId}/agent/sandbox` (`workspace.agent.write`).
  - It takes `text`, optional `history` (up to 20 `{role: customer|agent, text}`), `tags` (to try the VIP rule) and `goal`.
  - It runs the same reply pipeline in the Api and returns `outcome`, `messages` (what the customer would receive), `handoffReason`, `suggestedReply`, `unansweredQuestion` and the full `trace`.
  - Nothing is sent, stored or metered. It is limited to 20 messages per minute per workspace (429 `agent.sandbox.rate_limited`).
- Unanswered questions (`workspace.knowledge.write`):
  - `GET /api/v1/workspaces/{workspaceId}/knowledge/unanswered?status=open|answered|dismissed&cursor=` lists them, most recently asked first, with `count`.
  - `POST .../unanswered/{id}/answer` takes `answer` and an optional `question` wording. It creates a FAQ source that is indexed in the background and marks the question answered (201; 409 `knowledge.unanswered.already_answered`).
  - `POST .../unanswered/{id}/dismiss` hides one. A question asked again opens again.
- Eval set v1 (`tools/Pishkhan.EvalRunner/samples/v1.jsonl`): 100 synthetic Persian, Finglish and English DMs across clothing, cosmetics, home, food and digital accessories. Every intent and its expected handoff is labelled. It is a seed until consented launch-shop conversations replace it.
- Classifier eval runs now also score the reply pipeline's handoff rules: `handoffPrecision` and `handoffRecall` per run, and `expectedHandoff`, `predictedHandoff` and `handoffReason` per result. They show in the admin API and the EvalRunner output. A migration adds the columns.

### Added (Phase 1, BE-1.4 part 2: the AI reply pipeline, 2026-11-01)
- The Workers (ai group) now answer `conversations.reply-requested.v1` (AI on, no rule matched, or a flow handing over with a goal). One run answers the customer's whole turn (the messages since our last reply). It skips a request that is no longer current: a newer message, a reply already sent, the AI paused, the conversation handed off, or the window closed.
- Pipeline:
  - Handoff rules in code first: VIP tags, configured keywords, and built-in "talk to a person" phrases.
  - Then classification (prompt `intent.classify` v2 adds language and sentiment). Human requests, negative complaints and discount requests go to a person.
  - Then hybrid knowledge retrieval and generation (prompt `agent.reply` v1, JSON `reply`, `confidence`, `answered`) with tools `search_products`, `get_product`, `check_stock`, `request_handoff` and `add_unanswered_question`.
- Output guard:
  - Every amount (digits in any script, «هزار/میلیون», number words, Toman or Rial) must match a retrieved fact or a tool result.
  - Also checked: the never-say list, a link allowlist (links in facts or tools, plus the profile's domains), 1,000 characters, and Persian unless the customer wrote otherwise.
  - A failed answer is regenerated once and then handed off (`guard_price`, `guard_link`, ...).
- Combined confidence: half the model's own estimate, a quarter the classifier's, a quarter grounding. Below the workspace threshold the conversation is handed off with the reply as the suggested reply. Two weak answers in a row also hand off. Missing knowledge hands off and records an unanswered question.
- A handoff sends the profile's handoff message (if set) and hands off with reason `ai:<reason>`. `HandoffRequested` now carries the suggested reply.
- A sent reply records `billing.usage-recorded.v1` for `ai_messages` and `ai_conversations`. When the plan's `ai_messages` limit is reached, the agent hands off (`plan_limit`) instead of answering.
- Agent settings and traces (Api, `/api/v1/workspaces/{workspaceId}/agent`):
  - `GET /profile` (`workspace.settings.read`, defaults until saved) and `PUT /profile` (`workspace.agent.write`).
  - The profile holds name, tone (friendly, formal, playful), brand voice, reply in the customer's language, never-say list, confidence threshold (0.3 to 0.95, default 0.6), handoff rules (human request, complaint, discount request, repeated low confidence, keywords, VIP tags), handoff message and allowed domains.
  - `GET /traces?conversationId=&outcome=sent|handed_off|failed&cursor=` and `GET /traces/{id}` (`workspace.agent.write`). A trace has input, intent, language, sentiment, retrieved facts, tool calls, guard issues per attempt, reply or suggestion, confidences, prompts and models, tokens, cost and latency.
- New tables `agent.agent_profiles`, `agent.ai_traces` and `knowledge.unanswered_questions`. New topic `agent.unanswered-question-raised.v1`: Knowledge counts repeats of the same normalised question. The endpoints for unanswered questions come in part 3.
- AI gateway: a successful call reports its cost in `pishkhan.cost_irr`. Tests can script the fake provider (`FakeChatScript`).

### Added (Phase 1, BE-1.4 part 1: knowledge base and hybrid search, 2026-11-01)
- New Knowledge module (schema `knowledge`, tables `sources` and `chunks`). The migration enables pgvector and adds `embedding vector(1024)` with an HNSW cosine index, plus a generated `tsvector` over normalised text with a GIN index.
- AI gateway: `IModelRouter.GetEmbeddingGenerator(workspaceId)` uses the first `embed` route (breaker, timeout and usage as for chat). Providers `openai` (OpenAI-compatible embeddings at 1024 dimensions) and `fake` (deterministic, for tests).
- Endpoints under `/api/v1/workspaces/{workspaceId}/knowledge` (`workspace.knowledge.write`):
  - `GET /sources?kind=&q=&status=&cursor=` and `POST /sources` (`faq` with question and answer, or `text` with title and body; 201; 409 `knowledge.limit` at 5,000 sources).
  - `GET`, `PUT` and `DELETE /sources/{id}`, and `POST /sources/{id}/reindex` (202). Product sources can only be enabled or disabled; other edits answer 400 and delete answers 409 `knowledge.product_managed`.
  - `POST /search` takes `query` and `limit` and returns hits (chunk, source, kind, title, text, score, similarity, productId).
- Indexing runs in the Workers (ai group, `knowledge.source-changed.v1`): Persian-aware sentence chunking (about 450 tokens with 50 overlap), embeddings in batches of 32. A failed embedding marks the source `failed` with `knowledge.embedding_failed` or `knowledge.no_embedding_route` and keeps the previous chunks.
- Active catalog products become knowledge sources automatically (title, description, Toman price, variants and stock). Draft, archived and deleted products are removed.
- Search fuses vector and full-text results with Reciprocal Rank Fusion, and falls back to full text when embeddings are unavailable.
- Deferred: file uploads (PDF, DOCX) and URL sources.

### Added (Phase 1, BE-1.7 part 2: form submissions, consents and the contacts export, 2026-11-01)
- Form submissions: a completed flow run that answered questions publishes `automation.form-submitted.v1` (`FormSubmitted`: flow, version, name, run, conversation, answers by question variable). The Workers store it per run on the conversation's contact (`form_submissions`).
  - `GET /api/v1/workspaces/{workspaceId}/crm/forms` lists the flows that collected answers, with the latest name, the number of submissions, the last submission and the answer keys.
  - `GET .../crm/forms/{flowId}/submissions?cursor=&limit=` lists a flow's submissions newest first, and `GET .../contacts/{contactId}/submissions` lists a contact's.
- Consents (`contact_consents`, history only):
  - `POST .../contacts/{contactId}/consents` (`workspace.crm.write`) takes `channel` (phone, sms or bale), `granted` and optional `evidence` (up to 500 characters). It records the decision with source `manual` and the member who recorded it.
  - `GET .../contacts/{contactId}/consents` returns `current` (the latest decision per channel) and the history.
  - The contact list takes `consent=sms|phone|bale` (latest decision is a grant), and segments take a `consent` condition (`granted`, `not_granted`).
- Contacts export (`contact_exports`):
  - `POST .../contacts/exports` takes the list's filters (`q`, `tag`, `segmentId`, `channelAccountId`, `hasPhone`, `consent`) and answers 202. It answers 409 `crm.export.too_many` when the caller already has 3 pending.
  - The Workers (platform group, `conversations.contact-export-requested.v1`) write up to 200,000 contacts to an RTL Excel file in the `exports` bucket. Columns: id, name, username, phone, email, tags, Jalali created date, Jalali last message, and one column per custom field. Then they notify the member in-app (kind `crm_export`).
  - `GET .../contacts/exports` and `GET .../contacts/exports/{exportId}` show the caller's own exports. Status is pending, ready, failed, downloaded or expired; a ready file expires after 24 hours.
  - `POST .../contacts/exports/{exportId}/download` returns a 5-minute URL once. Otherwise it answers 409 `crm.export.not_ready` or `crm.export.gone`.
- Meta data deletion also deletes the customer's form submissions, and the consents of a contact it deletes.
- Migration `CrmFormsConsentsExports`.

### Fixed
- Ids created with an older clock (backfill, a test's fixed clock) could break the creation order of ids being made at the same time with the current clock: each such call restarted the sequence. Older timestamps now keep their own sequence per millisecond (the last 64), so each timeline stays in order.

### Added (Phase 1, BE-1.7 part 1: contacts, tags, custom fields and segments, 2026-11-01)
- Contacts in the panel (Conversations module, `workspace.crm.read`; changes need `workspace.crm.write`):
  - `GET /api/v1/workspaces/{workspaceId}/contacts?q=&tag=&segmentId=&channelAccountId=&hasPhone=&cursor=&limit=` returns contacts newest first, paged by cursor. `q` matches name, username or email, or a phone in any spelling. Each contact carries tags, fields, the channel accounts they wrote to, and `lastSeenAt` (their last message).
  - `GET .../contacts/{contactId}` adds their conversations.
  - `PATCH .../contacts/{contactId}` takes `displayName` or `clearDisplayName`, `tags` (replaces the set) and `fields` (merged; null removes a value). A value for a defined field must fit its type.
- Custom fields: `GET|POST .../crm/fields`, `PUT|DELETE .../crm/fields/{fieldId}`.
  - Each field has a `key` (lowercase, at most 40 characters, fixed once created), `label`, `type` and `sortOrder`.
  - `type` is `text`, `number`, `date` (yyyy-MM-dd), `boolean`, `email`, `phone` (stored as E.164) or `select` (with `options`).
  - At most 50 fields. A key that is taken answers 409 `crm.field.key_taken`. Deleting a definition keeps the contacts' values.
- Tags:
  - `GET .../crm/tags` lists every tag defined or in use, with `defined` and `contactCount`. Automation and the inbox can add tags nobody defined.
  - `PUT .../crm/tags/{name}` sets `color` (gray, red, orange, yellow, green, teal, blue, purple or pink) and `description`.
  - `POST .../crm/tags/{name}/rename` (`newName`) renames the tag on every contact. Renaming onto an existing tag merges the two, and the existing tag's look is kept.
  - `DELETE .../crm/tags/{name}` removes the tag from every contact.
- Segments:
  - `GET|POST .../crm/segments` and `PUT|DELETE .../crm/segments/{segmentId}` manage saved segments; a list or save returns each segment's current `contactCount`. A name that is taken answers 409 `crm.segment.name_taken`.
  - `POST .../crm/segments/preview` counts the matches of a rule without saving it.
  - A rule is `{ match: all|any, conditions: [{ field, op, key?, value? }] }` with up to 20 conditions, evaluated to parameterised SQL. Conditions:
    - `tag`: has, not_has.
    - `field` (with `key`): eq, neq, contains, exists, not_exists, gt, lt (numbers or dates).
    - `name` and `username`: contains, exists, not_exists.
    - `channel_account`: eq, neq.
    - `last_seen` and `created`: within_days, not_within_days.
    - `has_phone`: is.
  - Errors name the condition, for example `rule.conditions[0].op`.
- Migration `CrmTagsFieldsSegments` adds `contact_tags`, `contact_fields` and `contact_segments`. Form submissions, consent records and the Excel export follow in part 2.

### Added (Phase 1, BE-1.12 part 3: AI admin and the workspace plan, 2026-10-31)
- AI settings in the back office (Agent module, migration `AiSettings`: `ai_providers`, `ai_routes`, `prompt_versions`, `prompt_activations`). They are merged over configuration: a provider saved there replaces the configured one of that name (a disabled one is removed), a task with routes there uses only those, saved prompt versions are added to the configured ones, and an activation picks the active version. Every host reloads them every `Ai:Settings:RefreshSeconds` (default 15); the host that saves a change reloads at once. New `IAiSettings` in `Pishkhan.AI`, which the model router and the prompt registry now read.
- `/admin/v1/ai` (reading: SuperAdmin, AIOps, ReadOnly):
  - `GET /providers` (the providers in effect, with `source` configuration or database, `hasApiKey` and the last test); `PUT /providers/{name}` (SuperAdmin, step-up, `reason`; `kind` `openai` or `fake`, `baseUrl`, a write-only `apiKey` stored encrypted, `clearApiKey`, `enabled`); `POST /providers/{name}/test` (SuperAdmin, `{ model }`, returns `ok` and the error). Audited as `ai.provider.update`.
  - `GET /routes` (every task's fallback chain and its source); `PUT /routes/{task}` (AIOps, up to 5 steps over providers in effect, with optional prices in Rial per million tokens; an empty list goes back to configuration). Audited as `ai.routes.update`.
  - `GET /prompts` and `GET /prompts/{key}` (versions newest first, each with its latest eval run and gate result); `POST /prompts/{key}/versions` (AIOps, `text`, `baseVersion`; 201 with the next number); `POST /prompts/{key}/versions/{version}/activate` (AIOps, `evalRunId`, `reason`): 409 `ai.prompt.eval_gate` unless the run is of that key and version, completed, and passes the gate (`Ai:EvalGate`: intent accuracy at least 0.85, error rate at most 0.05, at least 1 case). Audited as `ai.prompt.create` and `ai.prompt.activate`.
  - `GET /eval-sets`, `POST /eval-sets/{name}/import` (AIOps, NDJSON body); `GET /eval-runs?promptKey=&promptVersion=&setId=&page=&pageSize=`, `GET /eval-runs/{runId}` (results, failures first); `POST /eval-runs` (AIOps, `setId`, `promptKey` `intent.classify`, `promptVersion`; 202, runs in the background).
- The admin workspace detail (`GET /admin/v1/workspaces/{workspaceId}`) now fills `planId` and adds `plan` (`source`, `planId`, `code`, `name`, `version`, `trial`, `endsAt`) from the workspace's current subscription or the free plan.
- The AI traces viewer waits for the reply pipeline (BE-1.4), which will write the traces.

### Added (Phase 1, BE-1.11 part 2: subscriptions, plan grants, usage and limits, 2026-10-31)
- Subscriptions (`billing.subscriptions`, migration `Subscriptions`). A workspace without a current subscription is on the published free plan (the cheapest when there are several); when none is published nothing is limited. A subscription keeps the plan version it was granted.
- Back office (Finance or SuperAdmin, step-up, `reason` of 5 to 300 characters, audited in the workspace): `POST /admin/v1/workspaces/{workspaceId}/subscription` grants a published plan version or a trial (`planId`, `trial`, and `endsAt` or `days` from 1 to 1095; a trial needs an end) and replaces the current one; `POST .../subscription/extend` (`days` 1 to 365) and `POST .../subscription/cancel` (back to the free plan). Audited as `subscription.grant`, `subscription.extend` and `subscription.cancel`. `GET /admin/v1/workspaces/{workspaceId}/billing` (SuperAdmin, Finance, Support, ReadOnly) returns the current plan, this period's usage and the subscription history.
- Panel (`workspace.settings.read`): `GET /api/v1/workspaces/{workspaceId}/billing/subscription` (plan, source, trial, end, limits) and `GET .../billing/usage` (every meter's use against its limit this period).
- Usage: `billing.usage-recorded.v1` (`UsageRecorded`: workspace, meter, quantity, time, optional subject) is counted per billing period by the Workers (platform group). A period is a Jalali month in Tehran time. `ai_conversations` counts each conversation once per period. Producers: the AI reply agent (BE-1.4) will emit `ai_messages` and `ai_conversations`.
- Limits answer 402 `billing.plan_limit`: inviting a member when members plus pending invitations fill the plan's `seats`, and connecting a new Instagram account over `channel_accounts` (the connect call when every account is in use and none needs reconnecting; the callback returns `status=error&reason=plan_limit` for a new account). AI usage over its limit is not blocked in Phase 1 (soft overage is BE-2.8).
- `IEntitlements` (Billing contracts) for other modules, cached 5 minutes and cleared on grants, extensions, cancellations and plan publishes. `IMembershipQueries.CountSeatsAsync`. The Workers host runs the Billing module.

### Fixed
- The Api host did not start in Development (service validation): the Billing module registered its back-office audit writer, which needs the staff directory, in every host. It is now registered by `AddBillingBackoffice()` in the AdminApi only, and the integration tests' Api host validates every registration at startup.

### Added (Phase 1, BE-1.11 part 1: plans and the public price list, 2026-10-31)
- New Billing module (`billing` schema, migration `Initial`) with versioned plans. A plan is a `code` with numbered versions; each version is `draft`, `published` or `archived`, and only one version per code can be a draft or published at a time. Prices are Rial (`monthly`, optional `annual` and `overagePerConversation` as `Money`); `limits` are per meter (`ai_conversations`, `ai_messages`, `channel_accounts`, `seats`, `receipt_verifications`, `sms`; `null` means unlimited) and `features` are the price-list lines (`key`, `label`, `included`, optional `limit` text).
- Back office (`/admin/v1`): `GET /plans?code=&status=&page=&pageSize=` and `GET /plans/{planId}` (SuperAdmin, Finance, Support, ReadOnly). Finance or SuperAdmin: `POST /plans` (201; 409 `billing.plan.code_taken`), `PUT /plans/{planId}` (drafts only, otherwise 409 `billing.plan.not_draft`), `POST /plans/{planId}/versions` (copies a version into a new draft; 409 `billing.plan.draft_exists`), `POST /plans/{planId}/publish` (step-up, optional `reason`; archives the previously published version of the code) and `POST /plans/{planId}/archive` (step-up, `reason` required). Audited as `plan.create`, `plan.update`, `plan.version`, `plan.publish` and `plan.archive`.
- `GET /api/v1/public/plans` (anonymous, the website's price list) returns the published public plans in sort order, cached 10 minutes and refreshed on every publish or archive.
- The Api, AdminApi and Migrator hosts run the Billing module.

### Fixed
- Cache invalidations from another host never reached the cache the endpoints read. The Redis cache that `AddStackExchangeRedisCache` registers asks the container for `HybridCache` when it first connects, and that happens inside HybridCache's own constructor, so each host built two HybridCache instances and, depending on how a reader got its cache (an endpoint parameter or a service's constructor) and which resolved first, it could read a different one from the one the `CacheInvalidated` consumer cleared. Such entries only went stale until their TTL. The platform now registers the plain `RedisCache`, and a test checks that a host holds one HybridCache.
- Broadcast consumer groups now carry a random instance id. Several hosts in one process (the integration tests) shared one group and split its partitions, so each missed most broadcasts.

### Added (Phase 1, BE-1.12 part 2: admin users, 2026-10-31)
- `GET /admin/v1/users?q=&status=&page=&pageSize=` (SuperAdmin, Support, Finance, ReadOnly) lists panel users newest first. `q` is a user id, a phone (exact, in any spelling) or text in the display name; `status` is `active` or `suspended`. Rows carry the masked phone, 2FA, last sign-in and the number of signed-in devices.
- `GET /admin/v1/users/{userId}` (same roles) adds the user's workspaces with role and status, and their signed-in devices (first and last use, expiry, IP, user agent, sign-in method).
- `POST /admin/v1/users/{userId}/sessions/revoke`, `/block` and `/unblock` (Support or SuperAdmin, step-up, `reason` of 5 to 300 characters). Revoking and blocking end every session at once, including access tokens already issued. A blocked user cannot sign in or refresh. Audited as `user.sessions_revoke`, `user.block` and `user.unblock`.
- `RequireStepUp` moves to `Pishkhan.Web.Auth` so any module's admin endpoints can use it. New `IStaffDirectory` (Backoffice contracts) and `IMembershipQueries.GetMembershipsOfUserAsync`.
- Integration tests use a 1-second OTP resend cooldown so a test can sign the same phone in twice.

### Added (Phase 1, BE-1.12 part 1: admin workspace search and detail, 2026-10-31)
- `GET /admin/v1/workspaces?q=&status=&industry=&createdFrom=&createdTo=&page=&pageSize=` (SuperAdmin, Support, Finance, ReadOnly) lists workspaces newest first with `totalCount`. `q` is a workspace id, the owner's phone (exact, in any spelling) or text found in the name, the slug or an Instagram username (a leading @ is ignored). Each row has the owner (masked phone), the member count and the channels with their status.
- `GET /admin/v1/workspaces/{workspaceId}` (same roles) adds the time zone, plan, deletion dates, the members with their roles, the channels' status history fields (`statusReason`, `statusChangedAt`, `tokenExpiresAt`) and the number of pending invitations. Deleted workspaces are still found.
- The AdminApi host now runs the Tenancy module and the Identity and Channels directories (`AddIdentityDirectory`, `AddChannelsDirectory`). `IUserDirectory` gains `FindByPhoneAsync` and `GetManyAsync`; `IChannelDirectory` gains `FindWorkspacesByUsernameAsync` and `ListForWorkspacesAsync`.

### Changed
- `Backoffice:UserPanelUrl` defaults to `http://localhost:3001`, the user panel's development port (it was 3000).
- Channel notifications link to `/channels`, the user panel's page (it was `/settings/channels`).

### Fixed
- The OpenAPI document listed the `workspaceId` path parameter of `GET /workspaces/{workspaceId}/audit` twice.

### Added (Phase 1, support sessions, 2026-10-31)
- `POST /admin/v1/workspaces/{workspaceId}/support-sessions` (Support or SuperAdmin, step-up, `reason` of 5 to 300 characters, optional `userId`, by default the owner) returns a one-time link `{UserPanelUrl}/api/auth/impersonation?token=…` valid 2 minutes (`Backoffice:UserPanelUrl`). Audited in the workspace as `support_session.grant` with the reason.
- `POST /api/v1/auth/support-session` (`token`) exchanges it once for a 30-minute access token as that member, with no refresh token. The token carries the staff member in `act` and `act_name`; every change under it is audited as staff on behalf of the member, which the owner's audit view shows as «support». The exchange is audited as `support_session.start` and notifies the owner. `POST /auth/logout` ends the session.
- Under a support session, password, TOTP and session changes, and asking for or cancelling workspace deletion, answer 403 `auth.support_session.not_allowed` (endpoint filter `DenySupportSession`, for billing and other sensitive actions as they arrive).

### Added (Phase 1, BE-1.14 notifications v1, 2026-10-31)
- New Notifications module (`notifications` schema). Handoffs, Instagram connection problems (reconnect needed, token expiring, access removed in Instagram) and `notifications.notification-requested.v1` (for example the automations' «notify the team» action) become notifications for the members who should act: those who can reply for handoffs and automations, those who manage channels for connection problems. A member who hands a conversation over is not notified about it.
- `GET /workspaces/{workspaceId}/notifications?unread=&cursor=&limit=` lists the caller's notifications, newest first, with `unreadCount`. `POST /workspaces/{workspaceId}/notifications/{notificationId}/read` and `POST /workspaces/{workspaceId}/notifications/read-all` mark them read.
- `GET` and `PUT /workspaces/{workspaceId}/notification-preferences`: per kind (`handoff`, `automation`, `channel`) the caller turns `inApp`, `push` and `sms` on or off. Defaults: in-app and push on, SMS on only for connection problems. SMS is offered for `handoff` and `channel` only, and at most one SMS per kind every 30 minutes is sent. `push` is stored now and used once web push ships.
- `IdempotentHandler` gains `AfterCommitAsync` for side effects outside the database (sent at most once).

### Added (Phase 1, workspace deletion with a grace period, 2026-10-31)
- `POST /workspaces/{workspaceId}/deletion` (owner, `workspace.delete`) with `confirmName` equal to the workspace name returns 202 and the workspace with status `pending_deletion` and `deletionScheduledFor` (30 days later). Members lose access at once (its endpoints answer 404) and automations stop sending. A wrong name gets 400 `tenancy.deletion.confirm_mismatch`.
- `DELETE /workspaces/{workspaceId}/deletion` cancels while the deletion is pending (owner; other members 403, non-members 404, nothing pending 409). The workspace is active again.
- `GET /workspaces` lists workspaces pending deletion too, so the owner can find and cancel them. `WorkspaceResponse` gains `deletionScheduledFor`.
- The daily `tenancy.workspace-purge` job marks workspaces past the grace period `deleted` and emits the new `tenancy.workspace-deleted.v1` event. The data is then deleted: every row with the workspace's `workspace_id` in the module schemas, and the workspace's files with their stored objects. The audit log is kept, and the workspace row stays as a tombstone with `deleted_at` (migration `WorkspaceDeletion`).

### Added (Phase 1, BE-1.10 part 1: dashboard, 2026-10-31)
- New Analytics module (`analytics` schema) with hourly rollups per workspace (`analytics.hourly_metrics`): messages received, new conversations, replies by rule, by AI and by a team member, and handoffs. The platform worker group fills it from `conversations.inbound-message-received`, `conversations.outbound-message-requested` (sends postponed by the rate limit are not counted twice) and `conversations.handoff-requested`, each counted once.
- `GET /api/v1/workspaces/{workspaceId}/dashboard?range=today|7d|30d` (permission `workspace.analytics.read`) returns totals with the automated share, one entry per Tehran day, and the overnight summary. The summary covers the latest stretch of closed business hours in the last 24 hours and includes a ready Persian sentence such as «از ۲۱:۳۰ تا ۰۹:۳۰: ۷ پیام دریافت شد، ۶ پاسخ خودکار داده شد و ۱ گفتگو به تیم سپرده شد.». The response also has the live inbox (open, handed off, unread, oldest waiting handoff) and channel health. Rollup figures are cached for up to 5 minutes (`dash:{ws}:{range}`).
- Overnight periods follow the hourly buckets, which start at :30 Tehran time (UTC+03:30).
- Not yet: leads captured and forms (BE-1.7), knowledge score and unanswered questions (BE-1.4), orders and revenue (Phase 2), quota usage (billing).

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
