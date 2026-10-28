# Implementation Plan: Overview and Shared Contract

**Product:** AI-powered Instagram automation platform for the Persian-speaking market
**Codename used in these plans:** `Pishkhan` (پیشخوان). Replace it everywhere with the final brand name.
**Source:** `/mnt/project-files/research/persian-ai-instagram-bot-market-report.md` (85 features, gaps G1 to G32, priorities P0 to P3)
**Date:** 28 October 2026

This file is shared context for every implementation agent. Read it first, then read the plan for your part:

| File | Part | Who implements it |
|---|---|---|
| `00-overview.md` | Shared decisions, contracts, phase map | Everyone (read only) |
| `01-backend.md` | .NET 10 backend, workers, Kafka, Redis, PostgreSQL, AV scanning | Backend agent(s) |
| `02-admin-panel.md` | Internal back-office panel (Next.js) | Admin panel agent |
| `03-public-website.md` | Public marketing site, pricing, blog, help center (Next.js) | Website agent |
| `04-user-panel.md` | Customer panel for sellers, agencies and their teams (Next.js, PWA) | User panel agent |

Each part plan is self-sufficient. Where a part depends on another, the dependency is stated as an API contract, not as "ask the other agent".

---

## 1. Assumptions (the report's open questions are still unanswered)

The report left three questions open. Until Parsa answers them, all plans use these assumptions. Each is isolated behind an interface so a different answer changes configuration or one adapter, not the architecture.

| # | Open question | Assumption used in these plans | Where it is isolated |
|---|---|---|---|
| A1 | Where is the company registered (Meta access, payments)? | A legal entity that can register a Meta app and pass Meta business verification exists. The platform uses the **Instagram API with Instagram Login** (official, OAuth, no passwords). | `Channels.Instagram` adapter in the backend. |
| A2 | Hosting under sanctions and filtering | Two network zones. **Domestic zone** (Iranian data center or Iranian cloud such as ArvanCloud) hosts the panels, API, PostgreSQL, Redis, Kafka and files, so sellers can work without a VPN (gap G12). **International edge zone** (small footprint outside Iran) hosts only the components that must reach Meta and foreign AI providers: Meta webhook ingress and the outbound Graph API sender. Both zones run the same container images. For the MVP everything may run in one zone; the split is a deployment change, not a code change. | Backend `Ingress` host and the `outbound-sender` worker group are separately deployable. |
| A3 | Which AI providers can be used lawfully | No single provider is assumed. All model calls go through one gateway that speaks the **OpenAI-compatible chat/embeddings protocol**, which both permitted hosted providers and self-hosted open-weight models (served by vLLM or similar) expose. Model choice is per task and set in the admin panel. | Backend `Pishkhan.AI` gateway (`IChatClient` from `Microsoft.Extensions.AI`). |
| A4 | Iran only or wider? | Iran first. UI language `fa-IR` only, RTL, Jalali dates, prices shown in Toman. Code is i18n-ready (`fa-AF` Dari and diaspora currencies are Phase 4, gap G17). | `next-intl` messages, backend `Money` and `Currency` types. |
| A5 | Launch vertical | Clothing, cosmetics, gold and jewelry, clinics and beauty, courses (report section 8.1). Templates for these ship in Phase 2. | Industry templates are data, managed in the admin panel. |
| A6 | Payment providers | Zarinpal first, for both our own subscription billing and sellers' in-chat payment links (sellers use their **own** merchant ID; we never hold sellers' money). Second PSP later through the same interface. | `Payments` module `IPaymentGateway`. |
| A7 | SMS and domestic messengers | One Iranian SMS provider (for example Kavenegar) behind `ISmsSender`. Bale bot API is the first domestic messenger (report: "start with SMS and one domestic messenger"). | `Notifications` and `Channels` adapters. |

**Never build** (report section 7.6): auto-follow, auto-like, mass comments, any password-based Instagram connection, unsolicited bulk DMs outside Meta's messaging window.

---

## 2. System at a glance

