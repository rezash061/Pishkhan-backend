# Meta outage (Instagram API or webhooks down)

## How it shows

- `pishkhan.instagram.api.errors` jumps for every account at once. Codes `1`, `2` (Meta's "temporary" errors) or HTTP 5xx and timeouts point to Meta, while `190` on one account is just that account's token.
- Outbound messages stay in `queued` or `sending` longer than usual, and the inbox shows retries.
- Inbound stops: no new `channels.instagram-webhook-received.v1` messages, while Ingress is healthy.
- Check <https://metastatus.com> and the Meta developer status page before touching anything.

First decide whether it is **Meta**, **our route to Meta** (the international edge where Ingress and the `outbound` group run, backend plan 2.4), or **one account**:

| Check | Meta | Our edge | One account |
|---|---|---|---|
| metastatus.com | incident | green | green |
| `curl -sS https://graph.instagram.com/` from an outbound pod | slow or 5xx | fails (DNS, TLS, timeout) | fine |
| Errors spread over accounts | all | all | one |

## What the system already does

- **Sends retry.** A retryable failure (5xx, timeout, Meta's temporary codes) is retried up to 6 times with backoff from 1 s to 60 s, inside the `outbound` group. A send that still fails after that goes to `outbound.dlq`. The message is not sent and stays queued until the DLQ is replayed. Rate-limit refusals are not failures: they wait in a timer until the bucket refills.
- **The 24-hour window is checked at send time.** A message retried after the window closed is refused with the window error, not sent against policy.
- **Comment private replies** follow the same retry path (6 attempts), one private reply per comment.
- **Token refresh** (`platform` job, daily) treats an unreachable Meta as a failed attempt (`instagram.unreachable`), not as an invalid token. It does not disconnect accounts. Only Meta's "invalid token" answer moves an account to `reauth_required`.
- **Webhooks.** Meta redelivers webhooks it could not deliver, for a limited time. Ingress dedupes each webhook entry by its content hash in Redis, so a burst of redeliveries after recovery is safe. When Kafka refuses a webhook, Ingress answers 5xx so that Meta retries.

## What to do

1. **Meta incident:** do nothing to the data. Post the status banner ("Instagram is having problems; messages will be sent when it recovers"). Watch `outbound.dlq`.
2. **Our edge:** fix the route (DNS, egress proxy, firewall, certificate). Sends retry by themselves for about 2 minutes. Anything older is in `outbound.dlq`: replay it after the route works (see [Kafka lag](kafka-lag.md#replaying-a-dlq)). Replaying is safe because the sender only sends a message that is still queued, and the window check runs again.
3. **One account:** if it is `reauth_required`, the seller reconnects from Channels. The panel already shows the banner, and the owner gets a notification.
4. **After recovery:**
   - Check that `pishkhan.instagram.api.errors` is back to normal and `outbound.dlq` is empty or replayed.
   - Do not run a backlog job just to make up for missed comments. Meta's private-reply age limit applies, and a late reply to a stale comment looks broken. Let sellers decide from the post's backlog screen.
   - If many accounts went to `reauth_required` during the incident because of wrong `190` answers, find them in the admin panel's workspace view and ask the sellers to reconnect.

## Do not

- Do not raise the account rate profiles to "catch up". Meta counts the burst, and a viral post can get the account limited.
- Do not mark failed messages as sent.
