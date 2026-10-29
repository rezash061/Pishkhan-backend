# 0009. Automation decides, Conversations applies, pacing reserves slots

Status: accepted (2026-10-29)

The Automation module evaluates rules and flows but does not write conversation data. It publishes one `conversations.actions-requested.v1` event per decision (messages to queue, private reply, tags, fields, handoff, AI pause), keyed by channel account and customer. Conversations applies it in one transaction and queues the outbound messages, creating the contact and conversation for a commenter who never wrote a DM. This keeps one owner per table at the cost of one extra Kafka hop.

Comment replies are paced per channel account. Each reply reserves the next free slot in `automation.pacing` (an upsert under a row lock, so replicas and partitions share it); the slot spacing is the rate profile's refill interval plus 10%. A reply whose slot is in the future waits in `platform.timers`. The send-time token bucket stays the hard limit: a send it refuses is deferred through a timer (up to 100 times) rather than retried and failed. Meta's one-private-reply rule is enforced twice, by `automation.comment_events` (one decision per comment) and by `conversations.private_reply_keys` (one send per comment).
