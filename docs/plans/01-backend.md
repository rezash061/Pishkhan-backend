# Backend Implementation Plan (.NET 10)

**Read first:** `00-overview.md` (assumptions A1–A7, API conventions, phase map, definition of done).
**Implements:** every server-side capability behind the user panel, admin panel and website, plus all background processing.
**Stack (fixed):** .NET 10, ASP.NET Core Minimal APIs, PostgreSQL 18 + pgvector, Redis 8 via HybridCache, Kafka 4 via MassTransit 8.5.x, .NET Worker hosts, ClamAV.

---

## 1. Architecture decision

### 1.1 Recommendation: a modular monolith with separately scalable hosts

One .NET solution, split into **business modules** with hard boundaries, compiled into **five deployable hosts** that share the same modules. Modules talk to each other in-process through small public contracts for queries, and through **Kafka integration events** for anything that happens "after" something else.

```
Hosts (deployables)          Modules (bounded contexts, one PostgreSQL schema each)
─────────────────────        ──────────────────────────────────────────────────────
Pishkhan.Api        ───┐     Identity      Tenancy       Channels      Conversations
Pishkhan.AdminApi   ───┤     Automation    Agent (AI)    Knowledge     Catalog
Pishkhan.Ingress    ───┼──▶  Orders        Payments      Shipping      Crm
Pishkhan.Workers    ───┤     Billing       Files         Notifications Content (CMS)
Pishkhan.Migrator   ───┘     Analytics     Audit         Backoffice
```

Inside each module, code is organized as **vertical slices** (one folder per use case: endpoint + request/response + validator + handler) on top of a small **domain model** where the rules are rich (Automation, Orders, Payments, Billing, Conversations). Simple CRUD slices talk to EF Core directly; there is no generic repository layer.

### 1.2 Why this and not the alternatives

| Option | Verdict | Reason |
|---|---|---|
| **Microservices from day one** | Rejected | A small team (and AI agents) would spend most of their effort on distributed transactions, service discovery, versioned contracts between 15+ services and running them in an Iranian data center with few managed services. None of the load in the first two years needs it. |
| **Classic layered monolith** (Controllers / Services / Repositories) | Rejected | Layers cut across features, so every change touches four folders and modules leak into each other. It becomes hard to split later. |
| **Modular monolith, single process** | Partly | Right code structure, but webhook ingress, outbound sending and AI calls have very different scaling and network needs (assumption A2). |
| **Modular monolith + separate hosts + Kafka** | **Chosen** | One codebase, one build, compile-time checks, in-process transactions inside a module. Hot paths still scale independently: `Ingress` (bursty, must answer Meta fast, sits in the international zone), outbound sender (rate limited per Instagram account), AI workers (slow, expensive, I/O-bound), AV scanning. Because modules already communicate through Kafka events and own their schema, any module can be extracted into its own service later by moving its consumers into a new host. |

**Additional reasons specific to this product**

