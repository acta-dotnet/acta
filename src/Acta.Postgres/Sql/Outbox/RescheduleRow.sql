/* Token-CAS set-based reschedule of a recoverable claimed group: each row returns to Pending with its own
   bumped failure count, per-row backoff added to the source clock (now()), and bounded error; the claim pair
   clears. A row whose token no longer matches changes nothing. */
UPDATE {{table_ref}} AS o
SET
    status_code = 10 /* OutboxStatusCode.Pending */,
    claim_token = NULL,
    claim_until_utc = NULL,
    failure_count = r.failure_count,
    next_attempt_at_utc = now() + (r.backoff_seconds * INTERVAL '1 second'),
    last_error = r.last_error
FROM
    jsonb_to_recordset(@p_rows::jsonb)
    AS r (outbox_id uuid, failure_count integer, backoff_seconds integer, last_error varchar(512))
WHERE
    o.outbox_id = r.outbox_id
    AND o.claim_token = @p_claim_token
    AND o.status_code = 20 /* OutboxStatusCode.Claimed */;
