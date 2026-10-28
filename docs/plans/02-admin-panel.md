# Admin Panel Implementation Plan (Next.js)

**Read first:** `00-overview.md` (assumptions, API conventions, shared frontend foundation in section 10, phase map).
**App:** `frontend/apps/admin`
**Users:** internal staff only (support, finance, content editors, AI operations, super admins).
**Backend:** `AdminApi` host, `/admin/v1/*`, OpenAPI document `backend/openapi/admin.v1.json` (backend plan section 9.19).

---

## 1. Purpose and principles

The admin panel is the back office that lets the team run the business: onboard and support sellers during the closed beta, manage plans and billing, control AI models and prompts safely, keep the website content current, and watch platform health (Kafka lag, dead letters, antivirus quarantine, Meta errors).

Principles:
1. **Security over convenience.** This app is reachable only from the private network (VPN or zero-trust proxy), never from the public internet. Every action is audited.
2. **Least privilege.** Staff roles map to permissions; the UI hides what a role cannot do, and the backend enforces it regardless.
3. **Dangerous actions are deliberate.** Impersonation, refunds, data deletion, AI provider changes and outage mode require step-up re-authentication and a typed reason.
4. **Fast for operators.** Dense tables, keyboard shortcuts, global search, deep links that can be pasted into a support chat.
5. **Persian UI**, RTL, Jalali dates, Toman amounts (same as the other apps), so staff see exactly what sellers see.

---

## 2. Technical setup

| Concern | Choice |
|---|---|
| Framework | Next.js 16 App Router, React 19, TypeScript strict |
| Shared packages | `@pishkhan/ui`, `@pishkhan/i18n`, `@pishkhan/api-client` (admin client), `@pishkhan/auth`, `@pishkhan/config`, `@pishkhan/test-utils` |
| Data | Server components for initial page data; TanStack Query (generated hooks) for tables, filters and mutations |
| Tables | `DataTable` from `@pishkhan/ui` (TanStack Table): server-side pagination (`page`, `pageSize`, `totalCount`), sorting, column visibility, saved filters in URL search params |
| Forms | react-hook-form + generated Zod schemas |
| Charts | Recharts (lightweight, RTL-friendly with mirrored axes) |
| Code/JSON viewers | Monaco editor (self-hosted, lazy-loaded) for prompt templates, JSON payloads and flow templates |
| Rich text for CMS | Tiptap with RTL and ZWNJ support, block-based JSON output matching the backend Content module |
| Realtime (optional) | Polling with TanStack Query `refetchInterval` for health pages; no SignalR needed in Phase 1 |
| Deployment | Separate container, `output: "standalone"`, served only on `admin.internal.example.ir` |

### 2.1 Route groups and structure

```
apps/admin/
├── app/
│   ├── (auth)/                         # unauthenticated layout, centered card
│   │   ├── login/page.tsx              # username + password
│   │   ├── totp/page.tsx               # TOTP step (mandatory)
│   │   └── totp-setup/page.tsx         # first login: enroll TOTP, show recovery codes once
│   ├── (console)/                      # authenticated layout: sidebar, top bar, global search, command palette
│   │   ├── layout.tsx                  # loads staff session + permissions, renders nav filtered by permission
│   │   ├── page.tsx                    # home: KPIs + alerts
│   │   ├── workspaces/                 # list, [workspaceId]/(overview|members|channels|usage|billing|audit|flags)
│   │   ├── users/                      # list, [userId]
│   │   ├── billing/                    # plans, subscriptions, invoices, refunds, coupons, wallet adjustments
│   │   ├── ai/                         # model-routes, providers, prompts, evals, traces
│   │   ├── templates/                  # industry templates, bank receipt templates
│   │   ├── content/                    # pages, posts, faqs, help, case-studies, media, redirects
│   │   ├── support/                    # tickets (Phase 2), announcements
│   │   ├── security/                   # file-quarantine, audit-log, staff, sessions
│   │   ├── platform/                   # health, kafka, dlq, workers, meta-app, feature-flags, outage-mode
│   │   └── settings/                   # my account, TOTP, sessions
│   ├── api/
│   │   ├── auth/[...action]/route.ts   # login, totp, refresh, logout, step-up (factories from @pishkhan/auth)
│   │   └── bff/[...path]/route.ts      # proxy to AdminApi with bearer, CSRF + Origin checks
│   └── layout.tsx                      # <html lang="fa" dir="rtl">, providers
├── proxy.ts                            # protect (console), redirect to /login, enforce TOTP completed, security headers, IP allowlist header check
├── messages/fa.json
└── tests/ (e2e with Playwright)
```

Use additional nested route groups where a section has its own layout, for example `workspaces/[workspaceId]/(tabs)/layout.tsx` for the tabbed workspace view.