- The report's conversation engine is "rules first, AI second" with every message stored with intent, confidence and outcome (report 8.2). That is a pipeline of steps with different latencies, which maps naturally to Kafka topics consumed by worker groups.
- Ordering matters per conversation (a customer's messages must be processed in order) but not globally. Kafka partitioning by `conversationId` gives exactly that, and lets throughput grow by adding partitions and worker replicas.
- Sanctions and filtering (A2) mean the Meta-facing parts must be deployable in a different network zone from the data. Separate hosts make that a deployment decision.
- AI agents implementing this plan work best on a codebase where a feature lives in one folder and module boundaries are enforced by tests.

### 1.3 Boundary rules (enforced by architecture tests)

1. A module references other modules only through their `*.Contracts` project (query interfaces, DTOs, integration event types). Never their internals or their `DbContext`.
2. Each module has its own `DbContext` mapped to its own PostgreSQL schema. No cross-schema joins and no cross-schema foreign keys. Reference other modules' entities by ID.
3. A command handler changes the data of one module in one transaction. Side effects in other modules happen through integration events written to that module's outbox in the same transaction.
4. Hosts contain no business logic: only composition, middleware and configuration.
5. `Domain` folders depend on nothing but `Pishkhan.SharedKernel`.

### 1.4 Libraries (all free/open-source licenses)

| Purpose | Library | Note |
|---|---|---|
| ORM | EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL` | `EFCore.NamingConventions` for snake_case |
| Vectors | `Pgvector.EntityFrameworkCore` | HNSW indexes |
| Raw SQL hot paths | `Dapper` | Only for read models and the outbox relay |
| Validation | `FluentValidation` | |
| Mapping | `Riok.Mapperly` (source generator) | Not AutoMapper |
| Handler dispatch | Plain interfaces `ICommandHandler<,>` / `IQueryHandler<,>` + `Scrutor` decorators | Not MediatR |
| Messaging | `MassTransit` **8.5.x** + `MassTransit.Kafka` 8.5.x | Pin with `[8.5,8.6)` |
| Scheduling | `Quartz.Extensions.Hosting` with PostgreSQL AdoJobStore (clustered) | Recurring jobs only |
| Caching | `Microsoft.Extensions.Caching.Hybrid` + `Microsoft.Extensions.Caching.StackExchangeRedis` | |
| Redis direct | `StackExchange.Redis` | Locks, rate limits, Lua scripts |
| Resilience | `Microsoft.Extensions.Http.Resilience` (Polly v8) | Every outbound HTTP client |
| AI | `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`) + OpenAI-compatible client | Assumption A3 |
| Antivirus | `nClam` | clamd INSTREAM over TCP |
| Object storage | `AWSSDK.S3` | S3-compatible endpoints |
| Image processing | `SkiaSharp` (MIT) | Re-encode, strip metadata, thumbnails |
| Excel | `ClosedXML` (MIT) | Export/import |
| Logging | `Serilog` → OpenTelemetry/Seq | |
| Telemetry | `OpenTelemetry.*` (traces, metrics, logs) | OTLP exporter |
| Realtime | ASP.NET Core SignalR + `Microsoft.AspNetCore.SignalR.StackExchangeRedis` backplane | |
| Tests | xUnit v3, `Shouldly` or `AwesomeAssertions`, `NSubstitute`, `Testcontainers` (PostgreSQL, Redis, Kafka, MinIO, ClamAV), `NetArchTest.Rules`, `Bogus`, `WireMock.Net` | Not FluentAssertions 8 |
| Load tests | k6 scripts in `tests/load` | |

---

## 2. Solution layout

```
backend/
├── Pishkhan.slnx
├── global.json                          # pin .NET 10 SDK
├── Directory.Build.props                # nullable, TreatWarningsAsErrors, analyzers, LangVersion
├── Directory.Packages.props             # central package management, all versions pinned
├── .editorconfig
├── openapi/                             # exported OpenAPI docs (committed)
├── src/
│   ├── BuildingBlocks/
│   │   ├── Pishkhan.SharedKernel/       # Entity, AggregateRoot, Result<T>, Error, StronglyTypedId, Money, PhoneNumber, IClock
│   │   ├── Pishkhan.Persian/            # PersianNormalizer, Finglish, digits, Jalali, ZWNJ, IBAN(Sheba)/card validators
│   │   ├── Pishkhan.Contracts/          # integration event base types, envelope, topic registry
│   │   ├── Pishkhan.Infrastructure/     # EF base, tenant filter, outbox/inbox, timers, caching, locks, rate limits,
│   │   │                                # MassTransit setup, storage, AV client, secret protection, telemetry
│   │   ├── Pishkhan.AI/                 # model gateway, routing, metering, prompt registry, guards
│   │   └── Pishkhan.Web/                # endpoint conventions, ProblemDetails, auth policies, idempotency filter
│   ├── Modules/
│   │   ├── Identity/      Pishkhan.Modules.Identity/        Pishkhan.Modules.Identity.Contracts/
│   │   ├── Tenancy/       ...                               ...
│   │   ├── Channels/      (Instagram, Bale, Sms, WebWidget, Sandbox adapters inside)
│   │   ├── Conversations/
│   │   ├── Automation/
│   │   ├── Agent/
│   │   ├── Knowledge/
│   │   ├── Catalog/
│   │   ├── Orders/
│   │   ├── Payments/
│   │   ├── Shipping/
│   │   ├── Crm/
│   │   ├── Billing/
│   │   ├── Files/
│   │   ├── Notifications/
│   │   ├── Content/
│   │   ├── Analytics/
│   │   ├── Audit/
│   │   └── Backoffice/
│   └── Hosts/
│       ├── Pishkhan.Api/
│       ├── Pishkhan.AdminApi/
│       ├── Pishkhan.Ingress/
│       ├── Pishkhan.Workers/
│       └── Pishkhan.Migrator/
├── tests/
│   ├── Pishkhan.ArchitectureTests/
│   ├── Pishkhan.Persian.Tests/
│   ├── Pishkhan.Modules.<Name>.Tests/   # unit tests per module
│   ├── Pishkhan.IntegrationTests/       # Testcontainers, WebApplicationFactory, Kafka end-to-end
│   └── load/                            # k6
└── tools/
    ├── Pishkhan.EvalRunner/             # CLI: runs the Persian evaluation set against the AI pipeline
    └── Pishkhan.Seed/                   # dev seed data (fake Persian shops)
```

**Module project internals**

```
Pishkhan.Modules.Automation/
├── Domain/                 # aggregates, value objects, domain events, invariants
├── Features/
│   ├── Rules/CreateRule/   # CreateRuleEndpoint.cs, CreateRuleCommand.cs, CreateRuleValidator.cs, CreateRuleHandler.cs
│   ├── Rules/ListRules/
│   └── Engine/             # rule evaluation, flow runtime
├── Consumers/              # MassTransit consumers for integration events
├── Jobs/                   # Quartz jobs owned by the module
├── Persistence/            # AutomationDbContext, configurations, migrations
├── Integration/            # implementations of other modules' needs (adapters)
└── AutomationModule.cs     # AddAutomationModule(IServiceCollection, IConfiguration), MapAutomationEndpoints(...)
```

Each module exposes `Add<Name>Module()` and `Map<Name>Endpoints()` / `Map<Name>AdminEndpoints()` / `Add<Name>Consumers()` so each host picks what it needs.

### 2.1 Hosts

| Host | Contains | Zone | Scaling signal |
|---|---|---|---|
| `Pishkhan.Api` | Panel and public REST endpoints, SignalR hub, PSP callbacks, payment-link redirects | Domestic | CPU, RPS |
| `Pishkhan.AdminApi` | Back-office endpoints only; listens on the private network | Domestic, private | Low |
| `Pishkhan.Ingress` | Meta webhook endpoint only: verify, dedupe, produce, return 200 | International edge | RPS bursts |
| `Pishkhan.Workers` | All consumers and jobs, grouped; `Workers:Groups` config chooses which groups run in a given deployment | Both (by group) | Kafka consumer lag per group |
| `Pishkhan.Migrator` | Applies EF migrations for all module schemas, seeds reference data, then exits | Domestic | Runs per release |

**Worker groups** (one image, deployed N times with different `Workers__Groups`):

| Group | Consumers / jobs | Zone |
|---|---|---|
| `ingest` | raw webhook → normalized inbound events, contact upsert | Domestic |
| `engine` | conversation engine: rules, flows, routing to AI or human | Domestic |
| `ai` | AI agent replies, classification, embeddings, transcription, OCR | Domestic (calls AI gateway; may egress via edge) |
| `outbound` | sends to Instagram / Bale / SMS with per-account rate limiting | International edge for Instagram, domestic for SMS and Bale |
| `media` | media download, AV scan, image processing | Domestic |
| `commerce` | orders, payments, receipts, shipping, price updates | Domestic |
| `platform` | outbox relay, timers, Quartz recurring jobs, notifications, analytics rollups, billing metering, audit sink | Domestic |

---

## 3. Cross-cutting foundations

### 3.1 Multi-tenancy

- Tenant = **Workspace**. Every tenant-owned table has `workspace_id uuid not null` as the first column of its main indexes.
- `ITenantContext` is resolved from the route (`/workspaces/{workspaceId}`), validated against the caller's memberships (cached), and set for the request. Consumers set it from the event envelope's `workspaceId`.
- EF Core global query filter on every entity implementing `ITenantOwned`; an architecture test fails if a tenant-owned entity lacks it. A save interceptor rejects writes whose `workspace_id` differs from the current tenant.
- Integration tests include a **cross-tenant access suite**: for every workspace-scoped endpoint, a member of workspace A calling with workspace B's IDs must get `404`.
- Phase 3 hardening adds PostgreSQL Row-Level Security on the most sensitive tables (contacts, messages, orders, payments) as defense in depth.
- Agency mode (Phase 3): an agency is an `Organization` owning many workspaces; membership can be granted at organization level.

### 3.2 Identifiers, time, money, Persian text

- IDs: UUID v7 (`Guid.CreateVersion7()`), wrapped in strongly typed IDs (`readonly record struct ContactId(Guid Value)`) with EF value converters.
- Time: store `timestamptz` UTC only; `IClock` abstraction; Jalali conversion only at the edges (exports, generated messages) using `System.Globalization.PersianCalendar`; business-hours logic uses `Asia/Tehran` time zone.
- Money: `Money(long Amount, Currency Currency)` in minor units, IRR default. Never `double`. Rounding rules for Toman display in one place.
- Persian text: every inbound text passes through `PersianNormalizer` before storage in a `normalized_text` column (original kept):
  - Arabic `ي ك ة ۀ` → Persian `ی ک ه هٔ`, Arabic-Indic and Persian digits → ASCII digits in the normalized copy, remove kashida and diacritics, normalize ZWNJ (U+200C) and spaces, collapse repeated letters beyond 2 ("سلاااام" → "سلاام"), unify punctuation.
  - Phase 2 adds Finglish detection and transliteration (section 9, BE-2.3).
- Validators in `Pishkhan.Persian`: Iranian mobile numbers (normalize to E.164 `+989xxxxxxxxx`), national ID (کد ملی) checksum, bank card number (Luhn + BIN → bank), Sheba/IBAN (`IR` + mod 97), 10-digit postal code.

### 3.3 API conventions

Implements section 6 of the overview. Additionally:

- Minimal API endpoint groups per module, versioned paths `/api/v1`, `/admin/v1`, `/api/v1/public`.
- Built-in `Microsoft.AspNetCore.OpenApi` generates three documents (`api`, `admin`, `public`); a build step exports them to `backend/openapi/`. Every endpoint has a summary, tags, response types and examples with Persian data.
- `Idempotency-Key` filter: stores `(key, user, route, request hash) → response` in the Redis state instance for 24 hours; same key with different body → `422`.
- Output caching (Redis-backed) for public GET endpoints.
- Response compression (Brotli/Gzip) and `System.Text.Json` source generation for all DTOs.
- Problem Details everywhere, with `traceId`. Validation errors return message keys, never English sentences.

### 3.4 Security

Target: OWASP ASVS Level 2, OWASP API Security Top 10.

**Authentication (users)**
- Primary login: phone OTP via SMS. OTP is 6 digits, stored as HMAC in Redis state with 2-minute TTL, max 5 attempts, resend cooldown 60 s, rate limits per phone (5/hour) and per IP (20/hour), plus ALTCHA proof-of-work token from the frontend on the request-OTP call.
- Optional password (ASP.NET Core Identity hasher, PBKDF2 with high iteration count) and optional TOTP 2FA; TOTP is mandatory for workspace Owners once the workspace has paid plans or connected payment settings.
- Tokens: access JWT (ES256, 10-minute lifetime, claims: `sub`, `sid`, `ws` memberships hash, `amr`), refresh token opaque 256-bit, stored hashed, **rotated on every use with reuse detection** (reuse revokes the whole session family). Sessions listed and revocable in the panel.
- Signing keys rotated; JWKS endpoint for internal validation.

**Authentication (staff)**
- Separate `backoffice` user store, separate JWT audience and signing key, `AdminApi` rejects user tokens and vice versa.
- Password + mandatory TOTP (WebAuthn in Phase 3), 15-minute idle timeout, 8-hour absolute, step-up re-authentication for dangerous actions (impersonation, refunds, deleting data, changing AI provider keys).
- `AdminApi` is not internet-facing (private network / VPN); IP allowlist at the ingress controller as a second layer.

**Authorization**
- Permission-based policies (`workspace.inbox.reply`, `workspace.catalog.write`, `workspace.billing.manage` ...). Roles (Owner, Admin, Agent, Viewer, plus custom in Phase 3) map to permission sets. Checks happen in the endpoint filter and again in handlers for resource ownership.
- Staff roles: SuperAdmin, Support, Finance, ContentEditor, AIOps, ReadOnly.
- **Impersonation:** staff obtains a time-boxed (30 min) impersonation token with `act` claim; every request under it is audited with both identities; the workspace owner sees an "accessed by support" entry. Impersonation cannot view decrypted payment credentials or change billing.

**Secrets and data protection**
- Instagram tokens, PSP merchant keys, SMS keys, Bale bot tokens: envelope encryption (AES-256-GCM, per-record data key encrypted by a master key from HashiCorp Vault transit, or an env-provided key in dev) through `ISecretProtector`. Never cached decrypted in Redis; at most 5 minutes in process memory.
- Contact phone numbers and seller bank card / Sheba numbers: encrypted columns plus HMAC "blind index" columns for exact-match lookup.
- ASP.NET Core Data Protection keys persisted in PostgreSQL and encrypted with a certificate.
- TLS everywhere, including Kafka (SASL_SSL), Redis (TLS + ACL users per host), PostgreSQL (`sslmode=verify-full`).

**Inbound integrations**
- Meta webhooks: verify `X-Hub-Signature-256` HMAC with the app secret using constant-time comparison before any parsing; reject bodies over 1 MB; handle the `hub.challenge` verification GET.
- PSP callbacks: never trust callback parameters; always call the PSP verify endpoint server-side and compare amount and order.
- **SSRF protection:** all server-side URL fetches (Instagram media, feed import, link previews) go through `SafeHttpClient`: HTTPS only, DNS resolved and checked against private/link-local/loopback ranges after resolution, host allowlist where possible (Meta CDN hosts for media), size and time limits, no redirects to disallowed hosts.

**AI-specific**
- Prompt-injection defense: customer text is always passed as user content, never concatenated into system instructions; retrieved knowledge is wrapped and labeled as data; tool calls are validated in code (a tool cannot change price, discount beyond policy, or workspace).
- Prices, stock and totals in AI replies come from tool results, and an output guard verifies every number that looks like a price against the catalog before sending (section 8.6).

**Platform hardening**
- Rate limiting: ASP.NET Core rate limiter with Redis-backed sliding window policies per user, per workspace, per IP, and stricter for auth and public endpoints.
- Security headers on all hosts (HSTS, `X-Content-Type-Options`, `Referrer-Policy`, `frame-ancestors 'none'` for the API).
- Audit log (module `Audit`): append-only table of who did what, from where, before/after for sensitive entities; written via events; staff and owners can query their own scope.
- Dependency scanning (`dotnet list package --vulnerable` in CI, plus Trivy for images), secret scanning, SAST (CodeQL or Semgrep).
- Data retention: messages kept per plan (default 12 months), receipts 24 months, deleted on workspace deletion after 30-day grace; Meta data-deletion callback endpoint implemented (required by Meta).

### 3.5 Performance targets and techniques

| Path | Target |
|---|---|
| Meta webhook ingress (receive → 200) | p99 < 150 ms; one pod handles ≥ 1,000 events/s |
| Rule-based reply (webhook → Graph API send call) | p95 < 2 s |
| AI reply (webhook → send) | p95 < 8 s, excluding provider incidents |
| Panel API reads / writes | p95 < 200 ms / < 400 ms |
| Inbox realtime update after inbound message | < 1 s |
| Initial capacity plan | 10,000 workspaces, 50 M messages/month, 2,000 webhook events/s peak (viral post) |

Techniques:
- Ingress does no database work: verify, dedupe in Redis, produce to Kafka (idempotent producer, `acks=all`, `linger.ms` 5), respond.
- Npgsql connection pooling + PgBouncer (transaction mode) in front of PostgreSQL; `AsNoTracking` for reads; compiled queries for hot paths; projections to DTOs instead of loading aggregates for reads; keyset pagination only.
- `conversations.messages` and `analytics.events` are **range-partitioned by month** on `created_at`; partitions created ahead by a Quartz job; old partitions detached per retention.
- Indexes designed per query (each work package lists them); `pg_stat_statements` reviewed in CI load tests.
- Read replica for analytics and exports from Phase 3 (`ReadOnlyDbContext` pointing at the replica).
- Kafka: partition by the key that needs ordering; consumers batch where order is not needed (analytics, audit).
- Redis caching per section 5; HybridCache stampede protection on hot keys.
- Outbound sending paced with token buckets per Instagram account so viral posts degrade to "slower" instead of "blocked by Meta".
- Load tests (k6) in CI nightly against the docker-compose stack, with thresholds above; a release fails if p95 regresses by more than 20%.

### 3.6 Observability

- OpenTelemetry traces across HTTP → Kafka → consumer → outbound (propagate `traceparent` in Kafka headers; MassTransit does this with its OpenTelemetry instrumentation).
- Metrics: RED metrics per endpoint, consumer lag per group, outbox backlog, timer backlog, AI tokens/cost/latency per model and workspace, AV scan duration and results, Instagram API error rates per error code, cache hit ratio.
- Logs: structured Serilog, PII-redacting enricher (phone numbers, card numbers, tokens masked).
- Health checks: `/health/live`, `/health/ready` (PostgreSQL, Redis both instances, Kafka, S3, ClamAV, AI gateway).
- Dashboards (Grafana) and alerts: consumer lag > 30 s, DLQ growth, AV down, Meta error spike, AI error rate > 5%, outbox backlog > 1,000.

---

## 4. Data stores

### 4.1 PostgreSQL schemas (one per module)

| Schema | Key tables (main columns only) |
|---|---|
| `identity` | `users` (id, phone_e164_enc, phone_bidx, password_hash?, totp_secret_enc?, status), `sessions` (id, user_id, family_id, refresh_hash, expires_at, revoked_at, ip, ua), `otp_audit` |
| `tenancy` | `organizations`, `workspaces` (id, org_id?, name, slug, industry, timezone, plan_id, status), `memberships` (user_id, workspace_id, role, permissions_override), `invitations`, `api_keys` (Phase 3) |
| `channels` | `channel_accounts` (id, workspace_id, kind [instagram, bale, sms, web, sandbox], external_id, username, status, token_enc, token_expires_at, scopes, rate_profile), `webhook_subscriptions`, `channel_health_events` |
| `conversations` | `contacts_channel_identities` (contact_id, channel_account_id, external_user_id), `conversations` (id, workspace_id, channel_account_id, contact_id, status [open, pending, handed_off, closed], assignee_id, ai_paused, last_inbound_at, last_outbound_at, window_expires_at), `messages` **partitioned** (id, created_at, workspace_id, conversation_id, direction, kind [text, image, audio, video, file, story_reply, share, card, system], text, normalized_text, media_file_id, external_id, status, intent, confidence, handled_by [rule, ai, human], automation_rule_id, ai_trace_id), `notes`, `quick_replies` |
| `automation` | `rules` (id, workspace_id, channel_account_id, trigger_kind, trigger_config jsonb, action_config jsonb, post_external_id?, priority, enabled, version), `flows` (id, workspace_id, name, graph jsonb, version, status), `flow_runs` (id, flow_id, conversation_id, current_node, variables jsonb, status), `comment_events` (dedupe of comment replies per Meta's one-private-reply rule) |
| `agent` | `agent_profiles` (workspace_id, name, tone, persona, language_prefs, confidence_threshold, handoff_rules jsonb, never_say jsonb), `ai_traces` (id, workspace_id, conversation_id, task, model, prompt_version, retrieved_chunk_ids, tool_calls jsonb, input_tokens, output_tokens, cost_irr, latency_ms, confidence, outcome), `eval_sets`, `eval_cases`, `eval_runs`, `eval_results` |
| `knowledge` | `sources` (id, workspace_id, kind [faq, document, url, catalog, feed, onboarding_chat], status), `chunks` (id, workspace_id, source_id, text, normalized_text, tsv tsvector, embedding vector(N), metadata jsonb), `unanswered_questions` |
| `catalog` | `products` (id, workspace_id, title, description, category_id, tags, status, pricing_mode [fixed, formula], base_price, formula jsonb?, media_file_ids), `variants` (id, product_id, sku, attributes jsonb, price, stock, stock_policy), `categories`, `price_change_batches` (Phase 2), `rate_sources` (Phase 3) |
| `orders` | `orders` (id, workspace_id, contact_id, conversation_id, number, status, subtotal, shipping_cost, discount, total, address jsonb, source_post_id?), `order_items`, `order_status_history` |
| `payments` | `merchant_accounts` (workspace_id, provider, merchant_id_enc, status), `seller_bank_accounts` (card_enc, card_bidx, sheba_enc, bank_code), `payment_links` (id, order_id, token, amount, status, expires_at), `payment_attempts`, `receipts` (id, workspace_id, order_id?, file_id, extracted jsonb, amount, paid_at, reference_no, dest_card_last4, image_phash, status, confidence, decided_by) |
| `shipping` | `carriers`, `shipping_rates`, `shipments` (order_id, carrier, tracking_code, status, events jsonb) |
| `crm` | `contacts` (id, workspace_id, display_name, phone_enc, phone_bidx, phone_consent_at, bale_linked, tags, custom_fields jsonb, lead_score, first_seen_at, last_seen_at), `tags`, `segments` (rule jsonb), `forms`, `form_submissions`, `exports` |
| `billing` | `plans`, `plan_entitlements`, `subscriptions`, `usage_records` (workspace_id, meter, quantity, period), `wallets`, `wallet_transactions`, `invoices`, `invoice_lines`, `coupons`, `installment_plans` |
| `files` | `files` (id, workspace_id?, purpose, original_name, content_type_declared, content_type_detected, size, sha256, storage_key, bucket, status [pending_scan, clean, infected, scan_failed, rejected], scan_engine_version, scanned_at, derived jsonb) |
| `notifications` | `notifications`, `push_subscriptions`, `sms_messages` |
| `content` | `pages`, `posts`, `post_categories`, `faqs`, `help_articles`, `case_studies`, `media`, `redirects` |
| `analytics` | `events` **partitioned**, `daily_workspace_metrics`, `attribution_paths` (Phase 3) |
| `audit` | `audit_entries` (append-only, partitioned by month) |
| `backoffice` | `staff_users`, `staff_sessions`, `feature_flags`, `announcements`, `industry_templates`, `bank_receipt_templates`, `ai_model_routes`, `prompt_templates` |
| `platform` | `outbox_messages`, `processed_messages` (inbox), `timers`, Quartz tables (`qrtz_*`) |

Rules for every table: snake_case; `created_at`, `updated_at` (`timestamptz`, set by interceptor); `xmin`-based optimistic concurrency (`UseXminAsConcurrencyToken`) on aggregates; soft delete (`deleted_at`) only where the product needs restore (contacts, products, rules); FK constraints only within a schema.

### 4.2 Object storage buckets

| Bucket | Content | Access |
|---|---|---|
| `quarantine` | Every file before AV scan | Workers only |
| `files` | Clean files and derivatives | Signed GET URLs (5 min) via `usercontent` domain |
| `public` | Website and CMS assets, storefront images already clean | CDN, cacheable |
| `exports` | Excel exports | Signed URLs, 24 h lifecycle |

---

## 5. Redis caching and state design

Two Redis deployments:

| Instance | Policy | Used for |
|---|---|---|
| `redis-cache` | `maxmemory-policy allkeys-lru`, no persistence | HybridCache L2, output cache, query results |
| `redis-state` | `noeviction`, AOF on, replica + Sentinel | OTPs, rate-limit buckets, locks, idempotency keys, webhook dedupe, conversation hot state, SignalR backplane (separate DB/channel prefix) |

**Key convention:** `{env}:{schemaVersion}:{area}:{workspaceId?}:{rest}`. Workspace-scoped keys always include the workspace ID. All values serialized with `System.Text.Json` source-generated contexts.

| Data | Key (after prefix) | TTL | Invalidation |
|---|---|---|---|
| Workspace settings, plan entitlements | `ws:{ws}:settings` | 10 min | tag `ws:{ws}`; `WorkspaceSettingsChanged` event |
| Membership and permissions for a user in a workspace | `ws:{ws}:member:{user}` | 5 min | `MembershipChanged` event |
| Compiled automation rule set per channel account | `auto:{ws}:{channelAccount}:rules` | 30 min | `RulesChanged` event (version bump) |
| Agent profile and guardrails | `agent:{ws}:profile` | 10 min | `AgentProfileChanged` |
| Product by ID, product list for storefront | `cat:{ws}:p:{id}`, `cat:{ws}:storefront:{cardSetId}` | 15 min | tag `cat:{ws}`; `ProductChanged`, `PricesUpdated` |
| Embeddings of normalized text | `emb:{model}:{sha256}` | 30 days | none (content-addressed) |
| FAQ answer cache (single-turn, exact normalized match) | `ai:faq:{ws}:{sha256}` | 1 h | tag `kb:{ws}`; `KnowledgeUpdated` |
| Conversation hot state (window expiry, flow position, AI paused, last intents) | `conv:{ws}:{conversation}` (hash) | 8 days | write-through to PostgreSQL |
| Webhook dedupe | `wh:dedupe:{sha256(entry)}` | 48 h | SET NX |
| Comment reply dedupe (one private reply per comment) | `ig:pr:{commentId}` | 8 days | SET NX, backed by `automation.comment_events` |
| Instagram rate buckets | `rl:ig:{channelAccount}:{api}` | rolling | Lua token bucket |
| API rate limits | `rl:api:{scope}:{id}` | rolling | sliding window |
| OTP | `otp:{purpose}:{phoneBidx}` | 2 min | delete on success |
| Idempotency responses | `idem:{user}:{key}` | 24 h | none |
| Distributed locks | `lock:{resource}` | 30 s, renewed | release with token check (Lua) |
| Dashboard aggregates | `dash:{ws}:{range}` | 5 min | none |
| Public content and plans | output cache `oc:public:*` | 5 min | tag `content`, `plans`; on publish |
| Revoked sessions | `sess:revoked:{sid}` | until access token expiry | none |

Rules:
1. Redis is never the only copy of business data, except ephemeral data listed under `redis-state`.
2. Never cache decrypted secrets in Redis.
3. Invalidation is event-driven: modules publish `*Changed` integration events; a `CacheInvalidationConsumer` in every host calls `HybridCache.RemoveByTagAsync`. Also invalidate in-process immediately in the host that made the change.
4. Cache-aside via `HybridCache.GetOrCreateAsync` only; no manual get/set pairs.
5. Each cached read path has a metric (hit/miss) and a test that asserts invalidation happens on change.

---

## 6. Event-driven backbone: Kafka with MassTransit 8.5

### 6.1 Principles

- Kafka carries **integration events** between modules and work items between pipeline stages. In-module logic stays in-process.
- Every produced event goes through the **transactional outbox** (except Ingress, which has no database transaction and produces directly with an idempotent producer).
- Every consumer is **idempotent** via the inbox table.
- Ordering is guaranteed per partition key; choose the key deliberately (table below).
- Kafka has no delayed delivery; delays use the `platform.timers` table (section 7.2).

### 6.2 Topic catalog

Naming: `<module>.<event>.v<version>`. Envelope (Kafka headers + JSON body): `messageId` (UUID v7), `type`, `occurredAt`, `workspaceId`, `correlationId`, `causationId`, `traceparent`, `schemaVersion`, `payload`.

| Topic | Key | Producer | Main consumers (group) | Partitions (start) |
|---|---|---|---|---|
| `channels.instagram-webhook-received.v1` | IG account id | Ingress | ingest | 24 |
| `channels.inbound-message-received.v1` | conversation id | ingest | engine, analytics | 48 |
| `channels.inbound-comment-received.v1` | IG media id | ingest | engine, moderation (Phase 3) | 24 |
| `conversations.reply-requested.v1` | conversation id | engine | ai | 48 |
| `conversations.outbound-message-requested.v1` | conversation id | engine, ai, Api (human replies), automation timers | outbound | 48 |
| `conversations.outbound-message-completed.v1` | conversation id | outbound | engine, analytics, realtime notifier | 48 |
| `conversations.handoff-requested.v1` | conversation id | engine, ai | notifications, realtime | 12 |
| `files.scan-requested.v1` | file id | Files | media | 12 |
| `files.scan-completed.v1` | file id | media | owners of the file purpose (receipts, knowledge, catalog, conversations) | 12 |
| `knowledge.source-changed.v1` | workspace id | Knowledge | ai (re-index), cache invalidation | 12 |
| `catalog.product-changed.v1` | workspace id | Catalog | knowledge (catalog chunks), cache invalidation | 12 |
| `catalog.prices-updated.v1` | workspace id | Catalog | knowledge, automation (storefront cards), cache | 12 |
| `orders.order-created.v1` / `orders.order-status-changed.v1` | order id | Orders | payments, shipping, notifications, analytics | 12 |
| `payments.payment-confirmed.v1` | order id | Payments | orders, analytics, notifications | 12 |
| `payments.receipt-submitted.v1` / `payments.receipt-verified.v1` | order id or receipt id | Payments | ai (OCR), orders | 12 |
| `billing.usage-recorded.v1` | workspace id | engine, ai, outbound | billing (batched) | 12 |
| `tenancy.workspace-changed.v1` | workspace id | Tenancy | cache invalidation, billing | 6 |
| `notifications.notification-requested.v1` | user id | any | notifications | 12 |
| `audit.entry-recorded.v1` | workspace id | any | audit sink (batched) | 12 |
| `analytics.event-recorded.v1` | workspace id | any | analytics (batched) | 24 |
| `*.dlq` (per consumer group) | original key | retry policy | admin DLQ tools | 6 |

Topics are created by the Migrator host (or `CreateIfMissing` in dev only) with replication factor 3 in production, `min.insync.replicas=2`, retention 7 days (14 days for `channels.*`), and `cleanup.policy=delete`.

### 6.3 MassTransit 8.5 configuration (reference)

MassTransit's Kafka support is a **rider**: it needs a bus, so register the in-memory bus and attach the Kafka rider. Producers are `ITopicProducer<TKey, TValue>`; `IPublishEndpoint` does not publish to Kafka.

```csharp
services.AddMassTransit(x =>
{
    x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));

    x.AddRider(rider =>
    {
        // Producers (used by the outbox relay and the Ingress host only)
        rider.AddProducer<string, InboundMessageReceived>(Topics.InboundMessageReceived);

        // Consumers registered by the modules enabled in this host's worker groups
        rider.AddConsumer<InboundMessageConsumer>();

        rider.UsingKafka((ctx, k) =>
        {
            var opts = ctx.GetRequiredService<IOptions<KafkaOptions>>().Value;
            k.Host(opts.BootstrapServers, h =>
            {
                h.UseSasl(s => { s.Mechanism = SaslMechanism.ScramSha512; s.Username = opts.User; s.Password = opts.Password; s.SecurityProtocol = SecurityProtocol.SaslSsl; });
            });
            k.ClientId = $"pishkhan-{opts.HostName}";

            k.TopicEndpoint<string, InboundMessageReceived>(Topics.InboundMessageReceived, "engine", e =>
            {
                e.AutoOffsetReset = AutoOffsetReset.Earliest;
                e.ConcurrentMessageLimit = 64;     // parallelism across keys
                e.CheckpointInterval = TimeSpan.FromSeconds(5);
                e.CheckpointMessageCount = 500;
                e.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200)));
                e.UseFilter(new DeadLetterToTopicFilter<InboundMessageReceived>(Topics.Dlq("engine")));
                e.ConfigureConsumer<InboundMessageConsumer>(ctx);
            });
        });
    });
});
```

Requirements for the agent:
- Wrap the above in `AddPishkhanMessaging(configure)` in `Pishkhan.Infrastructure` so modules only declare `(topic, consumer, group, options)`.
- **Verify with an integration test** (Testcontainers Kafka) that messages with the same key are processed sequentially and different keys concurrently under the chosen settings; if the default behavior in 8.5 differs, set the key-ordering options explicitly and document them in an ADR.
- Serializer: System.Text.Json with the envelope; unknown fields ignored; `schemaVersion` checked, older versions upcast in the consumer.
- Retry: in-memory exponential retry for transient errors; non-transient errors (`ValidationException`, `DomainException`) skip retry and go to DLQ immediately.
- DLQ: a small filter produces the failed message plus exception metadata to `<group>.dlq`; AdminApi offers list, inspect and replay (re-produce to the original topic).

### 6.4 Transactional outbox and inbox

**Outbox (custom, explicit, works with the Kafka rider)**

- `platform.outbox_messages(id uuid pk, topic text, key text, type text, payload jsonb, headers jsonb, occurred_at timestamptz, shard smallint, published_at timestamptz null, attempts int, last_error text)`; index on `(shard, id) where published_at is null`.
- Modules add outbox rows through `IIntegrationEventWriter` inside the same `DbContext` transaction (each module's DbContext maps the shared `platform.outbox_messages` table via a shared configuration, or writes through a `DbConnection`-level helper in the same transaction).
- `shard = hash(key) % 16`. The `OutboxRelayWorker` (platform group) holds a PostgreSQL advisory lock per shard, so each shard has one active relay; it reads up to 500 rows ordered by `id`, produces them with the idempotent producer, marks `published_at`. This preserves per-key order.
- Cleanup job deletes published rows older than 3 days.
- Metric: unpublished count and age per shard.

**Inbox (idempotent consumers)**

- `platform.processed_messages(consumer text, message_id uuid, processed_at, primary key(consumer, message_id))`.
- Base class `IdempotentConsumer<T>` opens the module's DbContext transaction, inserts the inbox row (conflict → skip as duplicate), runs the handler, writes any outbox rows, commits. Effects outside the database (calling Meta) use their own idempotency (outbound uses `messages.id` as the idempotency reference and checks status before sending).

---

## 7. Background workers and jobs

### 7.1 Consumers (by group) — see 2.1

### 7.2 Timers (per-entity delays, high volume)

Used for: follow-up reminders ("send a discount one day after the product link"), flow `delay` nodes, 24-hour window warnings, payment link expiry, receipt re-check, follow-gate re-check, trial ending notices.

- `platform.timers(id, workspace_id, due_at, kind, payload jsonb, status [pending, fired, cancelled], dedupe_key unique null, attempts)`; index on `(due_at) where status='pending'`.
- `TimerDispatcher` (platform group) loops: `SELECT ... WHERE status='pending' AND due_at <= now() ORDER BY due_at LIMIT 500 FOR UPDATE SKIP LOCKED`, produces the timer's event to Kafka through the outbox in the same transaction, marks fired. Multiple replicas are safe.
- Cancel by `dedupe_key` (for example `followup:{conversationId}:{ruleId}`) when the customer replies or buys.
- Handlers re-check conditions at fire time (for example still inside the 24-hour window).

### 7.3 Recurring jobs (Quartz.NET, clustered on PostgreSQL)

| Job | Schedule | Module |
|---|---|---|
| Instagram long-lived token refresh (before expiry) | hourly | Channels |
| Channel health check (token valid, webhook subscribed) | every 15 min | Channels |
| Create future monthly partitions; detach expired | daily | Platform |
| Daily metrics rollup per workspace | every 10 min (incremental) and nightly (full) | Analytics |
| Usage aggregation and overage invoicing | hourly / monthly | Billing |
| Subscription renewals, trial expiry, dunning | daily | Billing |
| Outbox and inbox cleanup | hourly | Platform |
| Stale file cleanup (quarantine retention 30 days) | daily | Files |
| ClamAV signature freshness check (alert if older than 24 h) | hourly | Files |
| Knowledge re-embed on model change | on demand | Knowledge |
| Evaluation run (nightly regression on the Persian eval set) | nightly | Agent |
| Weekly Persian insights summary (Phase 3) | weekly | Analytics |
| Gold/currency rate fetch (Phase 3) | every 15 min | Catalog |
| Outage detector (Instagram reachability) (Phase 2) | every minute | Channels |

---

## 8. File uploads and antivirus pipeline

Applies to every file entering the system: panel uploads (product images, knowledge documents, Excel imports, avatars), CMS uploads in the admin panel, and **media downloaded from Instagram** (customer images, receipt screenshots, voice notes, videos).

### 8.1 Flow

1. **Receive** (`POST /api/v1/workspaces/{ws}/files`, multipart, streaming, no full buffering):
   - Authorize, rate limit per workspace.
   - `purpose` is required (`product_image`, `knowledge_document`, `import_sheet`, `avatar`, `receipt`, `message_attachment`, `cms_media`); each purpose has an allowlist of types and a max size (for example images 10 MB, documents 20 MB, sheets 5 MB, videos 100 MB in Phase 2 via presigned POST).
   - Per-endpoint `MaxRequestBodySize`; reject early on `Content-Length`.
   - Detect the real type from magic bytes (own signature table for the allowed types); declared type and extension must agree with detected type, otherwise `415`.
   - The original filename is stored only as metadata (sanitized, max 200 chars); storage key is `{workspaceId}/{fileId}`.
   - Stream to the `quarantine` bucket while computing SHA-256.
   - Insert `files` row with `status = pending_scan`, write `files.scan-requested.v1` to the outbox, return `202` with the file ID and status.
2. **Scan** (`media` group, `FileScanConsumer`):
   - If a file with the same SHA-256 was scanned **clean** with the current signature version, reuse the result; if it was infected, mark infected immediately.
   - Stream from storage to `clamd` with `INSTREAM` (nClam), timeout 60 s. ClamAV limits configured: `MaxFileSize`, `MaxScanSize`, `MaxRecursion`, `MaxFiles`, `AlertExceedsMax yes` (exceeding limits counts as a failure, not as clean).
   - **Clean** → for images: decode and re-encode with SkiaSharp (strips EXIF/GPS and any embedded payload), cap dimensions, generate thumbnails (WebP); for PDFs and Office files: parse only in the worker with size and decompression-ratio limits (zip-bomb protection) and reject macros (`.docm`, `.xlsm` not allowed); copy to `files` bucket; status `clean`.
   - **Infected** → status `infected`, keep in quarantine (30-day retention), audit entry, notify the uploader ("file rejected for security reasons") and staff (AdminApi quarantine view).
   - **Error / timeout** → retry with backoff up to 5 times, then `scan_failed` (visible in AdminApi with re-scan action). **Fail closed:** a file is never usable unless `clean`.
   - Produce `files.scan-completed.v1`.
3. **Use:** modules that requested the file react to `scan-completed` (attach image to product, start OCR on receipt, start ASR on voice note, index document). API returns file URLs only for `clean` files: 5-minute signed URLs on the `usercontent` domain, with `Content-Disposition: attachment` for non-images and `X-Content-Type-Options: nosniff`.
4. **Instagram media:** ingest stores the media URL; the `media` group downloads it through `SafeHttpClient` (Meta CDN host allowlist, size limit), stores it in quarantine and follows the same scan path before any AI processing.

### 8.2 ClamAV operations

- `clamd` runs as a separate deployment (2+ replicas behind a TCP service), with `freshclam` updates.
- **ClamAV's official signature CDN refuses downloads from embargoed countries.** Run a private mirror (`cvdupdate`) in the international edge zone and point `freshclam` at it. Alert if signatures are older than 24 hours.
- Health check includes `PING` and signature version; uploads keep working while ClamAV is down (files stay `pending_scan`) and an alert fires.
- Integration tests use the EICAR test string to assert the infected path, and a Testcontainers ClamAV image.

---
## 9. Module specifications

Each module lists its responsibilities, main endpoints (panel `Api` unless marked Admin or Public) and the events it produces. Endpoint lists are the minimum; the agent adds read endpoints the frontends need, following the conventions.

### 9.1 Identity
- Phone OTP login and sign-up, optional password, TOTP setup/verify, sessions list and revoke, profile.
- Endpoints: `POST /auth/otp/request`, `POST /auth/otp/verify`, `POST /auth/token/refresh`, `POST /auth/logout`, `POST /auth/password/login`, `POST /auth/totp/setup|enable|disable`, `GET /me`, `PATCH /me`, `GET /me/sessions`, `DELETE /me/sessions/{id}`, `POST /auth/realtime-token`.
- Events: `identity.user-registered.v1`.

### 9.2 Tenancy
- Workspaces, organizations (agency, Phase 3), memberships, roles, invitations (by phone, SMS link), workspace settings (timezone, business hours, holidays calendar, language), workspace deletion with grace period.
- Endpoints: `POST /workspaces`, `GET /workspaces`, `GET|PATCH /workspaces/{ws}`, `GET|POST /workspaces/{ws}/members`, `PATCH|DELETE /workspaces/{ws}/members/{id}`, `POST /workspaces/{ws}/invitations`, `POST /invitations/{token}/accept`, `GET|PUT /workspaces/{ws}/business-hours`.
- Events: `tenancy.workspace-changed.v1`, `tenancy.membership-changed.v1`.

### 9.3 Channels
- Channel abstraction: `IChannelAdapter { Kind; SendAsync(OutboundMessage); ParseWebhook(...); GetProfile(...); }`, with adapters **Instagram**, **Sandbox** (test mode, Phase 1), **Sms** and **Bale** (Phase 2), **WebWidget**, **Rubika**, **Eitaa**, **Telegram** (Phase 3).
- Instagram: OAuth with Instagram Login (business login), exchange for long-lived token, store encrypted, subscribe the account to webhook fields (`messages`, `messaging_postbacks`, `messaging_seen`, `comments`, `live_comments`, `mentions`, `story_insights` as available), list media (for post-specific rules and feed import), send API (text, image, audio, video, generic template/carousel, quick replies, buttons), private replies to comments, public comment replies, hide comment (moderation, Phase 3), user profile fetch (name, username, follower status where the API exposes it for follow-gate).
- Webhook handling lives in the `Ingress` host: `GET /webhooks/instagram` (verify token challenge) and `POST /webhooks/instagram` (signature, dedupe, produce).
- Rate profile per account (configurable in admin because Meta limits change): token buckets per API family; "viral mode" pacing automatically enabled when the comment rate on a media exceeds a threshold.
- Connection status machine: `connecting → active → token_expiring → reauth_required | disconnected`; produces `channels.channel-status-changed.v1` (panel banner).
- Endpoints: `GET /workspaces/{ws}/channels`, `POST /workspaces/{ws}/channels/instagram/connect` (returns OAuth URL with signed `state`), `GET /channels/instagram/callback`, `DELETE /workspaces/{ws}/channels/{id}`, `GET /workspaces/{ws}/channels/{id}/media`.
- Meta-required endpoints: deauthorize callback and **data deletion callback** (`POST /webhooks/instagram/data-deletion`, returns a status URL and confirmation code).

### 9.4 Conversations
- Inbound normalization (from raw webhook to `InboundMessageReceived`): resolve or create contact and channel identity, find or open conversation, store message (with `normalized_text`), update window (`last_inbound_at`, `window_expires_at = +24h`), enqueue media download for attachments.
- Outbound pipeline: `OutboundMessageRequested` → policy checks (window open, or human-agent tag allowed for a human reply within 7 days; one private reply per comment; channel active; workspace not suspended; plan quota soft-check) → rate limiter → adapter send → `OutboundMessageCompleted` with status/error mapping.
- Inbox: list/filter conversations (status, assignee, channel, tag, unread, handed-off), conversation detail with messages (keyset pagination by `created_at,id`), send human reply, assign, change status, notes, pause/resume AI per conversation, quick replies CRUD, mark read.
- Realtime notifier: consumers push SignalR events (overview section 6) to group `ws:{ws}` and `conv:{id}`.
- Endpoints: `GET /workspaces/{ws}/conversations`, `GET /workspaces/{ws}/conversations/{id}`, `GET /workspaces/{ws}/conversations/{id}/messages`, `POST /workspaces/{ws}/conversations/{id}/messages`, `POST .../assign`, `POST .../status`, `POST .../ai-pause`, `POST .../notes`, `GET|POST|PATCH|DELETE /workspaces/{ws}/quick-replies`.

### 9.5 Automation (rules first)
- **Triggers:** DM keyword (exact, contains, any-of, regex-lite; on normalized text, Persian/ASCII digit-insensitive), comment keyword (all posts / specific post / "next post"), reply to all comments on a post, story reply, story mention, live comment, post share (customer shares your post), new conversation (welcome), postback/quick-reply payload, outside business hours, form completed, order status changed (Phase 2).
- **Actions:** send message (text, media, voice, card/carousel, buttons, quick replies, with variables like `{{contact.first_name}}`), private reply to comment, public reply to comment (randomized from a list), start flow, add/remove tag, set custom field, capture phone (Phase 2 consent flow), hand off, pause AI, schedule follow-up (timer), notify team.
- **Flows:** JSON graph (`nodes`, `edges`) versioned and validated server-side. Node types: `message`, `question` (text, number, phone, choice; with validation and retry text), `condition` (field, tag, follow status, time, answer), `delay` (timer), `follow_gate` (check follow status, send ask message, re-check on "I followed" postback), `storefront` (card set), `action` (tag, field, handoff, webhook in Phase 3), `ai` (hand to AI agent with a goal), `end`. Runtime is a state machine stored in `flow_runs`; all waits are timers or inbound events.
- **Evaluation order** per inbound event: active flow run waiting for input → exact keyword/postback rules (by priority) → comment rules → business-hours rule → AI agent (if enabled and not paused) → default fallback message. The result is recorded on the message (`handled_by`, `automation_rule_id`).
- **Backlog processing:** "reply to existing comments" job for a post (pages through comments via API, respects rate limits and Meta's private-reply age limit).
- Endpoints: CRUD `rules`, CRUD `flows` (+ `POST /flows/{id}/publish`, `POST /flows/{id}/validate`), `POST /rules/{id}/backlog-run`, `GET /rules/{id}/stats`.
- Events: `automation.rules-changed.v1`, `automation.flow-published.v1`, `conversations.outbound-message-requested.v1`, `conversations.reply-requested.v1`.
- Domain tests: every trigger kind, priority conflicts, Persian keyword matching with Arabic letters, half-spaces and Persian digits, one-private-reply rule, window expiry during a delay.

### 9.6 Agent (AI) — see section 10

### 9.7 Knowledge
- Sources: FAQ entries, rich-text documents, uploaded files (PDF, DOCX, TXT after AV), URLs (fetched via `SafeHttpClient`), catalog (auto-generated chunks per product/variant), onboarding chat transcript, Instagram feed (Phase 2).
- Chunking for Persian (sentence boundaries on `.`, `؟`, `!`, newline; 300–500 tokens; overlap 50), embeddings via the AI gateway, `tsvector` with the `simple` configuration on normalized text, `pg_trgm` index for fuzzy matching.
- Hybrid retrieval: vector (HNSW, cosine) top-k + full-text top-k, merged with Reciprocal Rank Fusion, filtered by workspace and source status; returns chunk IDs for traceability.
- **Knowledge score** (0–100): coverage of required facts per industry template (prices, shipping, return policy, payment methods, hours, contact), number of products with prices and stock, unanswered-question rate in the last 7 days.
- Unanswered questions: created when the agent hands off for missing knowledge; owner answers once → becomes a FAQ ("add to knowledge with one tap").
- Endpoints: CRUD `knowledge/sources`, `POST knowledge/sources/{id}/reindex`, `GET knowledge/score`, `GET|POST knowledge/unanswered`, `POST knowledge/unanswered/{id}/answer`, `POST knowledge/search` (debug, owner only).

### 9.8 Catalog
- Products, variants (size, color...), categories, tags, stock, images (clean files only), pricing mode `fixed` or `formula` (Phase 3: gold/currency rate formula), storefront card sets for DM carousels.
- Bulk price update (Phase 2, gap G8): by percentage or fixed amount, by category/tag/selection, with rounding rule (to nearest 1,000 Toman etc.), preview, apply as one `price_change_batch`, undo within 24 h; produces `catalog.prices-updated.v1` so knowledge, cards and cache refresh at once.
- Import/export via Excel (template with Persian headers).
- Endpoints: CRUD products/variants/categories, `POST /catalog/import`, `GET /catalog/export`, CRUD `storefront-cards`, `POST /catalog/price-changes/preview`, `POST /catalog/price-changes`, `POST /catalog/price-changes/{id}/undo`.

### 9.9 Orders (Phase 2)
- Order created by the AI agent tool, by a flow, or manually in the panel; statuses `draft → awaiting_payment → paid → preparing → shipped → delivered → completed`, plus `cancelled`, `refunded`, `returned`.
- Order number human-friendly per workspace (sequence), invoice view (Jalali dates).
- Address capture with parsing into province/city/postal code (validate 10-digit postal code; province/city reference table).
- Endpoints: CRUD orders, `POST /orders/{id}/status`, `GET /orders/{id}/timeline`.

### 9.10 Payments (Phase 2)
- `IPaymentGateway` with `ZarinpalGateway` first: request payment → authority → redirect URL → callback → verify → `payment-confirmed`.
- Seller connects their own merchant ID (stored encrypted); payment link `https://pay.example.ir/{token}` shows order summary (server-rendered by `Api`, minimal HTML, Persian) and redirects to the PSP; callback `GET /api/v1/pay/callback/{provider}`.
- Card-to-card receipts (section 10.5): seller bank accounts (card, Sheba) registered and validated; receipt pipeline; review queue; decisions audited.
- Endpoints: `GET|PUT merchant-accounts`, CRUD `bank-accounts`, `POST /orders/{id}/payment-links`, `GET receipts?status=`, `POST receipts/{id}/approve|reject`.

