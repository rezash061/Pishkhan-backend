# Kafka lag, outbox backlog and DLQs

## How it shows

- Alert: consumer lag > 30 s for a group, outbox backlog > 1,000, or `pishkhan.messaging.dead_lettered` growing.
- Sellers see slow replies (the `engine`, `ai` or `outbound` group lags), a bell that doesn't update (`realtime`), or uploads stuck (`media`).

Consumer groups are named after the worker groups: `ingest`, `engine`, `ai`, `outbound`, `media`, `commerce`, `platform`, `realtime`. Each has a DLQ topic `<group>.dlq`.

```bash
kafka-consumer-groups.sh --bootstrap-server $KAFKA --describe --group engine        # LAG per partition
kafka-consumer-groups.sh --bootstrap-server $KAFKA --list
```

Outbox backlog (events written by the modules and not yet produced to Kafka):

```sql
SELECT shard, count(*), min(created_at) FROM platform.outbox_messages WHERE published_at IS NULL GROUP BY shard ORDER BY shard;
```

## Which one is it

| Symptom | Likely cause | Action |
|---|---|---|
| Outbox backlog grows, Kafka lag is low | Relay not running: the `platform` worker is down, or Kafka is unreachable from it | Check the `platform` deployment and `/health/ready` (kafka). The relay holds one advisory lock per shard, so running two replicas is safe. |
| Lag in one group, the others fine | That group is slow or crashed | Check its pods and logs. Scale it out, up to its topic's partition count (for example `engine` reads 48-partition topics). |
| Lag on a few partitions only | A hot key (one viral post, one busy conversation) or a slow handler | Expected during a viral post: comments are keyed by media id, so one post's comments run in order on one partition. Pacing ("viral mode") spreads the replies out. Do not repartition during the incident. |
| `ai` lags | The AI provider is slow or failing | Check `pishkhan.ai.latency` and the provider's status. The reply pipeline hands off to a person when the model fails, so customers are not left silent. Lag here means slower answers, not lost ones. |
| DLQ grows | A handler throws after its retries | Read the DLQ message: it carries the topic, group, handler, exception and attempts. Fix the cause first, then replay. |

## Replaying a DLQ

Handlers are idempotent. Most use their module's inbox table, which drops a message id already processed. The outbound sender only sends a message that is still queued. So replaying a whole DLQ is safe: a message that was in fact handled is skipped.

1. Fix the cause and deploy.
2. Read the DLQ (each message is the original envelope, with `deadLetter` added):
   ```bash
   kafka-console-consumer.sh --bootstrap-server $KAFKA --topic outbound.dlq --from-beginning --property print.key=true --max-messages 20
   ```
3. Produce each envelope back to the topic named in `deadLetter.topic`, with the same key. Remove `deadLetter` first, or keep it: consumers ignore it. Keep the key, because it decides the partition and the order.
4. Watch the group's lag and the DLQ counter.

Old messages are rechecked against the current state when they are replayed: the window, the conversation status, the plan quota. So a replay never sends something the policy now forbids.

## Do not

- Do not reset a group's offsets to "latest" to clear lag. That drops messages: inbound DMs would never reach the inbox.
- Do not delete outbox rows that have not been published.
