-- Seeks the Quarantined rows, which no producer holds (docs/sql-internals.md, "Locking reads seek").
SELECT COUNT(*)
FROM {{table_ref}} WITH (FORCESEEK)
WHERE status_code = 90 /* OutboxStatusCode.Quarantined */;
