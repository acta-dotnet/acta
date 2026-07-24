/* Token-CAS set-based quarantine of a claimed group in one round trip: exclude each row from normal claims
   (status 90), clear the claim pair, and record its own consumed failure count and bounded error, so a
   coalesced group can partially quarantine. A row whose token no longer matches changes nothing. */
UPDATE o
   SET status_code = 90 /* OutboxStatusCode.Quarantined */,
       claim_token = NULL,
       claim_until_utc = NULL,
       failure_count = r.failure_count,
       last_error = r.last_error
  FROM {{table_ref}} AS o
  INNER JOIN OPENJSON(@p_rows)
       WITH (
           outbox_id uniqueidentifier '$.outbox_id',
           failure_count int '$.failure_count',
           last_error varchar(512) '$.last_error'
       ) AS r ON o.outbox_id = r.outbox_id
 WHERE o.claim_token = @p_claim_token AND o.status_code = 20 /* OutboxStatusCode.Claimed */;
