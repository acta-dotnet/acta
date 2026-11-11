/* Token-CAS set-based quarantine of a claimed group (status 90): each row clears its claim pair and records
   its own consumed failure count and bounded error, so a coalesced group can partially quarantine. Subquery
   columns are r_-prefixed so unqualified target columns resolve to the outbox table under UPDATE FROM. */
UPDATE {{table_ref}}
SET
    outbox_status_code = 90 /* OutboxStatusCode.Quarantined */,
    claim_token = NULL,
    claim_until_utc = NULL,
    failure_count = r.r_failure_count,
    failure_message = r.r_failure_message
FROM (
    SELECT
        json_extract(value, '$.outbox_id') AS r_outbox_id,
        json_extract(value, '$.failure_count') AS r_failure_count,
        json_extract(value, '$.failure_message') AS r_failure_message
    FROM json_each(@p_rows)
) AS r
WHERE
    lower(replace(outbox_id, '-', '')) = lower(replace(r.r_outbox_id, '-', ''))
    AND claim_token = @p_claim_token COLLATE NOCASE
    AND outbox_status_code = 20 /* OutboxStatusCode.Claimed */;