### 2.2 Authentication and session

- Login: `POST /api/auth/login` (BFF) → `AdminApi` returns a pending-TOTP ticket → `/totp` → tokens set as `__Host-admin_rt` and `__Host-admin_at` cookies (`HttpOnly`, `Secure`, `SameSite=Strict`).
- Idle timeout 15 minutes (client warns at 13 minutes with a countdown dialog), absolute 8 hours; backend enforces both.
- **Step-up:** actions marked dangerous call `POST /api/auth/step-up` (re-enter TOTP), receive a 5-minute step-up claim, then perform the action. Implement as a `useStepUp()` hook that wraps mutations and opens a TOTP dialog when the backend returns `401` with problem type `step-up-required`.
- Permissions come from the session (`/admin/v1/me`); `can("billing.refund")` helper used by nav, buttons and pages. Pages without permission render a 403 page.

### 2.3 Security requirements (frontend side)

- CSP with nonces, no inline scripts, `frame-ancestors 'none'`, `connect-src` limited to self.
- No staff data in `localStorage`; table preferences stored in cookies or the backend.
- Every destructive or sensitive action uses `ConfirmDialog` with a **required reason** text field, sent to the backend for the audit log.
- Impersonation opens the user panel in a new tab through a one-time link from the backend; the user panel shows a red "support session" banner (see `04-user-panel.md`). The admin app itself never holds user tokens.
- Mask sensitive values by default (phone numbers shown as `0912***4567`, card numbers as last 4); reveal requires permission and is audited (`POST /admin/v1/.../reveal`).
- Export buttons (CSV/Excel) only for roles with `export` permission; exports are generated by the backend and audited.

---

## 3. Staff roles and permissions (UI mapping)

| Role | Can see | Can do |
|---|---|---|
| SuperAdmin | Everything | Everything, including staff management, AI provider credentials, outage mode |
| Support | Workspaces, users, channels, conversations metadata, audit (workspace scope), quarantine | Impersonate (with reason), resend invitations, reset channel connection, suspend workspace (Phase 2 with approval), manage announcements |
| Finance | Billing, invoices, wallets, coupons, usage | Refunds, wallet adjustments, coupons, grant plans |
| ContentEditor | Content section | Create, edit, publish website content |
| AIOps | AI section, templates, eval results, traces (redacted) | Edit prompts and model routes (activation requires passing eval gate), manage eval sets and templates |
| ReadOnly | Dashboards and lists | Nothing |

The backend is the source of truth for permissions; the frontend only reflects them.

---

## 4. Feature specifications

Each feature lists screens, key components, backend endpoints (indicative; use the generated client) and acceptance criteria.

### 4.1 Home dashboard
- KPI tiles: active workspaces, new sign-ups (today/7d), paying workspaces, MRR in Toman, AI conversations today, handoff rate, receipt verifications (Phase 2), error-rate status of Meta API, Kafka lag status, AV quarantine count.
- Alerts list from backend (`GET /admin/v1/alerts`): consumer lag, DLQ growth, ClamAV signatures stale, Meta error spikes, channel disconnections spike.
- ✅ Loads in < 1.5 s; each tile links to the detailed page with filters applied.

### 4.2 Workspaces
- List: search by name, slug, owner phone (exact), Instagram username; filters by plan, status, industry, created date (Jalali range), channel status.
- Detail tabs: Overview (owner, plan, usage this period, knowledge score, channel status, onboarding progress), Members, Channels (status history, last webhook received, rate profile override, "force token refresh", "mark reauth required"), Usage (meters vs entitlements chart), Billing (subscription, invoices, wallet), Feature flags (per-workspace overrides), Audit (workspace scope), Notes (internal staff notes).
- Actions: impersonate (step-up + reason), extend trial, grant plan (Finance), suspend / unsuspend (reason, notifies owner), schedule deletion (SuperAdmin, step-up).
- Endpoints: `GET /admin/v1/workspaces`, `GET /admin/v1/workspaces/{id}`, `.../members`, `.../channels`, `.../usage`, `POST .../impersonations`, `POST .../suspend`, `POST .../trial-extensions`, `PUT .../feature-flags`.
- ✅ Impersonation flow end to end in Playwright (with MSW): reason required, step-up dialog, new tab URL contains a one-time token only.

### 4.3 Users
- List and detail: phone (masked), workspaces and roles, sessions (revoke), sign-in history, 2FA status, OTP audit (rate-limit hits).
- Actions: revoke all sessions, block user (reason), unblock.