```
                       ┌────────────────────── International edge zone ─────────────────────┐
 Meta webhooks ───────▶│  Ingress host (verify signature, dedupe, produce to Kafka, 200 OK) │
 Graph API  ◀──────────│  Workers: outbound-sender (rate limited)                           │
 AI providers ◀────────│  (optional) AI gateway egress                                      │
                       └───────────────────────────────┬────────────────────────────────────┘
                                                       │ Kafka (MirrorMaker 2 or one cluster
                                                       │ reachable from both zones over VPN)
 ┌─────────────────────────────────── Domestic zone ───┴─────────────────────────────────────┐
 │  Next.js: site (example.ir)  panel (app.example.ir)  admin (admin, private network only)  │
 │        │ BFF route handlers (httpOnly cookies)                                             │
 │        ▼                                                                                    │
 │  Api host (REST + SignalR)      AdminApi host (private)      Workers (MassTransit + jobs)  │
 │        │                                │                           │                       │
 │  PostgreSQL 18 + pgvector   Redis (cache)  Redis (state)   Kafka   S3 storage   ClamAV     │
 └─────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Fixed technology choices

| Concern | Choice | Notes |
|---|---|---|
| Backend runtime | .NET 10 (LTS), C# 14, ASP.NET Core Minimal APIs | Required by Parsa. |
| Database | PostgreSQL 18 (17 acceptable) with `pgvector` and `pg_trgm` | Required. One database, one schema per module. |
| Caching | Redis 8 (or Valkey 8) via `HybridCache` (L1 memory + L2 Redis) | Required. Two logical deployments: cache (evicting) and state (non-evicting). |
| Events | Apache Kafka 4.x (KRaft) through **MassTransit 8.5.x** Kafka rider | Required. Pin MassTransit to 8.5.x: version 9 changed to a commercial license. |
| Background work | .NET Worker hosts: MassTransit consumers, Quartz.NET for recurring jobs, a PostgreSQL-backed timer table for per-entity delays | Required. |
| Antivirus | ClamAV (`clamd`) over TCP via `nClam`, private signature mirror | Required. Every file, uploaded or downloaded from Instagram, is scanned before use. |
| Object storage | S3-compatible (MinIO self-hosted or ArvanCloud Object Storage) | Domestic. |
| Frontend | Next.js 16 (App Router, route groups), React 19, TypeScript strict | Required. Self-hosted (`output: "standalone"`), not Vercel. |
| UI kit | Tailwind CSS 4, shadcn/ui (Radix) with RTL, Vazirmatn font self-hosted | No Google Fonts or foreign CDNs at runtime (filtering and sanctions). |
| API contract | OpenAPI 3.1 generated by ASP.NET Core; TypeScript client generated by `orval` | Contract-first workflow (section 6). |

**License watch:** MediatR, AutoMapper, FluentAssertions and MassTransit 9 moved to commercial licenses in 2025. Do not use them. The backend plan names the free replacements.

---

## 4. Repository layout (one monorepo)

```
pishkhan/
├── backend/                     # .NET solution (see 01-backend.md)
├── frontend/                    # pnpm + Turborepo workspace
│   ├── apps/
│   │   ├── site/                # public website   (03-public-website.md)
│   │   ├── panel/               # user panel       (04-user-panel.md)
│   │   └── admin/               # admin panel      (02-admin-panel.md)
│   └── packages/
│       ├── ui/                  # shadcn/ui components, RTL, theme tokens
│       ├── api-client/          # orval-generated clients (panel, admin, public)
│       ├── auth/                # BFF helpers: cookie session, refresh, CSRF
│       ├── i18n/                # next-intl setup, fa-IR messages, Jalali utils, digit utils
│       ├── config/              # eslint, tsconfig, tailwind preset, prettier
│       └── test-utils/          # MSW setup, fixtures, Playwright helpers
├── infra/
│   ├── docker-compose.yml       # local stack: postgres, redis x2, kafka, kafka-ui, clamav, minio, otel, grafana
│   ├── docker/                  # Dockerfiles
│   └── k8s/                     # Helm charts (Phase 2)
└── docs/
    ├── plans/                   # these files
    └── adr/                     # architecture decision records
