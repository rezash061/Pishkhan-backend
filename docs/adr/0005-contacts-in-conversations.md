# 0005. Contacts live in the Conversations module until CRM exists

Status: accepted (2026-10-28), temporary

Ingest must create a contact and channel identity for each new Instagram user. The CRM module is Phase 1. Until then `contacts` and `contacts_channel_identities` are tables of the `conversations` schema. When CRM lands, the tables move to its schema with a data migration, and Conversations keeps only `contact_id`.