### 4.4 Billing (Phase 1 basic, Phase 2 full)
- Plans editor: name, description (Persian), price monthly/annual, entitlements per meter, overage unit prices (Phase 2), visibility (public/private), free tier flag; changes create a new plan version; existing subscriptions keep their version until renewal. Public pricing page reads published plans.
- Subscriptions list, invoices list and detail (PDF), payments (Zarinpal references), refunds (Finance, step-up, reason), wallet adjustments, coupons (code, percent/amount, validity, usage limits), installment plans (Phase 2), affiliate payouts (Phase 2).
- ✅ Plan editor validates entitlements; preview shows the pricing card exactly as the website will render it (shared component from `@pishkhan/ui`).

### 4.5 AI operations
- **Providers:** list of OpenAI-compatible providers (base URL, key write-only, region/zone, status, test-connection button). SuperAdmin only.
- **Model routes:** per task (`classify`, `generate_reply`, `extract_receipt`, `transcribe`, `embed`, `summarize`, `content`): primary model, fallback chain, temperature, max tokens, timeout; per-plan or per-workspace overrides; cost per 1k tokens for metering.
- **Prompt templates:** versioned list per key, Monaco editor with variable highlighting, diff view between versions, "run eval" button, activation blocked until the selected eval run passes thresholds (gate shown as checklist: intent accuracy, groundedness, price correctness, handoff precision/recall, Finglish subset in Phase 2).
- **Evaluation sets and runs:** upload/curate cases (anonymized Persian conversations with expected intent, facts, handoff), run history with metric charts over time, per-case diff (expected vs actual), filter failures.
- **Traces viewer:** search AI traces by workspace/conversation/date, shows model, prompt version, retrieved chunks, tool calls, confidence, latency and cost; message text redacted unless the staff member has `ai.traces.read_content` and enters a reason.
- ✅ Changing a route takes effect without redeploy (backend cache invalidation) and is audited; activation of an unevaluated prompt is impossible from the UI and rejected by the API.

### 4.6 Templates
- **Industry templates** (Phase 2, gap G32): editor for vertical (clothing, cosmetics, gold, clinics/beauty, courses...), knowledge skeleton (required facts with Persian prompts), default rules and flows (JSON with a read-only flow preview), agent profile defaults, storefront examples; publish/version; preview "apply to a sandbox workspace".
- **Bank receipt templates** (Phase 2, gap G1): per bank app, sample images (uploaded via AV pipeline), field regexes and layout hints, test panel that runs extraction on a sample and shows parsed fields and confidence.
- **Holiday calendar** (Phase 3, G22): Jalali events (Nowruz, Yalda, Ramadan, Muharram, school season, Iranian "Black Friday") with dates per year and campaign template links.

### 4.7 Content (website CMS)
- Pages (block editor: hero, feature grid, comparison table, FAQ block, CTA, testimonial, pricing table, rich text, image), blog posts (title, slug with Persian-safe slugify, excerpt, cover, body, categories, SEO title/description, canonical, scheduled publish), FAQs, help center articles (categories, search keywords), case studies (metrics, seller quote, logo with consent flag), media library (uploads go through AV; shows scan status), redirects.
- Draft → review → published workflow; preview button opens the website with a preview token; publish triggers website revalidation (backend calls it).
- ✅ ZWNJ preserved in editor and output; slug generation handles Persian; publishing a post makes it visible on the website within 60 s.

### 4.8 Support (Phase 2)
- Tickets inbox (from in-panel support chat and email/SMS), assignment, canned responses, link to workspace, SLA timers.
- Announcements: banner messages for the user panel (target all / plan / workspace list, severity, Jalali schedule).

### 4.9 Security
- **File quarantine:** list of files with status `infected`, `scan_failed`, `pending_scan` older than 10 minutes; columns: workspace, purpose, detected type, size, signature name, scanned at; actions: re-scan, delete now, mark false positive (SuperAdmin, step-up; file still never served without a clean re-scan). Never a "download infected file" button.
- **Audit log:** search by actor (staff or user), workspace, action, entity, date range; detail shows before/after JSON diff.
- **Staff management** (SuperAdmin): invite staff, assign roles, force TOTP reset, deactivate.

### 4.10 Platform
- **Health:** status of each dependency (PostgreSQL, Redis cache, Redis state, Kafka, S3, ClamAV with signature age, AI providers, Meta API), with refresh every 15 s.
- **Kafka:** topics with partitions, consumer groups with lag per partition (chart), throughput.
- **Dead letters:** list by consumer group, inspect payload (JSON viewer) and error, replay one or many (step-up), discard with reason.
- **Workers:** worker groups, replicas, last heartbeat, Quartz jobs with last/next run and "trigger now".
- **Meta app:** app mode, permission status, webhook subscription health, per-account rate profile defaults (editable because Meta limits change), API version in use.
- **Feature flags:** global flags with percentage rollout and per-plan targeting.
- **Outage mode** (Phase 2, gap G11): shows outage detector state; SuperAdmin can declare an Instagram outage (step-up), which shows a banner to all sellers and enables one-click "move my customers" campaigns in their panels; end outage.

