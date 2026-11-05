// Meta webhook burst against the Ingress host (BE-2.11): 2× the capacity plan's 2,000 events/s peak (backend plan 3.5), signed like Meta
// signs them. Ingress does no database work (verify, dedupe in Redis, produce to Kafka), so this measures the path a viral post hits.
//
//   k6 run -e BASE_URL=https://hooks.staging.example.ir -e APP_SECRET=... -e ACCOUNTS=17841400000000001,17841400000000002 webhook-burst.js
//
// RATE (events/s, default 4000), DURATION (default 5m), COMMENT_SHARE (0–1, default 0.6: a viral post is mostly comments). ACCOUNTS are
// Instagram account ids connected in the target environment; unknown ids are accepted by Ingress and dropped by the ingest workers.
import crypto from "k6/crypto";
import http from "k6/http";
import { check } from "k6";
import { Counter } from "k6/metrics";

const baseUrl = (__ENV.BASE_URL || "http://localhost:5081").replace(/\/$/, "");
const secret = __ENV.APP_SECRET || "dev-app-secret";
const rate = Number(__ENV.RATE || 4000);
const commentShare = Number(__ENV.COMMENT_SHARE || 0.6);
const accounts = (__ENV.ACCOUNTS || "17841400000000001").split(",");
const words = ["قیمت", "قیمت؟", "موجوده؟", "سایز", "price", "ghimat", "رنگ کرم", "ارسال دارید؟", "لینک", "خرید"];

const rejected = new Counter("webhooks_rejected");

export const options = {
  discardResponseBodies: true,
  scenarios: {
    burst: {
      executor: "ramping-arrival-rate",
      startRate: Math.round(rate / 10),
      timeUnit: "1s",
      preAllocatedVUs: Math.max(200, Math.round(rate / 10)),
      maxVUs: Math.max(1000, rate),
      stages: [
        { target: rate, duration: "30s" },
        { target: rate, duration: __ENV.DURATION || "5m" },
        { target: 0, duration: "15s" },
      ],
    },
  },
  thresholds: {
    // Backend plan 3.5: receive → 200 p99 < 150 ms.
    "http_req_duration{scenario:burst}": ["p(99)<150", "p(95)<80"],
    http_req_failed: ["rate<0.001"],
    checks: ["rate>0.999"],
  },
};

function pick(list) {
  return list[Math.floor(Math.random() * list.length)];
}

function event() {
  const account = pick(accounts);
  const now = Date.now();
  const id = `${__VU}-${__ITER}-${now}`;
  if (Math.random() < commentShare) {
    return {
      object: "instagram",
      entry: [{ id: account, time: Math.floor(now / 1000), changes: [{ field: "comments", value: {
        id: `c${id}`, text: pick(words), from: { id: `u${Math.floor(Math.random() * 1e6)}`, username: "load_user" }, media: { id: "1790000000000001", media_product_type: "FEED" },
      } }] }],
    };
  }
  return {
    object: "instagram",
    entry: [{ id: account, time: Math.floor(now / 1000), messaging: [{
      sender: { id: `u${Math.floor(Math.random() * 1e6)}` }, recipient: { id: account }, timestamp: now, message: { mid: `m${id}`, text: pick(words) },
    }] }],
  };
}

export default function () {
  const body = JSON.stringify(event());
  const signature = "sha256=" + crypto.hmac("sha256", secret, body, "hex");
  const res = http.post(`${baseUrl}/webhooks/instagram`, body, { headers: { "Content-Type": "application/json", "X-Hub-Signature-256": signature } });
  if (!check(res, { "200": (r) => r.status === 200 })) rejected.add(1);
}
