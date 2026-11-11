/* Token-CAS set-based quarantine of a claimed group in one round trip: exclude each row from normal claims
   (status 90), clear the claim pair, and record its own consumed failure count and bounded error, so a
   coalesced group can partially quarantine. A row whose token no longer matches changes nothing. */
UPDATE {{table_ref}} AS o
SET
    outbox_status_code = 90 /* OutboxStatusCode.Quarantined */,
    claim_token = NULL,
    claim_until_utc = NULL,
    failure_count = r.failure_count,
    failure_message = r.failure_message
FROM
    jsonb_to_recordset(@p_rows::jsonb)
    AS r (outbox_id uuid, failure_count integer, failure_message varchar(512))
WHERE
    o.outbox_id = r.outbox_id
    AND o.claim_token = @p_claim_token
    AND o.outbox_status_code = 20 /* OutboxStatusCode.Claimed */;
