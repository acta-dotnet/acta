/* Token-CAS set-based release of an unprocessed claimed group back to Pending, clearing the claim pair
   and leaving next_attempt_at_utc unchanged. Best-effort: lease expiry and token-CAS keep it safe. */
-- Seeks by key, so a producer's uncommitted rows never hold it up (docs/sql-internals.md, "Locking reads seek").
UPDATE o
SET
    status_code = 10 /* OutboxStatusCode.Pending */,
    claim_token = NULL,
    claim_until_utc = NULL
FROM {{table_ref}} AS o WITH (FORCESEEK)
WHERE
    o.claim_token = @p_claim_token
    AND o.status_code = 20 /* OutboxStatusCode.Claimed */
    AND o.outbox_id IN (SELECT CAST(value AS UNIQUEIDENTIFIER) FROM OPENJSON(@p_outbox_ids));
