# Public Website Implementation Plan (Next.js)

**Read first:** `00-overview.md` (assumptions, API conventions, shared frontend foundation in section 10, phase map).
**App:** `frontend/apps/site`
**Audience:** Persian-speaking Instagram sellers, creators, clinics, course creators and page-admin agencies, mostly on mobile, often on slow or filtered connections.
**Backend:** `Api` host public endpoints `/api/v1/public/*`, OpenAPI `backend/openapi/public.v1.json` (backend plan section 9.16 and `GET /public/plans`).

---

## 1. Purpose

The website wins trust and sign-ups. The report's positioning is the brief:

> **"Your Persian-speaking sales team on Instagram, from the first DM to the delivered parcel, even when Instagram is down."**

It must lead with outcomes competitors cannot show (orders closed, receipts verified, customers kept during outages, real colloquial and voice Persian), show **one clear, honest pricing page** (report 8.5: competitors' pricing pages are contradictory), carry the trust signals Iranian buyers expect (eNamad seal, company details, Persian support on Telegram/Bale and phone), and host the legal pages Meta requires for app review (privacy policy, terms, data deletion instructions).

## 2. Goals and targets

| Goal | Target |
|---|---|
| Performance on a mid-range Android over 4G | LCP < 2.0 s, INP < 200 ms, CLS < 0.05, JS on landing < 120 KB gzipped |
| SEO | Persian keyword landing pages indexed (for example «دایرکت هوشمند اینستاگرام», «ربات فروش اینستاگرام», «پاسخگوی هوش مصنوعی دایرکت»), structured data valid |
| Conversion | Clear primary CTA on every page: «شروع رایگان» → user panel sign-up |
| Reachability | Works inside Iran without a VPN: domestic hosting, no foreign fonts/scripts/CDNs |
| Accessibility | WCAG 2.2 AA |

---

## 3. Technical setup

| Concern | Choice |
|---|---|
| Framework | Next.js 16 App Router, React 19, TypeScript strict, React Server Components |
| Rendering | Static generation by default; ISR (`revalidate`) plus **on-demand revalidation** from the CMS for content pages; dynamic only for the demo chat and forms |
| Styling | Tailwind CSS 4 with `@pishkhan/config` preset, `@pishkhan/ui` components, Vazirmatn self-hosted |
| Content | Backend Content module (CMS edited in the admin panel); block JSON rendered by a `BlockRenderer` |
| Pricing data | `GET /api/v1/public/plans` at build + revalidate on plan publish |
| Analytics | Self-hosted Umami (cookie-less) via a first-party script path |
| Bot protection | ALTCHA (self-hosted proof-of-work) on all forms and the demo chat |
| Images | `next/image` with self-hosted optimization; CMS images from the backend `public` bucket |
| Deployment | Container `output: "standalone"` behind Nginx with long cache headers for static assets; domestic zone |

### 3.1 Route groups and structure

```
apps/site/
├── app/
│   ├── (marketing)/                    # marketing layout: header with nav + CTA, footer with eNamad and contacts
│   │   ├── page.tsx                    # landing
│   │   ├── features/
│   │   │   ├── page.tsx                # all features overview
│   │   │   └── [slug]/page.tsx         # ai-agent, receipt-verification, payment-links, outage-proof, voice-and-finglish,
│   │   │                               # smart-direct, storefront, forms-crm, analytics, content-studio (Phase 3)...
│   │   ├── solutions/[vertical]/page.tsx   # clothing, cosmetics, gold-jewelry, clinics-beauty, courses (+ agencies Phase 3)
│   │   ├── pricing/page.tsx
│   │   ├── compare/page.tsx            # honest comparison vs "smart direct" tools (no competitor names in Phase 1; legal review)
│   │   ├── case-studies/ (page.tsx, [slug]/page.tsx)
│   │   ├── affiliate/page.tsx          # Phase 2
│   │   ├── about/page.tsx
│   │   └── contact/page.tsx
│   ├── (content)/                      # reading layout: narrower column, table of contents
│   │   ├── blog/ (page.tsx, [slug]/page.tsx, category/[slug]/page.tsx)
│   │   └── help/ (page.tsx, [category]/page.tsx, [category]/[slug]/page.tsx, search/page.tsx)
│   ├── (legal)/                        # plain layout
│   │   ├── terms/page.tsx
│   │   ├── privacy/page.tsx
│   │   ├── data-deletion/page.tsx      # Meta requirement: instructions + status lookup by confirmation code
│   │   ├── refund-policy/page.tsx
│   │   └── acceptable-use/page.tsx     # states we never offer growth bots or password logins
│   ├── (status)/status/page.tsx        # Phase 2: platform and Instagram reachability status
│   ├── api/
│   │   ├── revalidate/route.ts         # HMAC-verified on-demand revalidation from backend
│   │   ├── demo-chat/route.ts          # proxy to /public/demo-chat with ALTCHA verification and rate limit
│   │   └── leads/route.ts              # proxy to /public/leads
│   ├── sitemap.ts, robots.ts, manifest.ts, opengraph-image.tsx
│   └── layout.tsx                      # <html lang="fa" dir="rtl">
├── components/ (blocks/, pricing/, demo-chat/, seo/)
├── messages/fa.json
└── tests/
```

The site has no authenticated pages. "Login" and «شروع رایگان» link to the user panel (`https://app.example.ir/login`, `https://app.example.ir/signup?plan=...&ref=...`), passing plan, UTM and affiliate parameters.

### 3.2 Security

- Strict CSP with nonces; only self and the Umami path; `frame-ancestors 'none'`.
- Forms submit through the site's own route handlers, which verify ALTCHA and forward to the backend; the backend rate-limits again.
- `revalidate` endpoint requires an HMAC signature header and a timestamp within 5 minutes.
- No user-generated HTML: CMS blocks are rendered from structured JSON; rich text is sanitized server-side by the backend and rendered through an allowlist renderer.

### 3.3 SEO

- Metadata API per page (Persian titles and descriptions from CMS with fallbacks), canonical URLs, `og:locale=fa_IR`, generated Open Graph images with Persian text (`opengraph-image.tsx`, Vazirmatn embedded, correct shaping).
- JSON-LD: `Organization` (with eNamad and contact points), `SoftwareApplication` with `offers` from pricing, `FAQPage` on pricing and feature pages, `BlogPosting`, `BreadcrumbList`, `HowTo` for help articles where suitable.
- `sitemap.ts` built from CMS slugs; `robots.ts`; redirects from the CMS redirect table via `next.config` `redirects()` fetched at build and a catch-all fallback.
- Persian slugs supported (URL-encoded) but prefer short Latin slugs for feature pages; blog posts may use Persian slugs.
- Future (`fa-AF`, Phase 4): locale segment and `hreflang`.

---

## 4. Page specifications

### 4.1 Landing page
Sections in order:
1. **Hero:** headline built on the positioning line, sub-headline naming the three differentiators, primary CTA «شروع رایگان» and secondary «ببینید چطور کار می‌کند» (scroll to demo). Visual: phone mockup of an Instagram DM where the AI answers a Finglish question, verifies a receipt and sends a tracking code (static, lightweight SVG/WebP, no video autoplay).
2. **Live demo chat:** customers type a question in Persian or Finglish (or pick a sample: «قیمت این مانتو چنده؟», «salam, ersal be shiraz chand rooze?»); the AI answers as a demo clothing shop (backend demo workspace), showing confidence and "answered from: shipping policy" chips. Rate-limited, ALTCHA-protected, max 10 messages per visitor session.
3. **Outcomes strip:** three outcome cards (orders closed in DM, receipts checked automatically, customers kept during outages).
4. **How it works:** 3 steps (connect Instagram officially, bot learns from your page and catalog, it sells and hands off when needed).
5. **Feature grid:** table-stakes features grouped (smart direct, comment-to-DM, story/live, storefront, forms and CRM, team inbox) with "included in every plan".
6. **Differentiators deep-dive:** receipt verification (Phase 2 label "به‌زودی" until launched), payment links, voice and Finglish, outage-proof, honest pricing.
7. **Verticals:** cards for the five launch segments linking to solution pages.
8. **Trust:** official Meta API (no password), eNamad seal, company details, data security summary, support channels.
9. **Pricing teaser** with link to pricing.
10. **FAQ** (CMS FAQs tagged `landing`).
11. **Final CTA.**

Content for sections comes from the CMS page `home` (blocks), so marketing can edit without deploys; the component set is fixed.

### 4.2 Feature pages (`/features/[slug]`)
Template: problem (in the seller's words), how it works (steps with screenshots), what makes it different, FAQ, CTA. Each page has a target Persian keyword. Status badge for features not yet live («به‌زودی», with waitlist signup).

### 4.3 Solution pages (`/solutions/[vertical]`)
Per vertical: typical DMs in that vertical, which template is included (Phase 2 industry templates), relevant features (for gold: rate-linked pricing «قیمت لحظه‌ای طلا» when Phase 3 ships; for clinics: booking and "never say" medical-claims rules), case study, CTA with `?template=<vertical>`.

### 4.4 Pricing page
- Plans from `GET /public/plans`: monthly/annual toggle (annual shows installment option in Phase 2), price in Toman with Persian digits, what is metered (AI conversations), what is unlimited (rule-based automations), **soft overage explained plainly** ("ربات هیچ‌وقت وسط فروش خاموش نمی‌شود"), free tier highlighted, add-ons (receipt verification, extra channels, agency seats).
- **Usage calculator** (Phase 2): sliders for monthly DMs and share answered by AI → recommended plan and estimated monthly cost including overage, using plan data only (no hard-coded prices).
- No follower-count tiers anywhere (report gap G31).
- Comparison table (all plans × features), FAQ (refunds, trial, upgrades with credit, VAT).
- ✅ Prices on the page always match the backend plans (test renders from MSW plans; nightly test against real backend).

### 4.5 Blog and help center
- Blog: list with categories and pagination (static pages + ISR), post page with table of contents, reading time in Persian, related posts, CTA block, share links (Telegram, WhatsApp, Bale, X, copy link).
- Help center: categories, articles, client-side search over a prebuilt index (FlexSearch with a Persian-normalizing tokenizer; same normalization rules as the backend: ی/ک, digits, ZWNJ) with fallback to `GET /public/help/search`.
- Help articles needed by Meta app review and onboarding: "How to connect your Instagram professional account", "Why we never ask for your password", "How to remove access".

### 4.6 Legal pages
- Terms, privacy policy (what data, why, retention, processors, Meta data use), refund policy, acceptable use (no spam, no growth bots).
- **Data deletion page:** instructions for removing the app from Instagram settings and requesting deletion, plus a form to check deletion status with the confirmation code returned by the backend's Meta data-deletion callback.
- All legal pages are CMS-managed with version and "last updated" Jalali date.

### 4.7 Contact, about, affiliate, case studies
- Contact form (name, phone, business Instagram handle, message, ALTCHA) → `POST /public/leads`; direct links to Telegram, Bale and phone support; office address and eNamad.
- Affiliate page (Phase 2): commission explained in Toman, sign-up links to the panel's affiliate section.
- Case studies with verifiable metrics and seller consent flag (only published when consent is recorded in CMS).

### 4.8 Status page (Phase 2)
- Reads a public status endpoint: platform components, Instagram reachability from inside Iran (outage detector), incident history. Helps sellers during disruptions and reinforces the outage-proof positioning.

### 4.9 Waitlist (Phase 0 to closed beta)
- While in closed beta, «شروع رایگان» opens a waitlist form (phone, Instagram handle, vertical, monthly DM volume) → `POST /public/leads` with `type=waitlist`. Switch to direct sign-up by feature flag from the public config endpoint.

---

## 5. Phased work packages

### Phase 0 — Foundations (weeks 1–6)

| WP | Scope | Acceptance |
|---|---|---|
| WS-0.1 | App shell: route groups `(marketing)`, `(content)`, `(legal)`; header, footer, mobile nav, 404/500 in Persian; fonts; SEO base (metadata, robots, sitemap skeleton) | Lighthouse mobile ≥ 95 performance/accessibility/SEO on the shell |
| WS-0.2 | Landing page v0 with static content and waitlist form (4.1 sections 1, 3, 4, 8, 11; 4.9) | Waitlist submission reaches backend (MSW in CI) with ALTCHA verified |
| WS-0.3 | Legal pages including data deletion page (4.6) as static Markdown until the CMS exists | URLs ready for Meta app review submission |
| WS-0.4 | Umami analytics integration and event names (`cta_click`, `waitlist_submit`, `demo_message`) | No third-party requests in the network panel |

### Phase 1 — MVP and closed beta (months 2–5)

| WP | Scope | Acceptance |
|---|---|---|
| WS-1.1 | CMS integration: `BlockRenderer` for all block types, pages from CMS, preview mode with token, on-demand revalidation endpoint | Admin publish → page updated within 60 s |
| WS-1.2 | Full landing page (4.1) from CMS | LCP < 2.0 s on throttled mobile profile |
| WS-1.3 | Feature pages and solution pages (4.2, 4.3) | Each page has unique metadata and JSON-LD validated |
| WS-1.4 | Pricing page v1 (4.4 without calculator) | Prices rendered only from backend plans |
| WS-1.5 | Blog and help center (4.5) | Help search finds articles with Arabic ي/ك typed in the query |
| WS-1.6 | Live demo chat (4.1 section 2) | Rate limit and ALTCHA enforced; graceful Persian error when backend busy |
| WS-1.7 | Contact page, about page (4.7) | |
| WS-1.8 | Sign-up handoff to panel with plan/UTM/ref params; feature-flagged switch from waitlist to sign-up | Params arrive in panel sign-up URL |

### Phase 2 — Public launch (months 5–8)

| WP | Scope |
|---|---|
| WS-2.1 | Launch campaign landing and differentiator pages for receipt verification, payment links, voice/Finglish, outage-proof (remove «به‌زودی» badges by feature flag) |
| WS-2.2 | Pricing calculator, annual installments display, add-ons (4.4) |
| WS-2.3 | Case studies (4.7) |
| WS-2.4 | Affiliate page (4.7) |
| WS-2.5 | Status page (4.8) |
| WS-2.6 | eNamad seal integration and company details in footer and `Organization` schema |
| WS-2.7 | Performance and SEO audit, Core Web Vitals monitoring via Umami custom events |

### Phase 3 — Growth (months 8–14)

| WP | Scope |
|---|---|
| WS-3.1 | Agency landing and white-label/reseller explanation (G29) |
| WS-3.2 | Integrations directory (channels, PSPs, carriers, later accounting) |
| WS-3.3 | Developer docs for the public API and webhooks (rendered from OpenAPI, Persian guides) |
| WS-3.4 | Content studio and Jalali campaign planner feature pages; seasonal campaign landing pages (Yalda, Nowruz) |

### Phase 4 — Expansion (month 14+)

| WP | Scope |
|---|---|
| WS-4.1 | `fa-AF` (Dari) locale pages with `hreflang`, diaspora pricing display in foreign currencies (G17) |

---

## 6. Testing

- Unit: formatters, block renderer per block type, pricing calculator logic.
- E2E (Playwright + MSW): landing CTA flows, waitlist, demo chat (including rate-limit message), pricing toggle, help search with Persian variants, data deletion status lookup.
- Visual regression (Playwright screenshots) for landing and pricing in RTL at 360 px and 1280 px.
- Lighthouse CI on landing, pricing, a feature page and a blog post with the performance budget from section 2.
- Link checker on the sitemap in nightly CI.

## 7. Definition of done (website-specific additions)

- [ ] Page meets the performance budget and Lighthouse thresholds.
- [ ] Unique Persian metadata and valid JSON-LD.
- [ ] No third-party network requests except the first-party analytics path.
- [ ] Content editable from the CMS where the spec says so.
- [ ] Correct RTL at 360 px width, Persian digits in all numbers, Jalali dates.
