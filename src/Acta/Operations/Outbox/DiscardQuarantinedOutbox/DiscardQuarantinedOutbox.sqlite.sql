/* Operator discard: the Quarantined row is deleted outright; the returned ids become ledger event
   evidence via the applying tick, so proof outlives the row. Only status 90 qualifies (an in-flight
   claimed row can never be deleted); a NULL id set discards every quarantined row. */
DELETE FROM {{table_ref}}
WHERE
    outbox_status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_outbox_ids IS NULL
        OR lower(replace(outbox_id, '-', '')) IN (SELECT lower(replace(value, '-', '')) FROM json_each(@p_outbox_ids))
    )
RETURNING outbox_id;
