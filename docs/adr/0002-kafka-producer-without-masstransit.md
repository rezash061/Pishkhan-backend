# 0002. Produce to Kafka directly; consume through the MassTransit rider

Status: accepted (2026-10-28)

Context: the plan uses MassTransit 8.5 for Kafka. The MassTransit Kafka rider binds a producer to one topic and message type at configuration time. The outbox relay reads rows for any topic and must produce raw envelopes it does not deserialize; Ingress produces without a database.

Decision: producing uses `IKafkaProducer`, a thin wrapper over Confluent.Kafka (idempotent, acks=all, linger 5 ms), used by the outbox relay, the DLQ path and Ingress. Consuming uses the MassTransit rider (`TopicEndpoint<string, EventEnvelope>`, concurrency 1 per key) with our own envelope consumer for retries, DLQ, tracing and tenant scope.

Consequences: one less abstraction on the write path and full control of headers and keys; we keep MassTransit's consumer hosting, checkpointing and partition handling. Staying on MassTransit 8.x (license) is unaffected.