### 9.11 Shipping (Phase 3)
- Carriers (Iran Post, Tipax, Chapar, Alopeyk, Snapp Box) behind `ICarrierAdapter`; start with manual tracking-code entry that triggers an automatic customer message; then rates by city and status polling where APIs exist.

### 9.12 CRM
- Contacts (merged identities across channels), tags, custom fields, segments (rule JSON evaluated to SQL), forms (DM forms definitions are flow `question` nodes; submissions stored here), consent records (phone, SMS, Bale), Excel export (async job → `exports` bucket → notification).
- Endpoints: `GET /contacts` (filters, search by name/username/phone via blind index), `GET|PATCH /contacts/{id}`, CRUD tags/segments/forms, `GET forms/{id}/submissions`, `POST /exports`.

### 9.13 Billing
- Plans and entitlements (meters: `ai_conversations`, `ai_messages`, `channel_accounts`, `seats`, `receipt_verifications`, `sms`), subscription lifecycle, free tier, usage records from `billing.usage-recorded.v1` (batched), **soft overage** (Phase 2, gap G31): the bot never stops; overage accrues to the wallet/invoice up to a workspace-set cap, with notifications at 80%, 100% and at the cap.
- Payments for our own billing via Zarinpal; wallet top-up; invoices with configurable VAT; annual plans with installments (Phase 2); coupons; affiliate attribution (Phase 2).
- Endpoints: `GET /public/plans` (Public), `GET billing/subscription`, `POST billing/checkout`, `GET billing/usage`, `GET billing/invoices`, `GET billing/invoices/{id}/pdf`, `PUT billing/overage-cap`, `POST billing/wallet/top-up`.

