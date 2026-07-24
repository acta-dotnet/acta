/* Token-CAS set-based release of an unprocessed claimed group back to Pending, clearing the claim pair
   and leaving next_attempt_at_utc unchanged. Best-effort: lease expiry and token-CAS keep it safe. */
UPDATE {{table_ref}}
   SET status_code = 10 /* OutboxStatusCode.Pending */,
       claim_token = NULL,
       claim_until_utc = NULL
 WHERE claim_token = @p_claim_token
   AND status_code = 20 /* OutboxStatusCode.Claimed */
   AND outbox_id IN (SELECT CAST(value AS uniqueidentifier) FROM OPENJSON(@p_outbox_ids));
