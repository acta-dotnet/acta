/* Token-CAS set-based reschedule of a recoverable claimed group: each row returns to Pending with its own
   bumped failure count, per-row backoff added to the source clock (SYSUTCDATETIME), and bounded error; the
   claim pair clears. A row whose token no longer matches changes nothing. */
UPDATE o
SET
    status_code = 10 /* OutboxStatusCode.Pending */,
    claim_token = NULL,
    claim_until_utc = NULL,
    failure_count = r.failure_count,
    next_attempt_at_utc = DATEADD(SECOND, r.backoff_seconds, SYSUTCDATETIME()),
    last_error = r.last_error
FROM {{table_ref}} AS o
INNER JOIN
    OPENJSON (@p_rows)
    WITH (
        outbox_id UNIQUEIDENTIFIER '$.outbox_id',
        failure_count INT '$.failure_count',
        backoff_seconds INT '$.backoff_seconds',
        last_error VARCHAR(512) '$.last_error'
    ) AS r
    ON o.outbox_id = r.outbox_id
WHERE o.claim_token = @p_claim_token AND o.status_code = 20 /* OutboxStatusCode.Claimed */;