```

**Why three Next.js apps instead of one app with three route groups.** All three apps use route groups internally (for example `(auth)` and `(dashboard)` inside the panel). The three surfaces are kept as separate apps because: the admin panel must be deployable only on a private network and must not ship in the public bundle or be routable from the public host; the website needs aggressive static generation and caching while the panels are fully dynamic; and three agents can work in parallel without touching each other's files. Shared code lives in `packages/*`. If Parsa prefers a single Next.js app with `(site)`, `(panel)` and `(admin)` route groups, the plans still apply: each app's `app/` tree becomes one route group and host-based routing in `proxy.ts` replaces the separate deployments.

---

## 5. Domains, hosts and authentication model

| Surface | Host (example) | Audience | Backend API it calls | Auth |
|---|---|---|---|---|
| Website | `example.ir` | Public | `Api` public endpoints `/api/v1/public/*` | None (plus anonymous demo token) |
| User panel | `app.example.ir` | Sellers, team members, agencies | `Api` `/api/v1/*` + SignalR `/hubs/inbox` | Phone OTP (+ optional password and TOTP) |
| Admin panel | `admin.internal.example.ir` (VPN / zero-trust only) | Staff | `AdminApi` `/admin/v1/*` | Username + password + mandatory TOTP |
| User content | `usercontent.example.ir` | Anyone with a signed link | Signed URLs to S3 | Short-lived signature |
| Payment links | `pay.example.ir` | Sellers' customers | `Api` `/api/v1/pay/*` | Opaque token in URL |

**BFF rule (all Next.js apps):** the browser never sees access or refresh tokens. Next.js route handlers under `/api/auth/*` exchange credentials with the backend, store the refresh token in an `__Host-` prefixed, `HttpOnly`, `Secure`, `SameSite=Lax` cookie and the short-lived access token in a separate `HttpOnly` cookie. Client components call the backend through the app's own `/api/bff/[...path]` proxy route, which attaches the bearer token, refreshes when needed, and checks `Origin` plus a double-submit CSRF token on mutations. SignalR connects directly to the `Api` host using a 5-minute hub-scoped token from `/api/auth/realtime-token`.

---

## 6. Contract-first workflow between agents

1. **The backend owns the contract.** For every feature, the backend agent first merges the endpoint signatures and DTOs (handlers may return `501 Not Implemented`) so the OpenAPI document changes before the logic exists.
2. OpenAPI documents are exported at build time to `backend/openapi/{api,admin,public}.v1.json` and committed.
3. `frontend/packages/api-client` regenerates with `pnpm gen:api`. CI fails if the committed client is stale.
4. Frontend agents build against **MSW** handlers in `packages/test-utils/mocks` generated by `orval` (mock mode) plus hand-written realistic Persian fixtures. Frontends never wait for backend logic.
5. Breaking API changes require a new version path (`/api/v2`) or an additive change; removing a field needs a deprecation note in the OpenAPI description for one release.

### Shared API conventions (frontends rely on these)

- JSON, `camelCase`, UTF-8. All timestamps are ISO 8601 UTC (`2026-10-28T12:00:00Z`); frontends convert to Asia/Tehran and Jalali for display.
- **Money** is `{ "amount": 1250000, "currency": "IRR" }`, amount as an integer in the minor unit (Rial). Frontends display Toman (Rial ÷ 10) with Persian digits and thousands separators.
- IDs are UUID v7 strings.
- Errors are RFC 9457 Problem Details: `{ type, title, status, detail, instance, traceId, errors? }`; `errors` maps field names to message keys (for example `"validation.phone.invalid"`), translated by the frontend.
- List endpoints use cursor pagination: `?cursor=&limit=` → `{ items, nextCursor }`. Admin tables may also use `?page=&pageSize=` with `totalCount`.
- Mutations that can be retried accept an `Idempotency-Key` header (UUID).
- Workspace-scoped endpoints are under `/api/v1/workspaces/{workspaceId}/...`.
- Permission errors are `403` with `type` `https://pishkhan.ir/problems/forbidden`; plan-limit errors are `402` with `type` `.../plan-limit` and `detail` naming the limit, which the panel turns into an upgrade prompt.

### Realtime events (SignalR hub `/hubs/inbox`)

| Event | Payload (summary) | Consumers |
|---|---|---|
| `conversation.updated` | id, status, assignee, lastMessage, unreadCount | Panel inbox list |
| `message.created` | conversationId, message DTO | Panel conversation view |
| `message.status` | messageId, status (sent, delivered, read, failed) | Panel |
| `handoff.requested` | conversationId, reason | Panel notification |
| `receipt.verified` | orderId, result | Panel orders (Phase 2) |
| `channel.status` | channelAccountId, status | Panel channels, banner |
| `notification` | in-app notification DTO | Panel bell |

---

## 7. Phase map across all parts

Phases follow the report's roadmap (section 9). Timeframes are indicative and assume parallel agents.

| Phase | Report scope | Backend (`01`) | Admin panel (`02`) | Website (`03`) | User panel (`04`) |
|---|---|---|---|---|---|
| **0. Foundations** (weeks 1–6) | Foundations | Solution skeleton, infra, identity, tenancy, Kafka/outbox, Redis, files + AV, AI gateway, Persian normalizer, Instagram spike | Shell, admin auth + TOTP, RBAC, audit viewer, system health | Shell, design system, landing, legal pages incl. Meta data-deletion page, waitlist | Shell, OTP login, workspace creation, PWA shell |
| **1. MVP / P0** (months 2–5) | Section 7.2 | Instagram channel, conversations, automation engine, AI agent v1, inbox + handoff, catalog + storefront, forms + CRM, follow-gate + reminders, onboarding + test mode, dashboard, plans + quotas | Tenants, users, impersonation, plans, AI model routing, prompt versions, eval runs, file quarantine, DLQ, feature flags, CMS | Feature pages, vertical pages, pricing page, blog, help center, demo chat, sign-up flow | Onboarding wizard, inbox, automations + flow builder, AI agent settings, knowledge, catalog, forms, CRM, team, dashboard |
| **2. Differentiated launch / P1** (months 5–8) | Section 7.3 | Receipt verification, PSP links + orders, Finglish, voice notes, feed import, bulk prices, SMS + Bale fallback, usage billing with soft overage, industry templates, launch hardening | Billing ops, refunds, coupons, bank receipt templates, industry template editor, outage mode, SMS provider config | Launch campaign, differentiator pages, pricing calculator, case studies, affiliate page, status page | Orders + receipt queue, payment settings, voice and Finglish display, feed import, bulk price editor, outage fallback, billing + usage |
| **3. Growth / P2** (months 8–14) | Section 7.4 | Shipping, visual search, gold pricing, attribution, insights, negotiation, post-sale agent, content studio, campaigns + Jalali planner, moderation, more channels, agency mode, public API + webhooks | Agency/reseller management, carrier configs, rate sources, moderation queues, API client management | Agency landing, integrations directory, developer docs | Shipping, gold pricing, attribution and insights, content studio, campaign planner, moderation, unified inbox, agency mode, API keys + webhooks |
| **4. Expansion / P3** (month 14+) | Section 7.5 | Bank SMS matching, accounting sync, fraud detection, TTS, Dari/Tajik + multi-currency, visual generation, pre-orders, lead scoring, offline sync API | Fraud review, integration health, locale management | `fa-AF` and diaspora pages | Pre-orders, lead scoring, offline queue, multi-currency, integrations |

**Cross-part milestones**

| Milestone | Requires |
|---|---|
| M0 "Hello DM" (end of Phase 0) | Backend receives a real Instagram DM in a test account and replies; panel can log in and see it. |
| M1 Closed beta (end of Phase 1) | 30–50 sellers; all P0 features; admin can onboard, impersonate and support them; website has waitlist and legal pages. |
| M2 Public launch (end of Phase 2) | P1 features, paid plans live, eNamad seal visible, load and penetration tests passed. |

---

## 8. Definition of done (all agents)

A work package is done when:

1. Code builds with zero warnings treated as errors (backend `TreatWarningsAsErrors`, frontend `tsc --noEmit` and ESLint clean).
2. Tests listed in the work package pass in CI; new code has unit tests, and integration or end-to-end tests where the package says so.
3. Every user-facing string is in Persian through the i18n layer (frontend) or a message key (backend); no hard-coded English in UI.
4. Security checklist for the package is satisfied (each plan has one).
5. OpenAPI and generated clients are up to date.
6. A short entry is added to `docs/CHANGELOG.md` and any new decision to `docs/adr/`.
7. No secrets in code, logs or test fixtures. Fixtures use fake Iranian data (fake phone numbers `0912000xxxx`, fake card numbers that fail Luhn).

## 9. Glossary

| Term | Meaning |
|---|---|
| Workspace | A tenant: one business (or one agency client). Owns channels, contacts, catalog, automations. |
| Channel account | One connected Instagram professional account, Bale bot, SMS sender or web widget. |
| Conversation | A thread between one contact and one channel account. |
| Contact | An end customer of the seller. |
| Handoff | Moving a conversation from the AI or automation to a human team member. |
| 24-hour window | Meta rule: a business may send standard messages only within 24 hours of the customer's last message. |
| Card-to-card (کارت به کارت) | Bank transfer from the customer's card to the seller's card, proven by a receipt screenshot. |
| PSP | Shaparak payment service provider (for example Zarinpal). |
| Toman | Display unit, 1 Toman = 10 Rial. Store Rial. |

---

## 10. Shared frontend foundation (`frontend/packages/*`)

All three Next.js apps use these packages. **Ownership:** the user panel agent builds them first as work package `UP-0.1` in `04-user-panel.md`. The admin and website agents consume them; if they start before `UP-0.1` is merged, they may add what they need to the package, following this spec, in a separate pull request.

| Package | Contents |
|---|---|
| `@pishkhan/config` | `tsconfig` bases (strict, `noUncheckedIndexedAccess`), ESLint flat config (Next, TypeScript, jsx-a11y, import order), Prettier, Tailwind 4 preset with design tokens |
| `@pishkhan/ui` | shadcn/ui components generated into the package and adapted for RTL (logical properties `ms-*`/`me-*`/`ps-*`/`pe-*` only, mirrored icons where direction matters), `DirectionProvider dir="rtl"`, theme tokens (light/dark), Vazirmatn variable font (self-hosted `woff2` with `next/font/local`, Persian digits by default), components: `DataTable` (TanStack Table), `JalaliDatePicker`, `MoneyInput` (Toman input, Rial value), `PhoneInput` (Iranian mobile), `PersianNumber`, `EmptyState`, `ConfirmDialog`, `FileUpload` (shows scan status: در حال بررسی امنیتی / سالم / رد شد), `Toast` |
| `@pishkhan/i18n` | `next-intl` setup with `fa-IR` default, message catalogs per app, ICU plurals, `formatToman`, `toPersianDigits`, `toLatinDigits` (for inputs), Jalali formatting via `date-fns-jalali`, relative time in Persian, backend error-key translation |
| `@pishkhan/api-client` | orval-generated TypeScript clients and TanStack Query hooks for `api`, `admin`, `public` OpenAPI documents; a `fetcher` that targets the app's BFF proxy in the browser and the backend directly (with token) on the server; Zod schemas generated for forms |
| `@pishkhan/auth` | BFF helpers: cookie names and options, `getSession()` for server components, refresh logic with single-flight lock, `/api/bff/[...path]` proxy handler factory with CSRF and `Origin` checks, `/api/auth/*` handler factories, `proxy.ts` (Next.js 16 request proxy, formerly middleware) helpers for protected route groups |
| `@pishkhan/test-utils` | MSW handlers (orval mocks + Persian fixtures), render helpers with providers, Playwright fixtures (logged-in user, seeded workspace), axe accessibility helper |

**Frontend rules for all apps**

- Next.js 16 App Router, React Server Components by default, client components only where interaction needs them. `output: "standalone"`, self-hosted Node 22+ behind Nginx in the domestic zone; `next/image` optimization runs on the server (sharp).
- No runtime dependency on foreign CDNs or Google services (fonts, analytics, reCAPTCHA, maps). Use self-hosted alternatives: Vazirmatn local font, Umami (self-hosted) analytics on the website only, ALTCHA proof-of-work for bot protection, Neshan or Map.ir only if a map is ever needed.
- `<html lang="fa" dir="rtl">`. Inputs that hold numbers, phones, card numbers, URLs or code are `dir="ltr"` inside the RTL layout. Accept Persian and Arabic digits in every numeric input and convert before sending.
- Accessibility: WCAG 2.2 AA, keyboard navigable, axe checks in Playwright.
- Security headers via `next.config` and `proxy.ts`: strict CSP with nonces, `frame-ancestors 'none'` (panel and admin), HSTS, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy`.
- Data fetching: server components call the backend with the session token; client components use the generated TanStack Query hooks through the BFF proxy. Never put tokens in `localStorage`.
- Forms: `react-hook-form` + generated Zod schemas, Persian validation messages.
- Testing: Vitest + Testing Library for units, Playwright for end-to-end against MSW (CI) and against the docker-compose backend (nightly).