### 9.14 Files — section 8.

### 9.15 Notifications
- Channels: in-app (stored + SignalR), web push (VAPID; unreliable in Iran, so never the only channel), SMS (critical only: handoff waiting > N minutes, payment received, channel disconnected), email optional.
- User notification preferences per event type.

### 9.16 Content (CMS for the website)
- Pages (block-based JSON content), blog posts and categories, FAQs, help articles (with search), case studies, redirects, media; draft → published with scheduled publishing; preview tokens; on publish, call the website's on-demand revalidation webhook (`POST https://example.ir/api/revalidate` with HMAC) and invalidate output cache.
- Public endpoints (Public, cached): `GET /public/pages/{slug}`, `GET /public/posts?cursor=`, `GET /public/posts/{slug}`, `GET /public/help/search?q=`, `GET /public/case-studies`, `POST /public/leads` (contact/waitlist with ALTCHA), `POST /public/demo-chat` (rate-limited demo against a fixed demo workspace).

### 9.17 Analytics
- Events from all modules (message received/answered by rule/AI/human, handoff, order created/paid, payment, follow-gate conversion, flow completion).
- Rollups `daily_workspace_metrics`; dashboard endpoint returns precomputed numbers plus "overnight summary" ("47 DMs answered, 9 purchases").
- Phase 3: attribution paths (post/Reel/story/live → conversation → order → payment) and conversation intelligence.

