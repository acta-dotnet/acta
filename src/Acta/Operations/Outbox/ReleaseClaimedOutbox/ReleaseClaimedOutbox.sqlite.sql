/* Token-CAS set-based release of an unprocessed claimed group back to Pending, clearing the claim pair
   and leaving next_attempt_at_utc unchanged. Best-effort: lease expiry and token-CAS keep it safe. */
UPDATE {{table_ref}}
SET
    outbox_status_code = 10 /* OutboxStatusCode.Pending */,
    claim_token = NULL,
    claim_until_utc = NULL
WHERE
    claim_token = @p_claim_token COLLATE NOCASE
    AND outbox_status_code = 20 /* OutboxStatusCode.Claimed */
    AND lower(replace(outbox_id, '-', '')) IN (SELECT lower(replace(value, '-', '')) FROM json_each(@p_outbox_ids));
