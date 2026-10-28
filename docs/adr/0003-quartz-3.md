# 0003. Quartz.NET 3.x for recurring jobs

Status: accepted (2026-10-28)

Quartz 4.x changed its job and scheduling APIs and its ecosystem packages lagged. We use Quartz 3.22 (clustered AdoJobStore on PostgreSQL, tables `platform.qrtz_*` created by the Migrator). Delays tied to business events use the `platform.timers` table and `platform.timer-fired.v1` instead of Quartz. Revisit when 4.x is stable with the hosting and System.Text.Json packages.