### 9.18 Audit
- Consumes `audit.entry-recorded.v1` in batches; query endpoints for workspace owners (their scope) and staff.

### 9.19 Backoffice (AdminApi only)
- Workspaces and users search/view/suspend, impersonation, plans and pricing, coupons, feature flags (per workspace / percentage), announcements, AI model routes and provider credentials, prompt templates (versioned, with eval gate), eval sets and runs, industry templates, bank receipt templates, file quarantine, DLQ browser/replay, Kafka lag and worker status, Meta app/rate profile settings, support tickets (Phase 2), CMS (proxies Content module), audit search, outage mode.
- Endpoints mirror the admin panel plan (`02-admin-panel.md`); all under `/admin/v1`, all audited.

---

## 10. AI agent design

### 10.1 Model gateway (`Pishkhan.AI`)

- `IModelRouter.GetChatClient(task, workspace)` returns an `IChatClient` for a **task**: `classify`, `generate_reply`, `extract_receipt`, `transcribe`, `embed`, `summarize`, `content`. Routes (provider, model, parameters, fallback chain, timeout) are configured in Backoffice (`ai_model_routes`) and cached.
- Providers configured as OpenAI-compatible endpoints (base URL + key), allowing permitted hosted providers or self-hosted open-weight models (assumption A3). ASR (`ISpeechToText`) and OCR may be separate self-hosted services (for example faster-whisper and a vision model) behind interfaces.
- Middleware pipeline on every call: OpenTelemetry, timeout, circuit breaker per provider, fallback to next route, token and cost metering (`billing.usage-recorded`, `agent.ai_traces`), PII redaction for logs.
- Prompt registry: prompts are versioned templates in `backoffice.prompt_templates`; code refers to them by key; a new version can be activated only after an eval run passes thresholds (admin panel shows the gate).

