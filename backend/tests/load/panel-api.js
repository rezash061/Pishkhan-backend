// Panel API reads and writes against the Api host (BE-2.11), at 2× the expected busy-hour load of the capacity plan (10,000 workspaces).
// Backend plan 3.5: reads p95 < 200 ms, writes p95 < 400 ms.
//
//   k6 run -e BASE_URL=https://api.staging.example.ir -e TOKENS=ws1:token1,ws2:token2 panel-api.js
//
// TOKENS are workspace id : access token pairs of seeded load-test workspaces (members with the agent role). VUS (default 400 at peak)
// and DURATION (default 10m). WRITES=0 turns the note writes off (for a read-only replica check).
import http from "k6/http";
import { check, group, sleep } from "k6";

const baseUrl = (__ENV.BASE_URL || "http://localhost:5080").replace(/\/$/, "");
const pairs = (__ENV.TOKENS || "").split(",").filter(Boolean).map((p) => p.split(":"));
const peak = Number(__ENV.VUS || 400);
const writes = __ENV.WRITES !== "0";

export const options = {
  scenarios: {
    agents: {
      executor: "ramping-vus",
      startVUs: 0,
      stages: [
        { target: peak, duration: "2m" },
        { target: peak, duration: __ENV.DURATION || "10m" },
        { target: 0, duration: "30s" },
      ],
      gracefulRampDown: "30s",
    },
  },
  thresholds: {
    "http_req_duration{kind:read}": ["p(95)<200"],
    "http_req_duration{kind:write}": ["p(95)<400"],
    http_req_failed: ["rate<0.005"],
  },
};

export function setup() {
  if (!pairs.length) throw new Error("TOKENS is required: workspaceId:accessToken[,…]");
}

const read = (url, token) => http.get(url, { headers: { Authorization: `Bearer ${token}` }, tags: { kind: "read" } });

// One agent's minute in the inbox: the list, a conversation and its messages, the notifications, now and then orders and the dashboard.
export default function () {
  const [ws, token] = pairs[(__VU - 1) % pairs.length];
  const api = `${baseUrl}/api/v1/workspaces/${ws}`;
  let conversationId = null;
  group("inbox", () => {
    const list = read(`${api}/conversations?status=open&limit=30`, token);
    check(list, { "list 200": (r) => r.status === 200 });
    const items = list.status === 200 ? list.json("items") : [];
    if (items && items.length) {
      conversationId = items[Math.floor(Math.random() * items.length)].id;
      check(read(`${api}/conversations/${conversationId}`, token), { "conversation 200": (r) => r.status === 200 });
      check(read(`${api}/conversations/${conversationId}/messages?limit=50`, token), { "messages 200": (r) => r.status === 200 });
    }
    check(read(`${api}/notifications`, token), { "notifications 200": (r) => r.status === 200 });
  });
  if (Math.random() < 0.2) {
    group("orders", () => check(read(`${api}/orders?limit=30`, token), { "orders 200": (r) => r.status === 200 }));
  }
  if (Math.random() < 0.05) {
    group("dashboard", () => check(read(`${api}/dashboard?range=7d`, token), { "dashboard 200": (r) => r.status === 200 }));
  }
  if (writes && conversationId && Math.random() < 0.1) {
    group("note", () => {
      const res = http.post(`${api}/conversations/${conversationId}/notes`, JSON.stringify({ text: "یادداشت تست بار" }), {
        headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
        tags: { kind: "write" },
      });
      check(res, { "note 2xx": (r) => r.status >= 200 && r.status < 300 });
    });
  }
  sleep(2 + Math.random() * 4);
}
