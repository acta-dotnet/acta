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
        @p_outbox_ids IS NULL
        OR lower(replace(outbox_id, '-', '')) IN (SELECT lower(replace(value, '-', '')) FROM json_each(@p_outbox_ids))
    )
RETURNING outbox_id;