### 10.2 Reply pipeline (consumer of `conversations.reply-requested.v1`)

1. **Load context:** agent profile, last N messages (token budget), contact profile, open order, flow state; from Redis hot state where possible.
2. **Pre-process:** normalized text; voice note → transcript (Phase 2); image → classify (`receipt`, `product_screenshot`, `other`) and branch (receipt → payments pipeline; screenshot → visual search in Phase 3).
3. **Classify** (small model, JSON output): intent (`price_question`, `availability`, `order`, `shipping`, `complaint`, `discount_request`, `human_request`, `greeting`, `other`), language (`fa`, `finglish`, `en`, mixed), sentiment, entities (product mentions, sizes, colors).
4. **Hard handoff rules (code, not LLM):** explicit human request, complaint with negative sentiment, discount request when negotiation is off, configured keywords, repeated failure (2 low-confidence turns), VIP tag.
5. **Retrieve:** hybrid search on knowledge + catalog search tool.
6. **Generate** with tools (function calling), system prompt built from the agent profile (name, tone, brand voice, "answer only from provided facts", "never say" list), and retrieved facts labeled as data.
   - Tools: `search_products(query, filters)`, `get_product(id)`, `check_stock(variantId)`, `get_order_status(orderNumber)`, `create_order(items, address?)` (Phase 2), `create_payment_link(orderId)` (Phase 2), `request_phone_consent()` (Phase 2), `request_handoff(reason)`, `add_unanswered_question(text)`.
   - Tool execution validates workspace, entitlements and business rules; tool results are the only source of prices and stock.
