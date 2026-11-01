-- Exact rows per table of a Pishkhan database. restore.sh runs it on the restored copy; run it on the source at the dump's time (or
-- compare with the source's monitoring) to check the restore. Each module's __ef_migrations_history is listed too, so a missing
-- migration shows as a lower count.
\pset footer off
SELECT table_schema AS schema, table_name AS "table",
       (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', table_schema, table_name), false, true, '')))[1]::text::bigint AS rows
FROM information_schema.tables
WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')
ORDER BY 1, 2;
