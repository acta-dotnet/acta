/* Operator requeue: back to Pending, due now, failure budget reset, last_error kept as evidence. The
   status filter is the whole guard (quarantined rows are never claimed, so no token CAS applies); a NULL
   id set requeues every quarantined row. The returned ids are the applying tick's event evidence. */
-- Seeks by key, so a producer's uncommitted rows never hold it up (docs/sql-internals.md, "Locking reads seek").
UPDATE o
SET
    status_code = 10 /* OutboxStatusCode.Pending */,
    failure_count = 0,
    next_attempt_at_utc = SYSUTCDATETIME()
OUTPUT INSERTED.outbox_id
FROM {{table_ref}} AS o WITH (FORCESEEK)
WHERE
    o.status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_outbox_ids IS NULL
        OR o.outbox_id IN (SELECT CAST(value AS UNIQUEIDENTIFIER) FROM OPENJSON(@p_outbox_ids))
    );
