/* The Quarantined rows an operator command targets, read before the command changes any so the applying tick
   can record them as its plan: the ids it names, or every quarantined row when it names none. */
SELECT job_ref
FROM {{table_ref}}
WHERE
    outbox_status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_job_refs IS NULL
        OR job_ref IN (SELECT value::uuid FROM jsonb_array_elements_text(@p_job_refs::jsonb))
    );
