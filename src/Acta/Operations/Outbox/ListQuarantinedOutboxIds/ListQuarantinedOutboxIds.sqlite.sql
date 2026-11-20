/* The Quarantined rows an operator command targets, read before the command changes any so the applying tick
   can record them as its plan: the ids it names, or every quarantined row when it names none. */
SELECT job_ref
FROM {{table_ref}}
WHERE
    outbox_status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_job_refs IS NULL
        OR lower(replace(job_ref, '-', '')) IN (SELECT lower(replace(value, '-', '')) FROM json_each(@p_job_refs))
    );
