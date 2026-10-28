# 0001. Modular monolith with separate hosts and Kafka

Status: accepted (2026-10-28)

The backend is one codebase with one schema per module, deployed as five hosts (Api, AdminApi, Ingress, Workers per group, Migrator). Modules talk through their `*.Contracts` projects (in-process interfaces, DTOs) and Kafka events written through a transactional outbox. The architecture tests enforce that no module references another module's implementation, that Contracts projects stay free of infrastructure, and that every tenant-owned entity has the tenant query filter. See backend plan section 1 for the alternatives considered.
