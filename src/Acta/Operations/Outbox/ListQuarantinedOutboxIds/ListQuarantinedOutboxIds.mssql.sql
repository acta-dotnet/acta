/* The Quarantined rows an operator command targets, read before the command changes any so the applying tick
   can record them as its plan: the ids it names, or every quarantined row when it names none. */
-- Seeks rather than skips: no producer holds a Quarantined row, and a skipped one would be missing from the plan.
SELECT job_ref
FROM {{table_ref}} WITH (FORCESEEK)
WHERE
    outbox_status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_job_refs IS NULL
        OR job_ref IN (SELECT CAST(value AS UNIQUEIDENTIFIER) FROM OPENJSON(@p_job_refs))
    );
