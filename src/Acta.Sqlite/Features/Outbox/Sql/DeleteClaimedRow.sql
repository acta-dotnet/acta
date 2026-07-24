/* Token-CAS set-based delete of a safely-ingested claimed group in one round trip. A stale token
   matches nothing and deletes nothing. */
DELETE FROM {{table_ref}}
 WHERE claim_token = @p_claim_token COLLATE NOCASE
   AND EXISTS (SELECT 1 FROM json_each(@p_outbox_ids) j WHERE j.value = outbox_id COLLATE NOCASE);
