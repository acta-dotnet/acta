/* The Quarantined rows an operator command targets, read before the command changes any so the applying tick
   can record them as its plan: the ids it names, or every quarantined row when it names none. */
SELECT outbox_id
FROM {{table_ref}}
WHERE
    status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_outbox_ids IS NULL
        OR outbox_id IN (SELECT value::uuid FROM jsonb_array_elements_text(@p_outbox_ids::jsonb))
    );