---

## 5. Phased work packages

### Phase 0 — Foundations (weeks 1–6)

**AD-0.1 App shell**
- Create `apps/admin` with route groups `(auth)` and `(console)`, layout with sidebar (collapsible, RTL), top bar (global search placeholder, staff menu), command palette (`Ctrl+K`), 403/404/500 pages in Persian.
- ✅ Builds, lints, type-checks; Playwright smoke test renders shell with MSW session.

**AD-0.2 Staff authentication**
- Login, TOTP, TOTP enrollment with recovery codes, refresh, logout, idle timeout dialog, step-up hook, `proxy.ts` protection.
- ✅ E2E: login → TOTP → console; idle timeout logs out; protected routes redirect; step-up dialog appears on `step-up-required`.

**AD-0.3 Permissions and navigation**
- `can()` helper, nav filtered by permission, per-page guards.
- ✅ Unit tests for each role's visible nav.

**AD-0.4 Audit log viewer and platform health (basic)**
- ✅ Health page shows each dependency; audit search works against MSW fixtures.

### Phase 1 — MVP support (months 2–5)

| WP | Scope | Acceptance |
|---|---|---|
| AD-1.1 | Workspaces list and detail (4.2) without billing tab | Search by owner phone and Instagram username; channel status history visible |
| AD-1.2 | Impersonation (4.2) | Reason + step-up; audit entry visible in audit log |
| AD-1.3 | Users (4.3) | Revoke sessions works |
| AD-1.4 | Plans editor and manual plan grants (4.4 basic) | Plan preview matches website pricing card |
| AD-1.5 | AI providers, model routes, prompt templates, eval runs, traces (4.5) | Eval gate enforced in UI |
| AD-1.6 | File quarantine (4.9) | Re-scan and delete actions; no download of infected files |
| AD-1.7 | Kafka, DLQ, workers, Meta app pages (4.10) | DLQ replay with step-up |
| AD-1.8 | Feature flags (4.10) | Per-workspace override shown on workspace detail |
| AD-1.9 | Content CMS (4.7) | Publish → website revalidation within 60 s (with real backend in nightly test) |
| AD-1.10 | Home dashboard (4.1) | KPIs and alerts link to filtered pages |

### Phase 2 — Launch operations (months 5–8)

| WP | Scope |
|---|---|
| AD-2.1 | Full billing: subscriptions, invoices, refunds, coupons, wallet adjustments, installments, affiliate payouts |
| AD-2.2 | Industry templates editor (G32) |
| AD-2.3 | Bank receipt templates with extraction test panel (G1) |
| AD-2.4 | SMS provider and Bale bot configuration; SMS logs |
| AD-2.5 | Outage mode (G11) |
| AD-2.6 | Support tickets and announcements |
| AD-2.7 | Staff management, sensitive-value reveal flows, exports |

### Phase 3 — Growth (months 8–14)

| WP | Scope |
|---|---|
| AD-3.1 | Agencies and resellers: organizations, client workspaces, white-label settings review, reseller billing (G29) |
| AD-3.2 | Carrier configuration and shipping status monitoring (G4) |
| AD-3.3 | Rate sources for gold/currency pricing with manual override and history (G9) |
| AD-3.4 | Moderation oversight: global spam/scam link lists, model thresholds (G25) |
| AD-3.5 | Public API client management: API key abuse monitoring, webhook delivery failures |
| AD-3.6 | Holiday calendar and campaign templates (G22) |
| AD-3.7 | WebAuthn for staff |

### Phase 4 — Expansion (month 14+)

| WP | Scope |
|---|---|
| AD-4.1 | Fraud review queue for suspicious receipts and impersonation reports (G26) |
| AD-4.2 | Integration health for accounting/marketplace connectors (G30) |
| AD-4.3 | Locale management for `fa-AF` and diaspora currencies (G17) |

---

## 6. Testing

- Unit (Vitest): permission helper, formatters, step-up hook, table URL-state sync.
- Component: DataTable filters, ConfirmDialog with reason, JSON diff viewer.
- E2E (Playwright + MSW): every WP has at least one happy path and one permission-denied path; axe accessibility check on each page.
- Nightly E2E against the docker-compose backend for login, impersonation, DLQ replay, content publish.

## 7. Definition of done (admin-specific additions)

- [ ] Page hidden and blocked for roles without permission (tested).
- [ ] Sensitive actions require reason and, where listed, step-up.
- [ ] Sensitive values masked by default.
- [ ] All lists support deep-linkable filters via URL search params.
- [ ] All strings in `messages/fa.json`; dates Jalali; amounts Toman with Persian digits.