7. **Guard output:** language and script check (Persian unless the customer writes otherwise), never-say list, **price guard** (every currency-looking number must match a tool result or a knowledge fact; otherwise regenerate once, then hand off), length limit, link allowlist (only workspace's own links and payment links), no promises beyond policy.
8. **Confidence:** combine classifier confidence, retrieval score and a self-assessment field; below the workspace threshold → hand off with a suggested reply for the human.
9. **Send:** `OutboundMessageRequested` (text, optional card set), record `ai_traces`, usage, analytics.

### 10.3 Test mode (Phase 1)
The `Sandbox` channel adapter lets the owner chat with their bot from the panel (via SignalR) through the exact same pipeline, with traces visible (retrieved facts, tools called, confidence), and without sending anything to Instagram or counting toward quotas (separately rate-limited).

### 10.4 Persian language quality
- Phase 0: normalizer; Phase 1: keyword matching and retrieval on normalized text; eval set v1 (≥ 1,000 anonymized, consented DMs across the five launch verticals with expected intents, facts and handoff decisions).
- Phase 2 (G14): Finglish detection (character-class ratio + language-ID model) and transliteration to Persian script (rule-based mapping plus an LLM fallback, cached per token sequence), applied before classification and retrieval; eval subset for Finglish and colloquial text.
- Phase 2 (G15): ASR for voice notes; transcript stored on the message and shown in the inbox; long voice notes chunked.
- `Pishkhan.EvalRunner` computes intent accuracy, groundedness (answer facts ⊆ retrieved facts, judged by a model plus spot checks), price correctness, handoff precision/recall, tone; results stored in `eval_runs`; CI blocks prompt/model route activation that regresses beyond thresholds.

### 10.5 Receipt verification (Phase 2, gap G1)
1. Receipt image arrives (DM attachment classified as receipt, or uploaded by the seller) → AV scan → `payments.receipt-submitted.v1`.
2. Extraction: vision model with a strict JSON schema (amount, date/time, reference/tracking number, source card masked, destination card or Sheba, bank name) plus **bank receipt templates** (layout hints and regexes per bank app, managed in Backoffice) to validate fields; Persian digits normalized.
3. Checks: amount equals the pending order total (or within a configured tolerance), destination card/Sheba matches one of the seller's registered accounts, time within the order window, **duplicate detection** (same reference number, or perceptual hash of the image near a previous receipt, across the workspace), basic tamper heuristics (inconsistent fonts/positions vs template, EXIF anomalies before stripping).
4. Decision: `auto_approved` (only if the seller enabled auto-approve and confidence ≥ threshold and all checks pass), otherwise `needs_review` in the panel queue; never auto-reject without seller review except exact duplicates.
5. On approval → order `paid`, customer confirmation message, analytics.

---

## 11. Instagram platform compliance (design constraints)

- Only official Instagram APIs; no passwords; no growth-bot actions.
- Standard messages only within 24 hours of the customer's last message; the `HUMAN_AGENT` tag only for genuine human replies (enforced: only from the panel by a user, within 7 days).
- One private reply per comment, within Meta's allowed age; tracked in `automation.comment_events`.
- Pacing and queuing for viral posts; per-account rate profiles editable in admin.
- Follow-gate only with what the API exposes; if follow status is unavailable, fall back to a self-declared "I followed" button.
- Meta app review assets (screencasts, privacy policy, data deletion URL) are a Phase 0 deliverable in coordination with the website plan.
- Monitor Meta API changelog; adapter isolates version (`graph.instagram.com/vXX.0` from config).

---

## 12. Testing strategy

| Level | Scope | Tools |
|---|---|---|
| Unit | Domain logic (rules engine, flow runtime, pricing, billing proration, normalizer, validators, guards) | xUnit v3, Shouldly, NSubstitute, Bogus |
| Architecture | Module boundaries, tenant filters, no forbidden packages, naming | NetArchTest |
| Integration | Endpoints with real PostgreSQL, Redis, Kafka, MinIO, ClamAV in containers; outbox → Kafka → consumer → DB | Testcontainers, `WebApplicationFactory` |
| Contract | OpenAPI snapshot test; Meta webhook payload fixtures; Zarinpal and SMS provider stubs | Verify (snapshot), WireMock.Net |
| Security | Cross-tenant suite, auth flows (refresh reuse), rate limits, upload abuse (EICAR, wrong magic bytes, oversize, zip bomb), SSRF attempts | Integration tests |
| AI quality | Eval set regression | `Pishkhan.EvalRunner` |
| Load | Ingress burst, inbox reads, outbound pacing | k6 |

Coverage target: ≥ 80% lines on Domain and Features folders; every bug fix adds a test.

---

## 13. DevOps

- Local: `infra/docker-compose.yml` with PostgreSQL 18 (pgvector image), `redis-cache`, `redis-state`, Kafka 4 (KRaft, single broker), Kafka UI, ClamAV (with local signatures volume), MinIO, OpenTelemetry Collector, Grafana + Prometheus + Tempo/Loki or Seq, Mailpit. `dotnet run` for hosts; `.NET Aspire` AppHost is optional for local orchestration.
- Containers: one Dockerfile per host, `mcr.microsoft.com/dotnet/aspnet:10.0` chiseled/non-root images, read-only root filesystem.
- CI (GitHub Actions or self-hosted GitLab if GitHub access is restricted): restore → build → unit → architecture → integration (Testcontainers) → OpenAPI export diff → vulnerability scan → image build → push to a domestic registry (Harbor).
- Deploy: Docker Compose for beta, Kubernetes + Helm from Phase 2; Migrator as a pre-deploy job; blue/green for Api; workers rolled per group.
- Backups: PostgreSQL PITR (pgBackRest) with daily restore test; MinIO bucket replication; Kafka topics are reproducible from outbox for 3 days.
- Secrets: Vault (or Kubernetes secrets sealed) — never in images or repo.

---

## 14. Phased work packages

Each work package (WP) lists scope and acceptance criteria. Implement in order within a phase unless marked parallel (∥).

### Phase 0 — Foundations (weeks 1–6)

**BE-0.1 Solution skeleton and CI**
- Create the layout in section 2, `global.json`, central package management, analyzers (`Microsoft.CodeAnalysis.NetAnalyzers`, `Meziantou.Analyzer` or similar), `.editorconfig`, CI pipeline.
- ✅ `dotnet build` with warnings as errors passes; CI runs unit + architecture tests; architecture test project enforces section 1.3.

**BE-0.2 Local infrastructure** ∥
- docker-compose per section 13; `make up`/`just up`; seeded dev Vault or env keys; Kafka topics bootstrap script.
- ✅ All services healthy; README explains ports and credentials (dev only).

**BE-0.3 Shared kernel and Persian library** ∥
- `Result<T>`, `Error`, strongly typed IDs, `Money`, `PhoneNumber`, `IClock`; `Pishkhan.Persian` normalizer and validators (section 3.2).
- ✅ Unit tests with ≥ 200 Persian text cases (Arabic letters, digits, ZWNJ, elongation), validator tests for phone, national ID, card, Sheba, postal code.

**BE-0.4 Persistence base**
- Module DbContext base (snake_case, timestamps, xmin concurrency, tenant filter, soft delete), Migrator host, `platform` schema (outbox, inbox, timers), partition helper.
- ✅ Migrator creates all schemas from scratch; architecture test fails for a tenant entity without filter; integration test proves cross-tenant query returns nothing.

**BE-0.5 Messaging base**
- `AddPishkhanMessaging`, topic registry, envelope, outbox writer + relay (sharded, advisory locks), idempotent consumer base, retry + DLQ filter, OpenTelemetry propagation.
- ✅ Integration test: command writes entity + outbox row → relay → Kafka → consumer → effect; duplicate delivery processed once; per-key ordering test passes; poison message lands in DLQ.

**BE-0.6 Caching, locks and rate limiting** ∥
- HybridCache with Redis L2 and tag invalidation consumer, `IDistributedLock` (Redis, token-checked release), Lua token bucket, ASP.NET Core rate limiter policies backed by Redis, idempotency-key filter.
- ✅ Tests for invalidation on event, lock exclusivity, bucket refill, idempotent POST replay.

**BE-0.7 Identity and tenancy**
- Section 9.1 and 9.2 core: OTP (SMS sender stub in dev that logs to console and to Mailpit-like viewer), JWT + refresh rotation with reuse detection, workspaces, memberships, roles/permissions, invitations; staff identity in Backoffice with TOTP.
- ✅ Auth integration tests (OTP limits, refresh reuse revokes family, staff token rejected by Api and vice versa); OpenAPI exported.

**BE-0.8 Files and antivirus**
- Section 8 end to end with ClamAV.
- ✅ EICAR upload ends `infected` and is never downloadable; PNG with JPEG extension rejected; oversize rejected before full read; clean image is re-encoded without EXIF; hash dedupe skips rescans; ClamAV down → `pending_scan` and health check degraded.

**BE-0.9 Observability and health** ∥
- Serilog + OTel, PII redaction, health endpoints, base Grafana dashboards.
- ✅ A trace spans HTTP → Kafka → consumer; phone numbers in logs appear masked.

**BE-0.10 API conventions and client generation** ∥
- Problem Details, versioned groups, three OpenAPI documents exported in build, examples with Persian data.
- ✅ `backend/openapi/*.json` regenerate deterministically; CI fails on drift.

**BE-0.11 AI gateway and eval harness**
- Model router, OpenAI-compatible provider config, metering, prompt registry, `EvalRunner` skeleton and eval set schema; import format for anonymized conversations.
- ✅ Route switch via config without code change; a dummy eval run stores results.

**BE-0.12 Instagram spike (milestone M0)**
- Meta app in development mode; OAuth with Instagram Login; Ingress webhook verify + signature; receive DM → store → echo reply via outbound worker.
- ✅ Real test account DM receives an automated reply; webhook signature failure returns 401 without processing.

### Phase 1 — MVP, P0 features (months 2–5)

**BE-1.1 Channels: Instagram production-grade** — section 9.3 full (token refresh job, status machine, media listing, rate profiles, deauthorize and data-deletion callbacks).
✅ Token near expiry refreshes automatically; revoked token moves account to `reauth_required` and emits status event.

**BE-1.2 Conversations and outbound pipeline** — section 9.4.
✅ Window enforcement tests; human-agent tag only from panel within 7 days; delivery statuses update; inbox list p95 < 200 ms with 1 M messages seeded.

**BE-1.3 Automation engine** — section 9.5 (all triggers, actions, flows, backlog).
✅ Domain tests listed in 9.5; viral-post load test (2,000 comments in 5 minutes) completes with pacing and zero duplicate private replies.

**BE-1.4 Knowledge and AI agent v1** — sections 9.7 and 10.1–10.3 (without Phase 2 pre-processing).
✅ Eval set v1 baseline recorded; price guard blocks a fabricated price in tests; low confidence hands off with a suggested reply; traces visible.

**BE-1.5 Inbox, handoff and realtime** — assignment, notes, quick replies, AI pause, SignalR hub with Redis backplane, notification on handoff.
✅ Two Api replicas deliver realtime events to clients connected to either.

**BE-1.6 Catalog and storefront cards** — section 9.8 without bulk price and formula pricing.
✅ Carousel card renders on Instagram test account; product change invalidates cache and re-indexes knowledge.

**BE-1.7 CRM and forms** — section 9.12 (tags, custom fields, segments, form submissions from flows, Excel export job).
✅ Export of 100k contacts completes asynchronously and is downloadable once.

**BE-1.8 Follow-gate and follow-ups** — follow-gate node, follow-up timers with cancellation on reply/purchase, window-aware.
✅ Timer fired after window close does not send; cancellation by dedupe key works.

**BE-1.9 Onboarding assistant and knowledge score** — conversational onboarding (AI interviews the owner and writes FAQ/knowledge entries after confirmation), knowledge score endpoint, sandbox test mode.
✅ Fresh workspace reaches a knowledge score > 60 through onboarding chat alone in an end-to-end test with a scripted owner.

**BE-1.10 Dashboard and analytics v1** — events, rollups, dashboard endpoint, overnight summary.

**BE-1.11 Plans, quotas and entitlements v1** — plans, free tier, entitlement checks (`402` responses), usage metering; payment for plans can be manual/admin-granted during closed beta.

**BE-1.12 Backoffice APIs v1** — section 9.19 items needed by `02-admin-panel.md` Phase 1.

**BE-1.13 Content and public APIs** — section 9.16 (CMS, public endpoints, leads, demo chat, revalidation webhook).

**BE-1.14 Notifications v1** — in-app, web push, SMS for critical events, preferences.

**BE-1.15 Beta hardening** — cross-tenant suite complete, rate limits tuned, backups and restore test, runbooks for on-call (Meta outage, Kafka lag, ClamAV down).
✅ Milestone M1 checklist in the overview.

### Phase 2 — Differentiated launch, P1 features (months 5–8)

| WP | Scope | Key acceptance criteria |
|---|---|---|
| **BE-2.1** Orders | Section 9.9 | Order lifecycle tests; AI `create_order` tool creates draft with catalog prices only |
| **BE-2.2** PSP payment links (G2) | Section 9.10 Zarinpal | Callback tampering test (wrong amount) fails verification; order auto-moves to `paid` |
| **BE-2.3** Finglish and colloquial (G14) | Section 10.4 | Finglish eval subset intent accuracy within 5 points of Persian-script accuracy |
| **BE-2.4** Voice notes (G15 input) | ASR service integration, transcripts, chunking | Transcripts shown in inbox; voice questions answered; ASR outage → handoff, not silence |
| **BE-2.5** Feed import (G20) | Read past posts/captions/highlights via API, extract products, prices and FAQs with AI into **draft** knowledge and catalog for owner review | 50-post feed produces draft catalog; nothing published without owner confirmation |
| **BE-2.6** Bulk price update (G8) | Section 9.8 bulk | Preview equals applied result; undo restores; cards and knowledge refreshed within 1 minute |
| **BE-2.7** Outage resilience v1 (G11) | Phone capture with consent (flow node + AI tool), SMS and Bale adapters, Bale account linking via deep link, outage detector, "move my customers" campaign (SMS/Bale to consented contacts with link), conversation continuity on Bale in the same inbox | Simulated Instagram outage triggers detector and owner notification; campaign only reaches consented contacts |
| **BE-2.8** Usage billing with soft overage (G31) | Section 9.13 full: wallet, overage cap, installments, invoices, Zarinpal checkout, dunning | Bot keeps answering after quota with overage accrued; cap reached → owner notified, AI falls back to rules + handoff (never silent) |
| **BE-2.9** Industry templates (G32) | Templates (knowledge skeleton, flows, rules, agent profile) for 5 verticals, applied at onboarding | Applying a template is idempotent and editable afterwards |
| **BE-2.10** Receipt verification (G1) | Section 10.5 | Duplicate receipt flagged; amount mismatch → `needs_review`; precision ≥ 95% on a labeled receipt set before enabling auto-approve |
| **BE-2.11** Launch hardening | Load test at 2× capacity plan, external penetration test, DR drill, Kubernetes deploy | Milestone M2 |

### Phase 3 — Growth, P2 features (months 8–14)

| WP | Scope |
|---|---|
| BE-3.1 Shipping (G4) | Section 9.11: tracking codes → automatic messages; carrier adapters where APIs exist; shipping cost by city |
| BE-3.2 Visual product search (G16) | Image embeddings for catalog images; screenshot → nearest products with threshold |
| BE-3.3 Rate-linked pricing (G9) | Rate sources (gold, USD) with admin-configured providers, formula pricing, AI quotes today's price with timestamp |
| BE-3.4 Revenue attribution (G23) | Attribution paths from post/Reel/story/live through order and payment; read replica |
| BE-3.5 Conversation intelligence (G24) | Weekly Persian insights: top questions, unanswered, lost-sale reasons, objections, demand trends |
| BE-3.6 Negotiation guardrails (G5) | Min price, discount ladders, bundles enforced in code; AI may offer only tool-approved discounts |
| BE-3.7 Post-sale agent (G7) | Returns, exchanges, warranty flows with policy knowledge |
| BE-3.8 Content studio (G19), campaign generator (G21), Jalali planner (G22) | AI captions/hashtags/Reels scripts, one-step campaign creation (post caption + keyword rule + flow + follow-up), holiday calendar data |
| BE-3.9 Comment moderation (G25) | Classify and hide insults/spam/scam links via API; complaint-thread alerts |
| BE-3.10 More channels | Rubika, Eitaa, Telegram, web widget adapters into the unified inbox |
| BE-3.11 Agency mode (G29) | Organizations, client workspaces, white-label settings, reseller billing |
| BE-3.12 Public API and webhooks (feature 79) | API keys (hashed, scoped), outbound webhooks with HMAC signatures and retries |
| BE-3.13 Hardening | RLS on sensitive tables, WebAuthn for staff, read replicas, partition archival |

### Phase 4 — Expansion, P3 features (month 14+)

| WP | Scope |
|---|---|
| BE-4.1 Bank SMS matching (G3) | Companion-app ingestion API with device binding, consent, strict parsing; match deposits to orders |
| BE-4.2 Accounting and marketplace sync (G30) | Holoo, Sepidar, Hesabfa, WooCommerce, Torob, Basalam connectors (stock and price sync) |
| BE-4.3 Fraud and impersonation detection (G26) | Deeper receipt forensics; lookalike page detection |
| BE-4.4 Persian TTS replies (G15 output) | `ITextToSpeech`, voice-message replies in brand voice |
| BE-4.5 Dari, Tajik, diaspora (G17) | Locale-aware normalization (Cyrillic Tajik transliteration), multi-currency Money |
| BE-4.6 Persian visual generation (G18) | Server-side rendering of Persian text on images (HarfBuzz shaping via SkiaSharp) |
| BE-4.7 Pre-orders and restock alerts (G6), lead scoring and win-back (G27) | Waitlists, restock notifications, scoring model, consented win-back campaigns |
| BE-4.8 Offline sync API (G13) | Delta sync endpoints and conflict rules for an offline-tolerant mobile app |

---

## 15. Security checklist per work package

Before marking any WP done, confirm:

- [ ] Every new endpoint has an authorization policy and a resource-ownership check; cross-tenant test added.
- [ ] Input validated (FluentValidation) with limits on lengths, collection sizes and numbers.
- [ ] No secrets, tokens, phone or card numbers in logs (check redaction test).
- [ ] Outbound HTTP goes through a resilient named client; user-supplied URLs go through `SafeHttpClient`.
- [ ] Any file goes through the Files module and is used only when `clean`.
- [ ] Mutations that can be retried support `Idempotency-Key`.
- [ ] New events go through the outbox and consumers are idempotent.
- [ ] Rate limits applied to new public or expensive endpoints.
- [ ] Audit entries for sensitive actions.
- [ ] Cache keys include workspace ID; invalidation tested.

## 16. Notes for the implementing agent

- Work package by work package; open one pull request per WP with tests; keep the OpenAPI export current (see overview section 6, step 1: publish endpoint signatures before logic when a frontend depends on them).
- When a decision is not covered here, choose the simpler option, record it as an ADR in `docs/adr/NNNN-title.md`, and keep the module boundary rules.
- Do not add a package outside section 1.4 without an ADR that checks its license.
- Use fake Persian data generators (`Bogus` with custom Persian name/city lists) for seeds and tests.
