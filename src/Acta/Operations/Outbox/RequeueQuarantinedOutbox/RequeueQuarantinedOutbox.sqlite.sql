/* Operator requeue: back to Pending, due now, failure budget reset, failure_message kept as evidence. The
   status filter is the whole guard (quarantined rows are never claimed, so no token CAS applies); a NULL
   id set requeues every quarantined row. The returned ids are the applying tick's event evidence. */
UPDATE {{table_ref}}
SET
    outbox_status_code = 10 /* OutboxStatusCode.Pending */,
    failure_count = 0,
    next_attempt_at_utc = STRFTIME('%Y-%m-%d %H:%M:%f', 'now')
WHERE
    outbox_status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_job_refs IS NULL
        OR lower(replace(job_ref, '-', '')) IN (SELECT lower(replace(value, '-', '')) FROM json_each(@p_job_refs))
    )
RETURNING job_ref;
