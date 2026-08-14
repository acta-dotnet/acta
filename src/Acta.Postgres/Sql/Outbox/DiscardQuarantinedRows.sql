/* Operator discard: the Quarantined row is deleted outright; the returned ids become ledger event
   evidence via the applying tick, so proof outlives the row. Only status 90 qualifies (an in-flight
   claimed row can never be deleted); a NULL id set discards every quarantined row. */
DELETE FROM {{table_ref}}
WHERE
    status_code = 90 /* OutboxStatusCode.Quarantined */
    AND (
        @p_outbox_ids IS NULL
        OR outbox_id IN (SELECT value::uuid FROM jsonb_array_elements_text(@p_outbox_ids::jsonb))
    )
RETURNING outbox_id;
