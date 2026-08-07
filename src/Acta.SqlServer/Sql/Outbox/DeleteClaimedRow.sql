/* Token-CAS set-based delete of a safely-ingested claimed group in one round trip. A stale token
   matches nothing and deletes nothing. */
DELETE FROM {{table_ref}}
WHERE
    claim_token = @p_claim_token
    AND outbox_id IN (SELECT CAST(value AS UNIQUEIDENTIFIER) FROM OPENJSON(@p_outbox_ids));
