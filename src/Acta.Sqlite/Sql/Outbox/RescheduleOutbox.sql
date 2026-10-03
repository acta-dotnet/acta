/* Token-CAS set-based reschedule of a recoverable claimed group: each row returns to Pending with its own
   bumped failure count, per-row backoff added to the source clock (strftime 'now'), and bounded error. The
   subquery columns are r_-prefixed so unqualified target columns resolve to the outbox table under UPDATE FROM. */
UPDATE {{table_ref}}
SET
    status_code = 10 /* OutboxStatusCode.Pending */,
    claim_token = NULL,
    claim_until_utc = NULL,
    failure_count = r.r_failure_count,
    next_attempt_at_utc = strftime('%Y-%m-%d %H:%M:%f', 'now', '+' || CAST(r.r_backoff_seconds AS TEXT) || ' seconds'),
    last_error = r.r_last_error
FROM (
    SELECT
        json_extract(value, '$.outbox_id') AS r_outbox_id,
        json_extract(value, '$.failure_count') AS r_failure_count,
        json_extract(value, '$.backoff_seconds') AS r_backoff_seconds,
        json_extract(value, '$.last_error') AS r_last_error
    FROM json_each(@p_rows)
) AS r
WHERE
    outbox_id = r.r_outbox_id COLLATE NOCASE
    AND claim_token = @p_claim_token COLLATE NOCASE
    AND status_code = 20 /* OutboxStatusCode.Claimed */;
