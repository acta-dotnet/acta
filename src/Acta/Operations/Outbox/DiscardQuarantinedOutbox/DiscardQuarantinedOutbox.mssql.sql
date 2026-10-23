/* Operator discard: the Quarantined row is deleted outright; the returned ids become ledger event
   evidence via the applying tick, so proof outlives the row. Only status 90 qualifies (an in-flight
   claimed row can never be deleted); a NULL id set discards every quarantined row. */
-- Seeks by key, so a producer's uncommitted rows never hold it up (docs/sql-internals.md, "Locking reads seek").
DELETE o
OUTPUT DELETED.outbox_id
FROM {{table_ref}} AS o WITH (FORCESEEK)
WHERE
    o.status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_outbox_ids IS NULL
        OR o.outbox_id IN (SELECT CAST(value AS UNIQUEIDENTIFIER) FROM OPENJSON(@p_outbox_ids))
    );
