/* Token-CAS set-based quarantine of a claimed group in one round trip: exclude each row from normal claims
   (status 90), clear the claim pair, and record its own consumed failure count and bounded error, so a
   coalesced group can partially quarantine. A row whose token no longer matches changes nothing. */
UPDATE {{table_ref}} AS o
   SET status_code = 90 /* OutboxStatusCode.Quarantined */,
       claim_token = NULL,
       claim_until_utc = NULL,
       failure_count = r.failure_count,
       last_error = r.last_error
  FROM jsonb_to_recordset(@p_rows::jsonb)
       AS r(outbox_id uuid, failure_count integer, last_error varchar(512))
 WHERE o.outbox_id = r.outbox_id
   AND o.claim_token = @p_claim_token
   AND o.status_code = 20 /* OutboxStatusCode.Claimed */;
