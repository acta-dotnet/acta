/* Token-CAS set-based delete of a safely-ingested claimed group in one round trip. A stale token
   matches nothing and deletes nothing. Only Claimed rows can carry a token, so status_code seeks
   ix_{table}_claims and the delete reads the rows in flight, never the backlog. */
DELETE FROM {{table_ref}}
WHERE
    status_code = 20 /* OutboxStatusCode.Claimed */
    AND claim_token = @p_claim_token COLLATE NOCASE
    AND outbox_id COLLATE NOCASE IN (SELECT value FROM json_each(@p_outbox_ids));
