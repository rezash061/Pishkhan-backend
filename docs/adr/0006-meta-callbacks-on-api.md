# 0006. Meta deauthorize and data-deletion callbacks are served by the Api host

Status: accepted (2026-10-28)

The plan lists `POST /webhooks/instagram/data-deletion` on Ingress. Ingress has no database by design (it must answer webhooks even when PostgreSQL is slow), while both callbacks change or record data. They are served by the Api host under `/api/v1/public/meta/instagram/deauthorize` and `/data-deletion`, with the public status page at `GET /api/v1/public/meta/instagram/data-deletion/{code}`. Both verify Meta's `signed_request` with the app secret. Register these URLs in the Meta app settings.
