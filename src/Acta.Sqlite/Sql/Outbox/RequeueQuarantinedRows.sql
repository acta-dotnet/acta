/* Operator requeue: back to Pending, due now, failure budget reset, last_error kept as evidence. The
   status filter is the whole guard (quarantined rows are never claimed, so no token CAS applies); a NULL
   id set requeues every quarantined row. The returned ids are the applying tick's event evidence. */
UPDATE {{table_ref}}
SET
    status_code = 10 /* OutboxStatusCode.Pending */,
    failure_count = 0,
    next_attempt_at_utc = STRFTIME('%Y-%m-%d %H:%M:%f', 'now')
WHERE
    status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_outbox_ids IS NULL
        OR EXISTS (SELECT 1 FROM json_each(@p_outbox_ids) j WHERE j.value = outbox_id COLLATE NOCASE)
    )
RETURNING outbox_id;
