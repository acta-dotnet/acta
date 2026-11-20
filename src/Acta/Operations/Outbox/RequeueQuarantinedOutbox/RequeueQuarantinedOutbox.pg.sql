/* Operator requeue: back to Pending, due now, failure budget reset, failure_message kept as evidence. The
   status filter is the whole guard (quarantined rows are never claimed, so no token CAS applies); a NULL
   id set requeues every quarantined row. The returned ids are the applying tick's event evidence. */
UPDATE {{table_ref}}
SET
    outbox_status_code = 10 /* OutboxStatusCode.Pending */,
    failure_count = 0,
    next_attempt_at_utc = now()
WHERE
    outbox_status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_job_refs IS NULL
        OR job_ref IN (SELECT value::uuid FROM jsonb_array_elements_text(@p_job_refs::jsonb))
    )
RETURNING job_ref;
