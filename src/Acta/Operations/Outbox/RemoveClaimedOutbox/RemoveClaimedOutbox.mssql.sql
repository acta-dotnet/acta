/* Token-CAS set-based delete of a safely-ingested claimed group in one round trip. A stale token
   matches nothing and deletes nothing. */
-- Seeks by key, so a producer's uncommitted rows never hold it up (docs/sql-internals.md, "Locking reads seek").
DELETE o
FROM {{table_ref}} AS o WITH (FORCESEEK)
WHERE
    o.claim_token = @p_claim_token
    AND o.status_code = 20 /* OutboxStatusCode.Claimed */
    AND o.outbox_id IN (SELECT CAST(value AS UNIQUEIDENTIFIER) FROM OPENJSON(@p_outbox_ids));
